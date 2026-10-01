using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #423: when a sign-in path proves ownership of an unverified email, the proof claims it and
/// evicts every credential, identity, session, and token that was attached before verification.
/// </summary>
[TestClass]
public sealed class EmailClaimIntegrationTests
{
    private const string SquatterPassword = "Squatter-P@ssword123!";
    private const string OwnerPassword = "Owner-P@ssword456!";
    private static EmailOwnershipServer _server = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _server = await EmailOwnershipServer.CreateAsync("EmailClaim");
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SquattedPasswordAccount_OwnerSignsInWithVerifiedOidc_SquatterIsEvicted()
    {
        var squat = await SquatAsync();

        var result = await _server.CompleteGoogleLoginAsync(squat.Email);

        result.UserId.Should().Be(squat.UserId);
        await AssertSquatterEvictedAsync(squat);
    }

    [TestMethod]
    public async Task SquattedPasswordAccount_OwnerSignsInWithEmailCode_SquatterIsEvicted()
    {
        var squat = await SquatAsync();
        await using var scope = _server.CreateScope();

        var started = await scope.Auth.RequestEmailOtpAsync(
            new SqlOSEmailOtpStartRequest(squat.Email, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var code = EmailOwnershipServer.ExtractCode(_server.MessagesTo(squat.Email).Last());
        var login = await scope.Auth.VerifyEmailOtpAsync(
            new SqlOSEmailOtpVerifyRequest(started.ChallengeToken, code),
            EmailOwnershipServer.HttpContext());

        (await _server.SessionUserIdAsync(login.Tokens!)).Should().Be(squat.UserId);
        await AssertSquatterEvictedAsync(squat);
        await AssertTokensStillWorkAsync(login.Tokens!);
    }

    [TestMethod]
    public async Task SquattedPasswordAccount_OwnerSignsInWithMagicLink_SquatterIsEvicted()
    {
        var squat = await SquatAsync();
        await using var scope = _server.CreateScope();

        await scope.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest(squat.Email, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var token = EmailOwnershipServer.ExtractToken(_server.MessagesTo(squat.Email).Last());
        var login = await scope.Auth.CompleteMagicLinkAsync(
            new SqlOSMagicLinkCompleteRequest(token),
            EmailOwnershipServer.HttpContext());

        (await _server.SessionUserIdAsync(login.Tokens!)).Should().Be(squat.UserId);
        await AssertSquatterEvictedAsync(squat);
    }

    [TestMethod]
    public async Task SquattedPasswordAccount_InvitationAcceptedForThatAddress_SquatterIsEvicted()
    {
        var squat = await SquatAsync();
        var organization = await _server.CreateOrganizationAsync($"Inviting Org {Guid.NewGuid():N}");
        await using var scope = _server.CreateScope();
        await scope.Invitations.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(organization.Id, squat.Email, "member"),
            EmailOwnershipServer.HttpContext());
        var invitationToken = EmailOwnershipServer.ExtractToken(_server.MessagesTo(squat.Email).Last());

        var acceptance = await scope.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(invitationToken, squat.UserId),
            EmailOwnershipServer.HttpContext());

        acceptance.OrganizationId.Should().Be(organization.Id);
        await AssertSquatterEvictedAsync(squat);
    }

    [TestMethod]
    public async Task SquattedPasswordAccount_OwnerResetsPassword_SquatterFactorsAreEvicted()
    {
        var squat = await SquatAsync();
        await using (var seed = _server.CreateScope())
        {
            var now = DateTime.UtcNow;
            // The squatter's factors, as the rows its own enrollment would have stored.
            seed.Context.Set<SqlOSUserAuthenticator>().Add(TestRows.Create<SqlOSUserAuthenticator>(new
            {
                Id = $"auth_{Guid.NewGuid():N}"[..28],
                UserId = squat.UserId,
                SecretProtected = "protected-secret",
                IsConfirmed = true,
                CreatedAt = now,
                ConfirmedAt = (DateTime?)now
            }));
            seed.Context.Set<SqlOSRecoveryCode>().Add(TestRows.Create<SqlOSRecoveryCode>(new
            {
                Id = $"rc_{Guid.NewGuid():N}"[..28],
                UserId = squat.UserId,
                CodeHash = Guid.NewGuid().ToString("N"),
                CreatedAt = now
            }));
            seed.Context.Set<SqlOSUserPhoneNumber>().Add(TestRows.Create<SqlOSUserPhoneNumber>(new
            {
                Id = $"phn_{Guid.NewGuid():N}"[..28],
                UserId = squat.UserId,
                PhoneNumber = "+15555550100",
                PhoneNumberHash = Guid.NewGuid().ToString("N"),
                IsPrimary = true,
                IsVerified = true,
                VerifiedAt = (DateTime?)now,
                CreatedAt = now,
                UpdatedAt = now
            }));
            await seed.Context.SaveChangesAsync();
        }

        await using var scope = _server.CreateScope();
        await scope.Auth.RequestPasswordResetEmailAsync(
            new SqlOSForgotPasswordRequest(squat.Email, EmailOwnershipServer.ClientId),
            EmailOwnershipServer.HttpContext());
        var resetToken = EmailOwnershipServer.ExtractToken(_server.MessagesTo(squat.Email).Last());
        await scope.Auth.ResetPasswordAsync(new SqlOSResetPasswordRequest(resetToken, OwnerPassword));

        await AssertSquatterEvictedAsync(squat);
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSUserAuthenticator>().AnyAsync(x => x.UserId == squat.UserId && x.RevokedAt == null))
            .Should().BeFalse("an authenticator enrolled before the owner proved the mailbox must not survive the claim");
        (await verification.Set<SqlOSRecoveryCode>().AnyAsync(x => x.UserId == squat.UserId && x.RevokedAt == null))
            .Should().BeFalse();
        (await verification.Set<SqlOSUserPhoneNumber>().AnyAsync(x => x.UserId == squat.UserId && x.RemovedAt == null))
            .Should().BeFalse();
        var ownerLogin = await scope.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(squat.Email, OwnerPassword, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        (await _server.SessionUserIdAsync(ownerLogin.Tokens!)).Should().Be(squat.UserId);
    }

    [TestMethod]
    public async Task UnverifiedUpstreamIdentity_OwnerSignsInWithVerifiedGoogle_UpstreamIdentityIsEvicted()
    {
        var email = $"victim{Guid.NewGuid():N}@corp.example";
        var squatter = await _server.CompleteCustomLoginAsync(email, mode: "unverified");
        await using (var verification = _server.CreateVerificationContext())
        {
            (await verification.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == squatter.UserId))
                .IsVerified.Should().BeFalse("an upstream login without email_verified provisions an unverified email");
        }

        var owner = await _server.CompleteGoogleLoginAsync(email);
        owner.UserId.Should().Be(squatter.UserId);

        var squatterAgain = async () => await _server.CompleteCustomLoginAsync(email, mode: "unverified");
        await squatterAgain.Should().ThrowAsync<InvalidOperationException>(
            "the unverified upstream identity attached before the verified claim must no longer sign in");
        await using var final = _server.CreateVerificationContext();
        (await final.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.UserId == squatter.UserId && x.Subject == $"custom-{email}"))
            .Should().BeFalse();
        (await final.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "user.email.claimed" && x.UserId == squatter.UserId))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task PasswordSignup_ThenVerificationLink_KeepsThePassword()
    {
        var email = $"owner{Guid.NewGuid():N}@verify.example";
        await using var scope = _server.CreateScope();
        var signup = await scope.Auth.SignUpAsync(
            new SqlOSSignupRequest("Owner", email, OwnerPassword, null, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var verificationToken = await scope.Auth.CreateEmailVerificationTokenAsync(new SqlOSCreateVerificationTokenRequest(email));

        await scope.Auth.VerifyEmailAsync(new SqlOSVerifyEmailRequest(verificationToken));

        var login = await scope.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(email, OwnerPassword, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var userId = await _server.SessionUserIdAsync(login.Tokens!);
        await AssertTokensStillWorkAsync(signup.Tokens!);
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId)).IsVerified.Should().BeTrue();
        (await verification.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "user.email.claimed" && x.UserId == userId))
            .Should().BeFalse("the explicit verification link confirms the signup and revokes nothing");
    }

    [TestMethod]
    public async Task VerifiedAccount_GoogleSignInAndInvitation_RevokeNothing()
    {
        var email = $"verified{Guid.NewGuid():N}@owner.example";
        var user = await _server.CreateUserAsync(email, OwnerPassword, verified: true);
        await using var scope = _server.CreateScope();
        var existing = await scope.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(email, OwnerPassword, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());

        var google = await _server.CompleteGoogleLoginAsync(email);
        google.UserId.Should().Be(user.Id);
        var organization = await _server.CreateOrganizationAsync($"Verified Invite {Guid.NewGuid():N}");
        await scope.Invitations.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(organization.Id, email, "member"),
            EmailOwnershipServer.HttpContext());
        await scope.Auth.AcceptEmailInvitationAsync(
            new SqlOSAcceptEmailInvitationRequest(EmailOwnershipServer.ExtractToken(_server.MessagesTo(email).Last()), user.Id),
            EmailOwnershipServer.HttpContext());

        var passwordLogin = await scope.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(email, OwnerPassword, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        (await _server.SessionUserIdAsync(passwordLogin.Tokens!)).Should().Be(user.Id);
        await AssertTokensStillWorkAsync(existing.Tokens!);
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSExternalIdentity>().CountAsync(x => x.UserId == user.Id)).Should().Be(1);
        (await verification.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "user.email.claimed" && x.UserId == user.Id))
            .Should().BeFalse();
    }

    private static async Task<Squat> SquatAsync()
    {
        var email = $"victim{Guid.NewGuid():N}@corp.example";
        await using var scope = _server.CreateScope();
        var signup = await scope.Auth.SignUpAsync(
            new SqlOSSignupRequest("Squatter", email, SquatterPassword, null, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var userId = await _server.SessionUserIdAsync(signup.Tokens!);
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId)).IsVerified.Should().BeFalse();
        return new Squat(email, userId, signup.Tokens!.RefreshToken);
    }

    private static async Task AssertSquatterEvictedAsync(Squat squat)
    {
        await using var scope = _server.CreateScope();
        var passwordLogin = async () => await scope.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(squat.Email, SquatterPassword, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        await passwordLogin.Should().ThrowAsync<InvalidOperationException>(
            "the password attached before the owner proved the mailbox must stop working");
        var refresh = async () => await scope.Auth.RefreshAsync(new SqlOSRefreshRequest(squat.RefreshToken, null));
        await refresh.Should().ThrowAsync<InvalidOperationException>(
            "tokens issued before the claim must stop working");

        await using var verification = _server.CreateVerificationContext();
        var email = await verification.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == squat.UserId);
        email.IsVerified.Should().BeTrue();
        var claims = await verification.Set<SqlOSAuditEvent>()
            .Where(x => x.EventType == "user.email.claimed" && x.UserId == squat.UserId)
            .ToListAsync();
        claims.Should().ContainSingle("each claim writes one audit event");
        claims[0].DataJson.Should().Contain("password");
    }

    private static async Task AssertTokensStillWorkAsync(SqlOSTokenResponse tokens)
    {
        await using var scope = _server.CreateScope();
        var refreshed = await scope.Auth.RefreshAsync(new SqlOSRefreshRequest(tokens.RefreshToken, tokens.OrganizationId));
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record Squat(string Email, string UserId, string RefreshToken);
}
