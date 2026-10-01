using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;

namespace SqlOS.Tests.Processes;

/// <summary>
/// The MFA processes: <see cref="VerifyMfaChallenge"/> (a second factor answers a challenge),
/// <see cref="StartTotpEnrollment"/> and <see cref="VerifyTotpEnrollment"/> (an authenticator is
/// enrolled for an account or for the challenge that requires one), and
/// <see cref="VerifySecondFactor"/>. The hosted AuthPage, the headless API and the public API run
/// them; the login they complete is the hub's (<see cref="ILoginCompletion"/>).
/// </summary>
[TestClass]
public sealed class MfaProcessTests
{
    [TestMethod]
    public async Task A_right_authenticator_code_completes_the_direct_login_with_two_factor_evidence()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var (user, secret) = await EnrolledUserAsync(harness);
        var challenge = await DirectLoginChallengeAsync(harness, user);

        var outcome = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(challenge, NextCode(harness, secret), MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);

        var signedIn = outcome.Should().BeOfType<MfaChallengeOutcome.SignedIn>().Subject;
        signedIn.Evidence.UserId.Should().Be(user.Id);
        signedIn.Evidence.Methods.Should().Equal("password", "totp");
        signedIn.Evidence.AuthenticationMethod.Should().Be("password+totp");
        signedIn.Evidence.Assurance.Should().Be(LoginAssurance.MultiFactor);
        signedIn.Evidence.Proofs.Should().BeEmpty();
        signedIn.Completion.Should().BeOfType<LoginCompletion.TokensIssued>().Which.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        (await ChallengeAsync(harness, challenge)).ConsumedAt.Should().NotBeNull("the challenge is spent with the login");
        (await harness.AuditAsync("user.login.mfa")).Should().ContainSingle().Which.UserId.Should().Be(user.Id);

