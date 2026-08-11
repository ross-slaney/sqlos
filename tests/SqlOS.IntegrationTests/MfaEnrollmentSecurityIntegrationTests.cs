using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class MfaEnrollmentSecurityIntegrationTests
{
    [TestMethod]
    public async Task SqlServer_EnrollmentProofForDifferentUser_DoesNotMutateOrIssueArtifacts()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaBindUser");
        var userA = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA User A",
            $"sql-mfa-a-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var userB = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA User B",
            $"sql-mfa-b-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var loginA = await fixture.LoginAsync(userA);
        var loginB = await fixture.LoginAsync(userB);
        var enrollment = await fixture.Auth.StartTotpEnrollmentForChallengeAsync(
            loginA.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("SQL authenticator"));
        var code = fixture.Totp.GenerateCodeForTesting(enrollment.Secret);

        var act = async () => await fixture.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, loginB.MfaToken));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment is not authorized for this challenge.");
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == enrollment.AuthenticatorId)).IsConfirmed.Should().BeFalse();
        (await fixture.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSDeviceAuthorization>().CountAsync(x => x.ApprovedAt != null)).Should().Be(0);
    }

    [TestMethod]
    public async Task SqlServer_ConcurrentEnrollmentVerification_ConsumesOneProofAndIssuesOneSession()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaBindRace");
        var user = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA Race",
            $"sql-mfa-race-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var login = await fixture.LoginAsync(user);
        var enrollment = await fixture.Auth.StartTotpEnrollmentForChallengeAsync(
            login.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("Race authenticator"));
        var code = fixture.Totp.GenerateCodeForTesting(enrollment.Secret);
        var connectionString = fixture.Context.Database.GetConnectionString()!;

        await using var instanceA = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var instanceB = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var request = new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, login.MfaToken);
        var outcomes = await Task.WhenAll(CaptureAsync(instanceA.Auth, request), CaptureAsync(instanceB.Auth, request));

        outcomes.Count(x => x.Success).Should().Be(1);
        outcomes.Count(x => !x.Success).Should().Be(1);
        outcomes.Single(x => !x.Success).Error.Should().BeOfType<InvalidOperationException>();

        await using var verify = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        (await verify.Context.Set<SqlOSUserAuthenticator>()
            .CountAsync(x => x.UserId == user.Id && x.IsConfirmed && x.RevokedAt == null)).Should().Be(1);
        (await verify.Context.Set<SqlOSRecoveryCode>()
            .CountAsync(x => x.UserId == user.Id && x.ConsumedAt == null && x.RevokedAt == null))
            .Should().Be(fixture.Options.Mfa.Totp.RecoveryCodeCount);
        (await verify.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(1);
        (await verify.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(1);
        (await verify.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await verify.Context.Set<SqlOSDeviceAuthorization>().CountAsync(x => x.ApprovedAt != null)).Should().Be(0);
        (await verify.Context.Set<SqlOSTemporaryToken>()
            .CountAsync(x =>
                (x.Purpose == SqlOSAuthService.MfaChallengePurpose || x.Purpose == SqlOSTotpMfaService.EnrollmentPurpose)
                && x.ConsumedAt != null)).Should().Be(2);
    }

    [TestMethod]
    public async Task SqlServer_ConcurrentWrongCodesAllCountTowardChallengeCap()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaGuessRace");
        var user = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA Guess Race",
            $"sql-mfa-guess-race-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await fixture.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest("Guess race authenticator"));
        await fixture.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            fixture.Totp.GenerateCodeForTesting(enrollment.Secret)));
        var login = await fixture.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext());
        login.RequiresMfa.Should().BeTrue();
        login.RequiresMfaEnrollment.Should().BeFalse();
        var connectionString = fixture.Context.Database.GetConnectionString()!;

        await using var instanceA = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var instanceB = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var instanceC = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var instanceD = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var instanceE = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var outcomes = await Task.WhenAll(
            CaptureWrongCodeAsync(instanceA.Auth, login.MfaToken!),
            CaptureWrongCodeAsync(instanceB.Auth, login.MfaToken!),
            CaptureWrongCodeAsync(instanceC.Auth, login.MfaToken!),
            CaptureWrongCodeAsync(instanceD.Auth, login.MfaToken!),
            CaptureWrongCodeAsync(instanceE.Auth, login.MfaToken!));
        outcomes.Should().OnlyContain(x => x is InvalidOperationException);

        await using var verify = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var challenge = await verify.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.TokenHash == verify.Crypto.HashToken(login.MfaToken!));
        challenge.ConsumedAt.Should().NotBeNull();
        verify.Crypto.DeserializePayload<SqlOSMfaChallengePayload>(challenge)!.FailedAttempts.Should().Be(5);
        (await verify.Context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.Action == "user.mfa.challenge_failed" && x.UserId == user.Id)).Should().Be(5);
        (await verify.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task SqlServer_ActiveChallengesAcrossReplicasShareOneAtomicUserBudget()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaSharedBudget");
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 4;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerUser = 4;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerIp = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerClient = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerDevice = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerAuthorizationRequest = 20;
        var enrolled = await CreateEnrolledUserAsync(fixture, "Shared budget user");
        var challenges = new List<string>();
        for (var index = 0; index < 6; index++)
        {
            var login = await fixture.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(enrolled.User.DefaultEmail!, "P@ssword123!", "test-client", null),
                CreateHttpContext($"203.0.113.{10 + index}", $"SqlOSMfaReplica{index}"));
            challenges.Add(login.MfaToken!);
        }

        var connectionString = fixture.Context.Database.GetConnectionString()!;
        await using var first = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var second = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var third = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var initialFailures = await Task.WhenAll(
            CaptureMfaAsync(first.Auth, challenges[0], "wrong", CreateHttpContext("203.0.113.10", "ReplicaA")),
            CaptureMfaAsync(second.Auth, challenges[1], "wrong", CreateHttpContext("203.0.113.11", "ReplicaB")),
            CaptureMfaAsync(third.Auth, challenges[2], "wrong", CreateHttpContext("203.0.113.12", "ReplicaC")));
        initialFailures.Should().OnlyContain(x => !x.Success && x.Error is InvalidOperationException);

        await using var thresholdWrong = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var thresholdCorrect = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var thresholdRace = await Task.WhenAll(
            CaptureMfaAsync(
                thresholdWrong.Auth,
                challenges[3],
                "wrong",
                CreateHttpContext("203.0.113.13", "ReplicaD")),
            CaptureMfaAsync(
                thresholdCorrect.Auth,
                challenges[4],
                enrolled.RecoveryCode,
                CreateHttpContext("203.0.113.14", "ReplicaE")));
        thresholdRace.Count(x => x.Success).Should().BeLessThanOrEqualTo(1);
        thresholdRace.Count(x => x.Error is InvalidOperationException).Should().BeGreaterThanOrEqualTo(1);

        var challengeForBlockedCorrect = challenges[4];
        if (thresholdRace.Any(x => x.Success))
        {
            await using var fillCapacity = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
            var finalFailure = await CaptureMfaAsync(
                fillCapacity.Auth,
                challenges[5],
                "wrong",
                CreateHttpContext("203.0.113.15", "ReplicaF"));
            finalFailure.Success.Should().BeFalse();
            challengeForBlockedCorrect = challenges[3];
        }

        await using var exhausted = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var correctAfterCapacity = await CaptureMfaAsync(
            exhausted.Auth,
            challengeForBlockedCorrect,
            exhausted.Totp.GenerateCodeForTesting(
                enrolled.Secret,
                DateTimeOffset.UtcNow.AddSeconds(fixture.Options.Mfa.Totp.PeriodSeconds)),
            CreateHttpContext("203.0.113.16", "ReplicaG"));
        correctAfterCapacity.Success.Should().BeFalse();
        correctAfterCapacity.Error.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be(SqlOSAuthService.MfaChallengeFailureMessage);

        var reissue = async () => await exhausted.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(enrolled.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext("203.0.113.17", "ReplicaH"));
        await reissue.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSAuthService.MfaChallengeFailureMessage);

        exhausted.Context.ChangeTracker.Clear();
        (await exhausted.Context.Set<SqlOSMfaAttemptBucket>()
            .SingleAsync(x => x.Scope == "user")).AttemptCount.Should().Be(4);
        (await exhausted.Context.Set<SqlOSSession>()
            .CountAsync(x => x.UserId == enrolled.User.Id)).Should().BeLessThanOrEqualTo(1);
        (await exhausted.Context.Set<SqlOSRefreshToken>()
            .CountAsync(x => x.Session!.UserId == enrolled.User.Id)).Should().BeLessThanOrEqualTo(1);
        (await exhausted.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await exhausted.Context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.Action == "user.mfa.challenge_failed" && x.UserId == enrolled.User.Id))
            .Should().BeInRange(3, 4);
    }

    [TestMethod]
    public async Task SqlServer_CommonUserAgentDoesNotShareDeviceBudgetAcrossAccounts()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaDeviceBoundary");
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 1;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerUser = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerIp = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerClient = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerDevice = 1;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerAuthorizationRequest = 20;
        var attacker = await CreateEnrolledUserAsync(fixture, "Shared browser attacker");
        var victim = await CreateEnrolledUserAsync(fixture, "Shared browser victim");
        const string commonUserAgent = "Mozilla/5.0 Common Browser";
        var attackerLogin = await fixture.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(attacker.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext("198.51.100.60", commonUserAgent));
        var victimLogin = await fixture.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(victim.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext("198.51.100.61", commonUserAgent));

        var connectionString = fixture.Context.Database.GetConnectionString()!;
        await using var attackerReplica = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        (await CaptureMfaAsync(
            attackerReplica.Auth,
            attackerLogin.MfaToken!,
            "wrong",
            CreateHttpContext("198.51.100.60", commonUserAgent))).Success.Should().BeFalse();

        await using var victimReplica = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        (await CaptureMfaAsync(
            victimReplica.Auth,
            victimLogin.MfaToken!,
            victim.RecoveryCode,
            CreateHttpContext("198.51.100.61", commonUserAgent))).Success.Should().BeTrue();
        victimReplica.Context.ChangeTracker.Clear();
        (await victimReplica.Context.Set<SqlOSMfaAttemptBucket>()
            .CountAsync(x => x.Scope == "device")).Should().Be(1);

        var secondVictimLogin = await victimReplica.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(victim.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext("198.51.100.61", commonUserAgent));
        (await CaptureMfaAsync(
            victimReplica.Auth,
            secondVictimLogin.MfaToken!,
            victim.SecondRecoveryCode,
            CreateHttpContext("198.51.100.61", commonUserAgent))).Success.Should().BeTrue();
    }

    [TestMethod]
    public async Task SqlServer_ReservationAndReleaseReplaysAreIdempotent()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaReservationReplay");
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 4;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerUser = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerIp = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerClient = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerDevice = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerAuthorizationRequest = 20;
        var enrolled = await CreateEnrolledUserAsync(fixture, "Reservation replay user");
        var httpContext = CreateHttpContext("198.51.100.70", "ReservationReplayDevice");
        var login = await fixture.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(enrolled.User.DefaultEmail!, "P@ssword123!", "test-client", null),
            httpContext);

        var connectionString = fixture.Context.Database.GetConnectionString()!;
        await using var replica = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var token = await replica.Context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.TokenHash == replica.Crypto.HashToken(login.MfaToken!));
        var payload = replica.Crypto.DeserializePayload<SqlOSMfaChallengePayload>(token)!;
        var admission = new SqlOSMfaAttemptAdmissionService(
            replica.Context,
            Microsoft.Extensions.Options.Options.Create(fixture.Options));

        var first = await admission.TryReserveAsync(
            token,
            payload,
            httpContext,
            operationId: "mfaop_replay_first");
        var second = await admission.TryReserveAsync(
            token,
            payload,
            httpContext,
            operationId: "mfaop_replay_second");
        var replayedFirst = await admission.TryReserveAsync(
            token,
            payload,
            httpContext,
            operationId: "mfaop_replay_first");

        first.Admitted.Should().BeTrue();
        second.Admitted.Should().BeTrue();
        replayedFirst.Admitted.Should().BeTrue();
        (await replica.Context.Set<SqlOSMfaAttemptBucket>().ToArrayAsync())
            .Should().OnlyContain(x => x.AttemptCount == 2);

        await admission.ReleaseAsync(first.Reservation!);
        await admission.ReleaseAsync(first.Reservation!);
        replica.Context.ChangeTracker.Clear();
        (await replica.Context.Set<SqlOSMfaAttemptBucket>().ToArrayAsync())
            .Should().OnlyContain(x => x.AttemptCount == 1);

        await admission.ReleaseAsync(second.Reservation!);
        replica.Context.ChangeTracker.Clear();
        (await replica.Context.Set<SqlOSMfaAttemptBucket>().CountAsync()).Should().Be(0);
        (await replica.Context.Set<SqlOSMfaAttemptReservation>()
            .CountAsync(x => x.ReleasedAt == null)).Should().Be(0);
    }

    [TestMethod]
    public async Task SqlServer_CleanupDoesNotDeleteConcurrentlyReactivatedAttemptBucket()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaCleanupRace");
        var connectionString = fixture.Context.Database.GetConnectionString()!;
        var staleAt = DateTime.UtcNow.Subtract(fixture.Options.Mfa.Totp.FailedAttemptWindow).AddMinutes(-1);
        var bucket = new SqlOSMfaAttemptBucket
        {
            Id = $"mab_{Guid.NewGuid():N}",
            Scope = "user",
            BucketKey = "cleanup-race",
            AttemptCount = 1,
            WindowStartedAt = staleAt,
            LastAttemptAt = staleAt,
            CreatedAt = staleAt,
            UpdatedAt = staleAt
        };
        fixture.Context.Add(bucket);
        await fixture.Context.SaveChangesAsync();

        await using var reactivating = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var transaction = await reactivating.Context.Database.BeginTransactionAsync();
        var lockedBucket = await reactivating.Context.Set<SqlOSMfaAttemptBucket>()
            .SingleAsync(x => x.Id == bucket.Id);
        lockedBucket.AttemptCount = 1;
        lockedBucket.WindowStartedAt = DateTime.UtcNow;
        lockedBucket.LastAttemptAt = DateTime.UtcNow;
        lockedBucket.UpdatedAt = DateTime.UtcNow;
        await reactivating.Context.SaveChangesAsync();

        await using var cleanup = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var cleanupTask = cleanup.Admin.CleanupExpiredTemporaryTokensAsync();
        await Task.Delay(250);
        cleanupTask.IsCompleted.Should().BeFalse();
        await transaction.CommitAsync();
        await cleanupTask;

        await using var verify = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var preserved = await verify.Context.Set<SqlOSMfaAttemptBucket>()
            .SingleAsync(x => x.Id == bucket.Id);
        preserved.LastAttemptAt.Should().BeAfter(staleAt);
        preserved.AttemptCount.Should().Be(1);
    }

    [TestMethod]
    public async Task SqlServer_ClientAndIpBudgetsSpanUsersWithoutCrossingOtherScopes()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaScopeBudget");
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerChallenge = 2;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerUser = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerIp = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerClient = 2;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerDevice = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerAuthorizationRequest = 20;
        var clientUsers = new[]
        {
            await CreateEnrolledUserAsync(fixture, "Client scope A"),
            await CreateEnrolledUserAsync(fixture, "Client scope B"),
            await CreateEnrolledUserAsync(fixture, "Client scope blocked"),
            await CreateEnrolledUserAsync(fixture, "Client scope control")
        };
        var clientChallenges = new List<string>();
        for (var index = 0; index < clientUsers.Length; index++)
        {
            var clientId = index == clientUsers.Length - 1 ? "alternate-client" : "test-client";
            var login = await fixture.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(clientUsers[index].User.DefaultEmail!, "P@ssword123!", clientId, null),
                CreateHttpContext($"198.51.100.{20 + index}", $"ClientDevice{index}"));
            clientChallenges.Add(login.MfaToken!);
        }

        var connectionString = fixture.Context.Database.GetConnectionString()!;
        await using var clientA = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var clientB = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var clientFailures = await Task.WhenAll(
            CaptureMfaAsync(clientA.Auth, clientChallenges[0], "wrong", CreateHttpContext("198.51.100.20", "ClientDevice0")),
            CaptureMfaAsync(clientB.Auth, clientChallenges[1], "wrong", CreateHttpContext("198.51.100.21", "ClientDevice1")));
        clientFailures.Should().OnlyContain(x => !x.Success);

        await using var clientBlocked = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var blockedByClient = await CaptureMfaAsync(
            clientBlocked.Auth,
            clientChallenges[2],
            clientUsers[2].RecoveryCode,
            CreateHttpContext("198.51.100.22", "ClientDevice2"));
        blockedByClient.Success.Should().BeFalse();

        await using var clientControl = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var allowedOtherClient = await CaptureMfaAsync(
            clientControl.Auth,
            clientChallenges[3],
            clientUsers[3].RecoveryCode,
            CreateHttpContext("198.51.100.23", "ClientDevice3"));
        allowedOtherClient.Success.Should().BeTrue();

        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerClient = 20;
        fixture.Options.Mfa.Totp.MaxFailedAttemptsPerIp = 2;
        var ipUsers = new[]
        {
            await CreateEnrolledUserAsync(fixture, "IP scope A"),
            await CreateEnrolledUserAsync(fixture, "IP scope B"),
            await CreateEnrolledUserAsync(fixture, "IP scope blocked"),
            await CreateEnrolledUserAsync(fixture, "IP scope control")
        };
        var ipChallenges = new List<string>();
        for (var index = 0; index < ipUsers.Length; index++)
        {
            var login = await fixture.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(
                    ipUsers[index].User.DefaultEmail!,
                    "P@ssword123!",
                    index % 2 == 0 ? "test-client" : "alternate-client",
                    null),
                CreateHttpContext(index == 3 ? "192.0.2.41" : "192.0.2.40", $"IpDevice{index}"));
            ipChallenges.Add(login.MfaToken!);
        }

        await using var ipA = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var ipB = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var ipFailures = await Task.WhenAll(
            CaptureMfaAsync(ipA.Auth, ipChallenges[0], "wrong", CreateHttpContext("192.0.2.40", "IpDevice0")),
            CaptureMfaAsync(ipB.Auth, ipChallenges[1], "wrong", CreateHttpContext("192.0.2.40", "IpDevice1")));
        ipFailures.Should().OnlyContain(x => !x.Success);

        await using var ipBlocked = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        (await CaptureMfaAsync(
            ipBlocked.Auth,
            ipChallenges[2],
            ipUsers[2].RecoveryCode,
            CreateHttpContext("192.0.2.40", "IpDevice2"))).Success.Should().BeFalse();
        await using var ipControl = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        (await CaptureMfaAsync(
            ipControl.Auth,
            ipChallenges[3],
            ipUsers[3].RecoveryCode,
            CreateHttpContext("192.0.2.41", "IpDevice3"))).Success.Should().BeTrue();
    }

    [TestMethod]
    public async Task RealEndpoints_PublicHeadlessAndHostedRejectChallengeSubstitution()
    {
        await using var server = await MfaEndpointServer.CreateAsync();
        using var client = server.App.GetTestClient();

        await VerifyPublicEndpointRejectsFirstFactorEnrollmentAsync(server, client);
        await VerifyHeadlessEndpointBindsAuthorizationRequestAsync(server, client);
        await VerifyHostedEndpointBindsAuthorizationRequestAsync(server, client);
    }

    [TestMethod]
    public async Task RealPublicEndpoint_BoundsMfaGuessingAndRejectsCorrectCodeAfterCap()
    {
        await using var server = await MfaEndpointServer.CreateAsync();
        using var client = server.App.GetTestClient();
        string email;
        string userId;
        string secret;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Bounded MFA",
                $"endpoint-bounded-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            userId = user.Id;
            var enrollment = await auth.StartTotpEnrollmentAsync(user.Id, new SqlOSTotpEnrollmentStartRequest());
            secret = enrollment.Secret;
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(secret)));
        }

        var loginResponse = await client.PostAsJsonAsync("/sqlos/auth/password/login", new
        {
            email,
            password = "P@ssword123!",
            clientId = "test-client"
        });
        loginResponse.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var mfaToken = loginJson.RootElement.GetProperty("mfaToken").GetString()!;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rejected = await client.PostAsJsonAsync(
                "/sqlos/auth/mfa/challenge/verify",
                new { mfaToken, code = "not-a-valid-code" });
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await rejected.Content.ReadAsStringAsync()).Should().Contain("MFA code is invalid");
        }

        string validCode;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            validCode = totp.GenerateCodeForTesting(
                secret,
                DateTimeOffset.UtcNow.AddSeconds(30));
        }

        var afterCap = await client.PostAsJsonAsync(
            "/sqlos/auth/mfa/challenge/verify",
            new { mfaToken, code = validCode });
        afterCap.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var verifyScope = server.App.Services.CreateAsyncScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var crypto = verifyScope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
        var challenge = await context.Set<SqlOSTemporaryToken>()
            .SingleAsync(x => x.TokenHash == crypto.HashToken(mfaToken));
        challenge.ConsumedAt.Should().NotBeNull();
        crypto.DeserializePayload<SqlOSMfaChallengePayload>(challenge)!.FailedAttempts.Should().Be(5);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.Action == "user.mfa.challenge_failed" && x.UserId == userId)).Should().Be(5);
        (await context.Set<SqlOSSession>().CountAsync(x => x.UserId == userId)).Should().Be(0);
    }

    private static async Task VerifyPublicEndpointRejectsFirstFactorEnrollmentAsync(
        MfaEndpointServer server,
        HttpClient client)
    {
        string email;
        string userId;
        string[] recoveryHashes;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Existing MFA",
                $"endpoint-existing-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            userId = user.Id;
            var enrollment = await auth.StartTotpEnrollmentAsync(user.Id, new SqlOSTotpEnrollmentStartRequest());
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(enrollment.Secret)));
            recoveryHashes = await context.Set<SqlOSRecoveryCode>()
                .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .Select(x => x.CodeHash)
                .ToArrayAsync();
        }

        var loginResponse = await client.PostAsJsonAsync("/sqlos/auth/password/login", new
        {
            email,
            password = "P@ssword123!",
            clientId = "test-client"
        });
        loginResponse.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var mfaToken = loginJson.RootElement.GetProperty("mfaToken").GetString()!;
        loginJson.RootElement.GetProperty("requiresMfaEnrollment").GetBoolean().Should().BeFalse();

        var startResponse = await client.PostAsJsonAsync(
            "/sqlos/auth/mfa/challenge/totp/enroll/start",
            new { mfaToken, displayName = "Attacker authenticator" });
        startResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await startResponse.Content.ReadAsStringAsync()).Should().Contain("not authorized for this challenge");

        await using var verifyScope = server.App.Services.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSUserAuthenticator>()
            .CountAsync(x => x.UserId == userId && x.RevokedAt == null)).Should().Be(1);
        (await verifyContext.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync()).Should().BeEquivalentTo(recoveryHashes);
        (await verifyContext.Set<SqlOSSession>().CountAsync(x => x.UserId == userId)).Should().Be(0);
    }

    private static async Task VerifyHeadlessEndpointBindsAuthorizationRequestAsync(
        MfaEndpointServer server,
        HttpClient client)
    {
        string email;
        string requestA;
        string requestB;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var authorization = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Headless MFA",
                $"endpoint-headless-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            requestA = (await CreateAuthorizationRequestAsync(authorization, "headless-a", email, "headless")).Id;
            requestB = (await CreateAuthorizationRequestAsync(authorization, "headless-b", email, "headless")).Id;
        }

        var loginResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/password/login", new
        {
            requestId = requestA,
            email,
            password = "P@ssword123!"
        });
        loginResponse.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var view = loginJson.RootElement.GetProperty("viewModel");
        var mfaToken = view.GetProperty("mfaToken").GetString()!;
        var enrollment = view.GetProperty("totpEnrollment");
        var enrollmentToken = enrollment.GetProperty("enrollmentToken").GetString()!;
        var secret = enrollment.GetProperty("secret").GetString()!;
        var code = await GenerateCodeAsync(server, secret);

        var rejected = await client.PostAsJsonAsync("/sqlos/auth/headless/mfa/totp/enroll/verify", new
        {
            requestId = requestB,
            mfaToken,
            enrollmentToken,
            code
        });
        rejected.EnsureSuccessStatusCode();
        using (var rejectedJson = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
        {
            rejectedJson.RootElement.GetProperty("type").GetString().Should().Be("view");
            rejectedJson.RootElement.GetProperty("viewModel").GetProperty("error").GetString()
                .Should().Be("The request could not be completed.");
        }

        await AssertNoAuthorizationArtifactsAsync(server, requestA, requestB);

        var accepted = await client.PostAsJsonAsync("/sqlos/auth/headless/mfa/totp/enroll/verify", new
        {
            requestId = requestA,
            mfaToken,
            enrollmentToken,
            code
        });
        accepted.EnsureSuccessStatusCode();
        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        acceptedJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        await AssertAuthorizationCodeOnlyForAsync(server, requestA, requestB);
    }

    private static async Task VerifyHostedEndpointBindsAuthorizationRequestAsync(
        MfaEndpointServer server,
        HttpClient client)
    {
        var antiforgery = await GetHostedAntiforgeryAsync(client);
        string email;
        string requestA;
        string requestB;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var authorization = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Hosted MFA",
                $"endpoint-hosted-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            requestA = (await CreateAuthorizationRequestAsync(authorization, "hosted-a", email, null)).Id;
            requestB = (await CreateAuthorizationRequestAsync(authorization, "hosted-b", email, null)).Id;
        }

        var loginResponse = await PostHostedFormAsync(
            client,
            "/sqlos/auth/login/password",
            new Dictionary<string, string>
            {
                ["requestId"] = requestA,
                ["email"] = email,
                ["password"] = "P@ssword123!"
            },
            antiforgery);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginResponse.Content.ReadAsStringAsync();
        var mfaToken = ExtractHtmlValue(html, "mfaToken");
        var enrollmentToken = ExtractHtmlValue(html, "enrollmentToken");
        var secret = Regex.Match(html, @"<span>Setup key</span>\s*<code>([^<]+)</code>").Groups[1].Value;
        secret.Should().NotBeNullOrWhiteSpace();
        var code = await GenerateCodeAsync(server, WebUtility.HtmlDecode(secret));

        var rejected = await PostHostedFormAsync(
            client,
            "/sqlos/auth/mfa/totp/enroll/verify",
            new Dictionary<string, string>
            {
                ["requestId"] = requestB,
                ["mfaToken"] = mfaToken,
                ["enrollmentToken"] = enrollmentToken,
                ["code"] = code
            },
            antiforgery);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoAuthorizationArtifactsAsync(server, requestA, requestB);

        var accepted = await PostHostedFormAsync(
            client,
            "/sqlos/auth/mfa/totp/enroll/verify",
            new Dictionary<string, string>
            {
                ["requestId"] = requestA,
                ["mfaToken"] = mfaToken,
                ["enrollmentToken"] = enrollmentToken,
                ["code"] = code
            },
            antiforgery);
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await AssertAuthorizationCodeOnlyForAsync(server, requestA, requestB);
    }

    private static async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(
        SqlOSAuthorizationServerService authorization,
        string state,
        string email,
        string? presentationMode)
        => await authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "test-client",
            "https://client.example.test/callback",
            state,
            "openid profile email",
            new string('A', 43),
            "S256",
            null,
            email,
            null,
            null,
            presentationMode,
            null));

    private static async Task<string> GenerateCodeAsync(MfaEndpointServer server, string secret)
    {
        await using var scope = server.App.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>().GenerateCodeForTesting(secret);
    }

    private static string ExtractHtmlValue(string html, string inputName)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(inputName)}"" value=""([^""]+)""",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"hosted MFA HTML should include {inputName}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<(string Token, string Cookie)> GetHostedAntiforgeryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/sqlos/auth/password/reset?token=test-only-antiforgery-bootstrap");
        response.EnsureSuccessStatusCode();
        var token = ExtractHtmlValue(await response.Content.ReadAsStringAsync(), "__RequestVerificationToken");
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith("sqlos_auth_page_csrf_", StringComparison.Ordinal));
        return (token, cookie);
    }

    private static async Task<HttpResponseMessage> PostHostedFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> fields,
        (string Token, string Cookie) antiforgery)
    {
        fields["__RequestVerificationToken"] = antiforgery.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.TryAddWithoutValidation("Cookie", antiforgery.Cookie);
        return await client.SendAsync(request);
    }

    private static async Task AssertNoAuthorizationArtifactsAsync(
        MfaEndpointServer server,
        string requestA,
        string requestB)
    {
        await using var scope = server.App.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == requestA || x.AuthorizationRequestId == requestB)).Should().Be(0);
        (await context.Set<SqlOSSession>().CountAsync(x =>
            x.AuthenticationMethod != null && x.AuthenticationMethod.Contains("totp"))).Should().Be(0);
        (await context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
    }

    private static async Task AssertAuthorizationCodeOnlyForAsync(
        MfaEndpointServer server,
        string requestA,
        string requestB)
    {
        await using var scope = server.App.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.AuthorizationRequestId == requestA)).Should().Be(1);
        (await context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.AuthorizationRequestId == requestB)).Should().Be(0);
    }

    private static async Task<VerificationOutcome> CaptureAsync(
        SqlOSAuthService auth,
        SqlOSTotpEnrollmentVerifyRequest request)
    {
        try
        {
            await auth.VerifyTotpEnrollmentAsync(request, CreateHttpContext());
            return new VerificationOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new VerificationOutcome(false, ex);
        }
    }

    private static async Task<Exception?> CaptureWrongCodeAsync(SqlOSAuthService auth, string mfaToken)
    {
        try
        {
            await auth.VerifyMfaChallengeAsync(
                new SqlOSMfaChallengeVerifyRequest(mfaToken, "not-a-valid-code"),
                CreateHttpContext());
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<VerificationOutcome> CaptureMfaAsync(
        SqlOSAuthService auth,
        string mfaToken,
        string code,
        HttpContext context)
    {
        try
        {
            await auth.VerifyMfaChallengeAsync(new SqlOSMfaChallengeVerifyRequest(mfaToken, code), context);
            return new VerificationOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new VerificationOutcome(false, ex);
        }
    }

    private static async Task<EnrolledMfaUser> CreateEnrolledUserAsync(
        SqlMfaFixture fixture,
        string displayName)
    {
        var user = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            displayName,
            $"{displayName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var enrollment = await fixture.Auth.StartTotpEnrollmentAsync(
            user.Id,
            new SqlOSTotpEnrollmentStartRequest($"{displayName} authenticator"));
        var verified = await fixture.Auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
            enrollment.EnrollmentToken,
            fixture.Totp.GenerateCodeForTesting(enrollment.Secret)));
        return new EnrolledMfaUser(
            user,
            enrollment.Secret,
            verified.RecoveryCodes[0],
            verified.RecoveryCodes[1]);
    }

    private static DefaultHttpContext CreateHttpContext(
        string ipAddress = "203.0.113.230",
        string userAgent = "SqlOSMfaIntegrationTest")
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }

    private sealed record VerificationOutcome(bool Success, Exception? Error);
    private sealed record EnrolledMfaUser(
        SqlOSUser User,
        string Secret,
        string RecoveryCode,
        string SecondRecoveryCode);

    private sealed class SqlMfaFixture : IAsyncDisposable
    {
        public required TestSqlOSDbContext Context { get; init; }
        public required SqlOSAuthServerOptions Options { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSTotpMfaService Totp { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSSettingsService Settings { get; init; }
        public bool DeleteDatabaseOnDispose { get; init; }

        public static async Task<SqlMfaFixture> CreateAsync(string databasePrefix)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix);
            var options = CreateOptions();
            var fixture = Build(context, options, deleteDatabaseOnDispose: true);
            await fixture.Admin.UpsertSeededClientsAsync();
            await fixture.Settings.UpsertSeededMfaSettingsAsync();
            await fixture.Crypto.EnsureActiveSigningKeyAsync();
            return fixture;
        }

        public static SqlMfaFixture CreateForExistingDatabase(
            string connectionString,
            SqlOSAuthServerOptions options)
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseSqlServer(connectionString)
                    .Options);
            return Build(context, options, deleteDatabaseOnDispose: false);
        }

        private static SqlMfaFixture Build(
            TestSqlOSDbContext context,
            SqlOSAuthServerOptions optionsValue,
            bool deleteDatabaseOnDispose)
        {
            var options = Microsoft.Extensions.Options.Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var sender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, sender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, sender, options);
            var abuse = new SqlOSPasswordLoginAbuseService(context, admin, crypto, options);
            var policy = new SqlOSMfaPolicyService(context, settings, options);
            var totp = new SqlOSTotpMfaService(context, crypto, policy, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                passwordLoginAbuseService: abuse,
                mfaPolicyService: policy,
                totpMfaService: totp);

            return new SqlMfaFixture
            {
                Context = context,
                Options = optionsValue,
                Admin = admin,
                Auth = auth,
                Totp = totp,
                Crypto = crypto,
                Settings = settings,
                DeleteDatabaseOnDispose = deleteDatabaseOnDispose
            };
        }

        public async Task<SqlOSLoginResult> LoginAsync(SqlOSUser user)
        {
            var result = await Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
                CreateHttpContext());
            result.RequiresMfa.Should().BeTrue();
            result.RequiresMfaEnrollment.Should().BeTrue();
            result.Tokens.Should().BeNull();
            return result;
        }

        private static SqlOSAuthServerOptions CreateOptions()
        {
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            options.SeedBrowserClient("alternate-client", "Alternate Client", "https://alternate.example.test/callback");
            options.Mfa.Enabled = true;
            options.Mfa.RequireForAllUsersByDefault = true;
            options.Mfa.AllowUserSelfEnrollmentByDefault = true;
            options.Mfa.RecoveryCodesEnabledByDefault = true;
            return options;
        }

        public async ValueTask DisposeAsync()
        {
            if (DeleteDatabaseOnDispose)
            {
                await Context.Database.EnsureDeletedAsync();
            }

            await Context.DisposeAsync();
        }
    }

    private sealed class MfaEndpointServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public static async Task<MfaEndpointServer> CreateAsync()
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("MfaEndpoint");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(db => db.UseSqlServer(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = "https://tests/sqlos/auth";
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
                options.AuthServer.Mfa.Enabled = true;
                options.AuthServer.Mfa.RequireForAllUsersByDefault = true;
                options.AuthServer.Mfa.AllowUserSelfEnrollmentByDefault = true;
                options.AuthServer.Mfa.RecoveryCodesEnabledByDefault = true;
                options.AuthServer.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["password"];
                    page.EnablePasswordSignup = true;
                });
                options.AuthServer.UseHeadlessAuthPage(headless =>
                {
                    headless.BuildUiUrl = context =>
                        $"https://app.example.test/authorize?request={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(context.View)}";
                });
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await app.StartAsync();

            await using var scope = app.Services.CreateAsyncScope();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var settings = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededMfaSettingsAsync();

            return new MfaEndpointServer { App = app };
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }

            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