        var replay = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(challenge, NextCode(harness, secret), MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);
        replay.Should().BeOfType<MfaChallengeOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.Invalid);
    }

    [TestMethod]
    public async Task A_recovery_code_answers_one_challenge_only()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var (user, _, recoveryCodes) = await EnrolledUserWithRecoveryCodesAsync(harness);
        var recoveryCode = recoveryCodes.First();

        var first = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(await DirectLoginChallengeAsync(harness, user), recoveryCode, MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);
        first.Should().BeOfType<MfaChallengeOutcome.SignedIn>().Which.Evidence.Methods.Should().Equal("password", "recovery_code");

        var second = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(await DirectLoginChallengeAsync(harness, user), recoveryCode, MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);
        second.Should().BeOfType<MfaChallengeOutcome.Refused>().Which.Refusal.Message.Should().Be("MFA code is invalid.");
    }

    [TestMethod]
    public async Task Wrong_factors_are_counted_and_the_last_allowed_one_withdraws_the_challenge()
    {
        var harness = await IdentityProcessHarness.CreateAsync(options =>
        {
            RequireMfa(options);
            options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 2;
        });
        var (user, secret) = await EnrolledUserAsync(harness);
        var challenge = await DirectLoginChallengeAsync(harness, user);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var wrong = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
                new VerifyMfaChallengeCommand(challenge, "wrong-code", MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
                CancellationToken.None);
            wrong.Should().BeOfType<MfaChallengeOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.CodeInvalid);
        }

        var stored = await ChallengeAsync(harness, challenge);
        stored.ConsumedAt.Should().NotBeNull("the second failure reached the limit and withdrew the challenge");
        stored.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)!.FailedAttempts.Should().Be(2);
        var failures = await harness.AuditAsync("user.mfa.challenge_failed");
        failures.Select(row => row.MetadataJson).Should().Equal(
            $"{{\"challengeId\":\"{stored.Id}\",\"details\":{{\"attemptCount\":1,\"challengeLocked\":false}}}}",
            $"{{\"challengeId\":\"{stored.Id}\",\"details\":{{\"attemptCount\":2,\"challengeLocked\":true}}}}");
        failures.Should().OnlyContain(row => row.UserId == user.Id && row.IpAddress == IdentityProcessHarness.IpAddress);

        var right = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(challenge, NextCode(harness, secret), MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);
        right.Should().BeOfType<MfaChallengeOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.Invalid);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task Each_surface_answers_only_the_challenges_of_its_own_login()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var (user, secret) = await EnrolledUserAsync(harness);
        var directLoginChallenge = await DirectLoginChallengeAsync(harness, user);

        var asAuthorization = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(directLoginChallenge, NextCode(harness, secret), MfaChallengeTarget.AuthorizationRequest, IdentityProcessHarness.Request),
            CancellationToken.None);

        asAuthorization.Should().BeOfType<MfaChallengeOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("MFA challenge is not valid for hosted authorization.");
        (await ChallengeAsync(harness, directLoginChallenge)).ConsumedAt.Should().BeNull();
        (await harness.AuditAsync("user.mfa.challenge_failed")).Should().BeEmpty("a challenge of another login compares no factor");
    }

    [TestMethod]
    public async Task A_challenge_that_requires_enrollment_is_not_answered_with_a_factor()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var user = await harness.CreateUserAsync();
        var challenge = await DirectLoginChallengeAsync(harness, user, expectEnrollment: true);

        var outcome = await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(challenge, "123456", MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);

        outcome.Should().BeOfType<MfaChallengeOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.EnrollmentPending);
        (await ChallengeAsync(harness, challenge)).ConsumedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task A_third_party_clients_direct_login_challenge_is_refused_before_its_factor_is_checked()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var (user, secret) = await EnrolledUserAsync(harness);
        // A challenge a third-party client could hold only if it was issued before the direct-login gate (#419).
        var partner = await harness.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == IdentityProcessHarness.ThirdPartyClientId);
        var issued = SqlOSTemporaryToken.Issue(
            SqlOSTemporaryTokenKinds.MfaChallenge,
            new SqlOSMfaChallengePayload(MfaChallenges.Client, partner.ClientId, "password"),
            new TemporaryTokenBinding(UserId: user.Id, ClientApplicationId: partner.Id),
            TimeSpan.FromMinutes(5),
            DateTime.UtcNow);
        harness.Context.Set<SqlOSTemporaryToken>().Add(issued.Token);
        await harness.Context.SaveChangesAsync();
        var lastAcceptedStep = (await harness.Context.Set<SqlOSUserAuthenticator>().AsNoTracking().SingleAsync(x => x.UserId == user.Id)).LastAcceptedTimeStep;

        var act = async () => await harness.Processes.VerifyMfaChallenge(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(issued.RawToken, NextCode(harness, secret), MfaChallengeTarget.DirectLogin, IdentityProcessHarness.Request),
            CancellationToken.None);

        (await act.Should().ThrowAsync<SqlOSPublicAuthException>()).Which.Error.Should().Be("invalid_client");
        (await harness.Context.Set<SqlOSUserAuthenticator>().AsNoTracking().SingleAsync(x => x.UserId == user.Id)).LastAcceptedTimeStep
            .Should().Be(lastAcceptedStep, "the factor was never compared");
        (await harness.AuditAsync("oauth.direct_login.rejected")).Should().ContainSingle()
            .Which.MetadataJson.Should().Contain("\"route\":\"/sqlos/auth/test\"");
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task An_account_enrolls_only_when_its_policy_allows_it_an_authenticator_app()
    {
        async Task<TotpEnrollmentStartOutcome> StartAsync(Action<SqlOSAuthServerOptions> configure)
        {
            var harness = await IdentityProcessHarness.CreateAsync(configure);
            var account = await harness.CreateUserAsync();
            var outcome = await harness.Processes.StartTotpEnrollment().ExecuteAsync(
                new StartTotpEnrollmentCommand(new TotpEnrollmentTarget.Account(account.Id, OrganizationId: null), "Phone", SqlOSRequestContext.System),
                CancellationToken.None);
            (await harness.Context.Set<SqlOSUserAuthenticator>().CountAsync()).Should().Be(outcome is TotpEnrollmentStartOutcome.Started ? 1 : 0);
            return outcome;
        }

        (await StartAsync(options => options.Mfa.Enabled = false)).Should().BeOfType<TotpEnrollmentStartOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Authenticator app enrollment is not enabled.");
        (await StartAsync(options => options.Mfa.AllowUserSelfEnrollmentByDefault = false)).Should().BeOfType<TotpEnrollmentStartOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Authenticator app enrollment is not available for this account.");
        var started = (await StartAsync(_ => { })).Should().BeOfType<TotpEnrollmentStartOutcome.Started>().Subject.Result;
        started.ProvisioningUri.Should().StartWith("otpauth://totp/").And.Contain($"secret={started.Secret}");
        started.QrCodeDataUrl.Should().StartWith("data:image/svg+xml");
    }

    [TestMethod]
    public async Task An_enrollment_a_challenge_does_not_permit_is_refused_and_audited()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var (user, _) = await EnrolledUserAsync(harness);
        var challenge = await DirectLoginChallengeAsync(harness, user);

        var outcome = await harness.Processes.StartTotpEnrollment().ExecuteAsync(
            new StartTotpEnrollmentCommand(new TotpEnrollmentTarget.Challenge(challenge, MfaChallengeTarget.DirectLogin), "Attacker", IdentityProcessHarness.Request),
            CancellationToken.None);

        outcome.Should().BeOfType<TotpEnrollmentStartOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.EnrollmentNotAuthorized);
        (await harness.Context.Set<SqlOSUserAuthenticator>().CountAsync(x => x.UserId == user.Id)).Should().Be(1, "no authenticator was added");
        var rejected = (await harness.AuditAsync("user.mfa.enrollment.challenge_rejected")).Should().ContainSingle().Subject;
        rejected.ActorType.Should().Be("user");
        rejected.ActorId.Should().Be(user.Id);
        rejected.IpAddress.Should().BeNull("7.2.1 recorded the refusal without the request's address");
        rejected.MetadataJson.Should().Contain("\"stage\":\"start\"");
    }

    [TestMethod]
    public async Task A_challenge_bound_enrollment_confirms_the_authenticator_and_completes_the_login_once()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var user = await harness.CreateUserAsync();
        var challenge = await DirectLoginChallengeAsync(harness, user, expectEnrollment: true);
        var started = await harness.Processes.StartTotpEnrollment().ExecuteAsync(
            new StartTotpEnrollmentCommand(new TotpEnrollmentTarget.Challenge(challenge, MfaChallengeTarget.DirectLogin), null, IdentityProcessHarness.Request),
            CancellationToken.None);
        var enrollment = started.Should().BeOfType<TotpEnrollmentStartOutcome.Started>().Subject.Result;
        var target = new TotpEnrollmentTarget.Challenge(challenge, MfaChallengeTarget.DirectLogin);

        var unbound = await harness.Processes.VerifyTotpEnrollment(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyTotpEnrollmentCommand(enrollment.EnrollmentToken, harness.Authenticators.GenerateCodeForTesting(enrollment.Secret), Challenge: null, IdentityProcessHarness.Request),
            CancellationToken.None);
        unbound.Should().BeOfType<TotpEnrollmentVerifyOutcome.Refused>()
            .Which.Refusal.Message.Should().Be("Challenge-bound enrollment must be verified with its original MFA challenge.");

        var outcome = await harness.Processes.VerifyTotpEnrollment(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyTotpEnrollmentCommand(enrollment.EnrollmentToken, harness.Authenticators.GenerateCodeForTesting(enrollment.Secret), target, IdentityProcessHarness.Request),
            CancellationToken.None);

        var signedIn = outcome.Should().BeOfType<TotpEnrollmentVerifyOutcome.SignedIn>().Subject;
        signedIn.Evidence.Methods.Should().Equal("password", "totp");
        signedIn.Result.AuthenticatorId.Should().Be(enrollment.AuthenticatorId);
        signedIn.Result.RecoveryCodes.Should().NotBeEmpty();
        signedIn.Completion.Should().BeOfType<LoginCompletion.TokensIssued>();
        (await ChallengeAsync(harness, challenge)).ConsumedAt.Should().NotBeNull();
        (await harness.Context.Set<SqlOSUserAuthenticator>().AsNoTracking().SingleAsync(x => x.Id == enrollment.AuthenticatorId)).IsConfirmed.Should().BeTrue();

        var replay = await harness.Processes.VerifyTotpEnrollment(IdentityProcessHarness.HttpContext()).ExecuteAsync(
            new VerifyTotpEnrollmentCommand(enrollment.EnrollmentToken, NextCode(harness, enrollment.Secret), target, IdentityProcessHarness.Request),
            CancellationToken.None);
        replay.Should().BeOfType<TotpEnrollmentVerifyOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.EnrollmentNotAuthorized);
        (await harness.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(1);
    }

    [TestMethod]
    public async Task A_host_checks_a_second_factor_and_spends_it()
    {
        var harness = await IdentityProcessHarness.CreateAsync(RequireMfa);
        var (user, secret) = await EnrolledUserAsync(harness);
        var code = NextCode(harness, secret);

        (await harness.Processes.VerifySecondFactor().ExecuteAsync(new VerifySecondFactorCommand(user.Id, " "), CancellationToken.None))
            .Should().BeOfType<SecondFactorCheck.Refused>().Which.Refusal.Message.Should().Be("MFA code is required.");
        (await harness.Processes.VerifySecondFactor().ExecuteAsync(new VerifySecondFactorCommand(user.Id, code), CancellationToken.None))
            .Should().BeOfType<SecondFactorCheck.Verified>().Which.Factor.Should().Be("totp");
        (await harness.Processes.VerifySecondFactor().ExecuteAsync(new VerifySecondFactorCommand(user.Id, code), CancellationToken.None))
            .Should().BeOfType<SecondFactorCheck.Refused>("a code for an accepted time step is a replay")
            .Which.Refusal.Message.Should().Be("MFA code is invalid.");
    }

    private static void RequireMfa(SqlOSAuthServerOptions options)
    {
        options.Mfa.Enabled = true;
        options.Mfa.RequireForAllUsersByDefault = true;
        options.Mfa.AllowUserSelfEnrollmentByDefault = true;
        options.Mfa.RecoveryCodesEnabledByDefault = true;
    }

    private static async Task<(SqlOSUser User, string Secret)> EnrolledUserAsync(IdentityProcessHarness harness)
    {
        var (user, secret, _) = await EnrolledUserWithRecoveryCodesAsync(harness);
        return (user, secret);
    }

    private static async Task<(SqlOSUser User, string Secret, IReadOnlyList<string> RecoveryCodes)> EnrolledUserWithRecoveryCodesAsync(IdentityProcessHarness harness)
    {
        var user = await harness.CreateUserAsync();
        var enrollment = await harness.Authenticators.StartEnrollmentAsync(user.Id, displayName: "Phone");
        var confirmed = await harness.Authenticators.VerifyEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            harness.Authenticators.GenerateCodeForTesting(enrollment.Secret)));
        return (user, enrollment.Secret, confirmed.RecoveryCodes);
    }

    private static async Task<string> DirectLoginChallengeAsync(IdentityProcessHarness harness, SqlOSUser user, bool expectEnrollment = false)
    {
        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, IdentityProcessHarness.Password, IdentityProcessHarness.ClientId, null),
            IdentityProcessHarness.HttpContext());
        login.RequiresMfa.Should().BeTrue();
        login.RequiresMfaEnrollment.Should().Be(expectEnrollment);
        return login.MfaToken!;
    }

    /// <summary>The code of the next time step: the step the enrollment accepted cannot be used again.</summary>
    private static string NextCode(IdentityProcessHarness harness, string secret)
        => harness.Authenticators.GenerateCodeForTesting(secret, DateTimeOffset.UtcNow.AddSeconds(harness.Options.Mfa.Totp.PeriodSeconds));

    private static Task<SqlOSTemporaryToken> ChallengeAsync(IdentityProcessHarness harness, string rawToken)
    {
        var hash = harness.Crypto.HashToken(rawToken);
        return harness.Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.TokenHash == hash);
    }
}
