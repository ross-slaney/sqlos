using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class PasswordLoginAdmissionIntegrationTests
{
    [TestMethod]
    public async Task ParallelWrongPasswords_AdmitExactlyTheAccountCapAcrossInstances()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 3;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Parallel Password User",
            $"parallel-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 10).Select(async index =>
        {
            await using var actor = database.CreateActor();
            await start.Task;
            var act = async () => await actor.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
                CreateHttpContext($"203.0.113.{100 + index}", $"parallel-{index}"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(attempts);

        database.Context.ChangeTracker.Clear();
        var auditTypes = await database.Context.Set<SqlOSAuditEvent>()
            .Where(x => x.DataJson != null
                        && x.DataJson.Contains(SqlOSAdminService.NormalizeEmail(user.DefaultEmail!)))
            .Select(x => x.EventType)
            .ToListAsync();
        auditTypes.Count(x => x == "password.login.failed").Should().Be(3);
        auditTypes.Count(x => x == "password.login.rate_limit_rejected").Should().Be(7);
        auditTypes.Count(x => x == "password.login.locked").Should().Be(2,
            "the threshold-causing reservation emits one transition for each account bucket");
        (await database.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task CorrectPasswordAfterThresholdReservations_IsRejectedWithoutSession()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 2;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Threshold Race User",
            $"threshold-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(user.DefaultEmail!);

        await using var first = database.CreateActor();
        await using var second = database.CreateActor();
        var firstAttempt = first.Abuse.CreateAttempt(
            normalizedEmail,
            CreateHttpContext("203.0.113.140", "threshold-one"),
            "test-client",
            surface: "api",
            userId: user.Id);
        var secondAttempt = second.Abuse.CreateAttempt(
            normalizedEmail,
            CreateHttpContext("203.0.113.141", "threshold-two"),
            "test-client",
            surface: "api",
            userId: user.Id);
        await first.Abuse.ReserveAsync(firstAttempt);
        await second.Abuse.ReserveAsync(secondAttempt);

        await using var correctGuess = database.CreateActor();
        var act = async () => await correctGuess.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext("203.0.113.142", "threshold-correct"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
        (await database.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);

        await first.Abuse.RecordFailureAsync(firstAttempt, "invalid_password");
        await second.Abuse.RecordFailureAsync(secondAttempt, "invalid_password");
    }

    [TestMethod]
    public async Task OverlappingAccountAndIpBuckets_AdmitOnlyTheLowestCap()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 10;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 3;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var users = new List<SqlOSUser>();
        for (var index = 0; index < 8; index++)
        {
            users.Add(await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"Overlap User {index}",
                $"overlap-{index}-{Guid.NewGuid():N}@example.com",
                "P@ssword123!")));
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = users.Select(async (user, index) =>
        {
            await using var actor = database.CreateActor();
            await start.Task;
            var act = async () => await actor.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", "test-client", null),
                CreateHttpContext("203.0.113.150", $"overlap-{index}"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(attempts);

        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSAuditEvent>()
                .CountAsync(x => x.EventType == "password.login.failed" && x.IpAddress == "203.0.113.150"))
            .Should().Be(3);
        (await database.Context.Set<SqlOSAuditEvent>()
                .CountAsync(x => x.EventType == "password.login.rate_limit_rejected" && x.IpAddress == "203.0.113.150"))
            .Should().Be(5);
    }

    [TestMethod]
    public async Task UnknownAccounts_AreDummyVerifiedOnlyUpToTheConfiguredCap()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 2;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var email = $"unknown-{Guid.NewGuid():N}@example.com";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 6).Select(async index =>
        {
            await using var actor = database.CreateActor();
            await start.Task;
            var act = async () => await actor.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(email, "anything", "test-client", null),
                CreateHttpContext($"203.0.113.{160 + index}", $"unknown-{index}"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(attempts);

        database.Context.ChangeTracker.Clear();
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var audits = await database.Context.Set<SqlOSAuditEvent>()
            .Where(x => x.DataJson != null && x.DataJson.Contains(normalizedEmail))
            .Select(x => new { x.EventType, x.DataJson })
            .ToListAsync();
        audits.Count(x => x.EventType == "password.login.failed"
                          && x.DataJson!.Contains("unknown_email", StringComparison.Ordinal)).Should().Be(2);
        audits.Count(x => x.EventType == "password.login.rate_limit_rejected").Should().Be(4);
    }

    [TestMethod]
    public async Task ExpiredAndRetriedReservations_RepairCountersWithoutDoubleAdmission()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var email = $"reservation-{Guid.NewGuid():N}@example.com";
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        await using var first = database.CreateActor();
        var attempt = first.Abuse.CreateAttempt(
            normalizedEmail,
            CreateHttpContext("203.0.113.180", "reservation"),
            "test-client",
            surface: "api");

        await first.Abuse.ReserveAsync(attempt);
        await using (var retry = database.CreateActor())
        {
            await retry.Abuse.ReserveAsync(attempt);
        }

        database.Context.ChangeTracker.Clear();
        var bucket = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == normalizedEmail);
        bucket.FailureCount.Should().Be(1);

        var reservation = await database.Context.Set<SqlOSPasswordLoginReservation>()
            .SingleAsync(x => x.Id == attempt.ReservationId);
        reservation.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await database.Context.SaveChangesAsync();

        await using var afterExpiry = database.CreateActor();
        var replacement = afterExpiry.Abuse.CreateAttempt(
            normalizedEmail,
            CreateHttpContext("203.0.113.181", "replacement"),
            "test-client",
            surface: "api");
        await afterExpiry.Abuse.ReserveAsync(replacement);

        database.Context.ChangeTracker.Clear();
        var repaired = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == normalizedEmail);
        repaired.FailureCount.Should().Be(1);
        (await database.Context.Set<SqlOSPasswordLoginReservation>()
                .CountAsync(x => x.Id == replacement.ReservationId))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task LockedSharedBucket_DoesNotPersistNovelRejectedIdentityBuckets()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 10;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 1;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        const string ip = "203.0.113.190";
        await using (var first = database.CreateActor())
        {
            var attempt = first.Abuse.CreateAttempt(
                SqlOSAdminService.NormalizeEmail("first@example.com"),
                CreateHttpContext(ip, "first"),
                surface: "api");
            await first.Abuse.ReserveAsync(attempt);
            await first.Abuse.RecordFailureAsync(attempt, "unknown_email");
        }

        var rejectedEmails = Enumerable.Range(0, 5)
            .Select(index => SqlOSAdminService.NormalizeEmail($"rejected-{index}@example.com"))
            .ToArray();
        foreach (var email in rejectedEmails)
        {
            await using var actor = database.CreateActor();
            var attempt = actor.Abuse.CreateAttempt(email, CreateHttpContext(ip, email), surface: "api");
            var act = async () => await actor.Abuse.ReserveAsync(attempt);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPasswordLoginBucket>()
                .CountAsync(x => x.Scope == "email" && rejectedEmails.Contains(x.BucketKey)))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task ExpiredLock_PreservesActiveReservationCapacity()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
            options.PasswordLogin.LockoutDuration = TimeSpan.FromSeconds(1);
        });
        var email = SqlOSAdminService.NormalizeEmail("active-reservation@example.com");
        await using var first = database.CreateActor();
        var active = first.Abuse.CreateAttempt(
            email,
            CreateHttpContext("203.0.113.191", "active"),
            surface: "api");
        await first.Abuse.ReserveAsync(active);

        database.Context.ChangeTracker.Clear();
        var bucket = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == email);
        bucket.LockedUntil = DateTime.UtcNow.AddSeconds(-1);
        await database.Context.SaveChangesAsync();

        await using var second = database.CreateActor();
        var replacement = second.Abuse.CreateAttempt(
            email,
            CreateHttpContext("203.0.113.192", "replacement"),
            surface: "api");
        var act = async () => await second.Abuse.ReserveAsync(replacement);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        database.Context.ChangeTracker.Clear();
        var preserved = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == email);
        preserved.FailureCount.Should().Be(1);
        preserved.LockedUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [TestMethod]
    public async Task ShortFailureWindow_DoesNotExpireOwningComparisonReservation()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 3;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
            options.PasswordLogin.FailureWindow = TimeSpan.FromMilliseconds(1);
        });
        var email = SqlOSAdminService.NormalizeEmail("short-window@example.com");
        await using var actor = database.CreateActor();
        var attempt = actor.Abuse.CreateAttempt(
            email,
            CreateHttpContext("203.0.113.193", "short-window"),
            surface: "api");

        await actor.Abuse.ReserveAsync(attempt);
        await Task.Delay(20);
        await actor.Abuse.RecordSuccessAsync(attempt);
        await actor.Abuse.RecordSuccessAsync(attempt);

        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPasswordLoginReservation>()
                .CountAsync(x => x.Id == attempt.ReservationId))
            .Should().Be(1);
        (await database.Context.Set<SqlOSPasswordLoginReservationBucket>()
                .CountAsync(x => x.ReservationId == attempt.ReservationId))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task MaximumLengthClientId_UsesBoundedBucketKeyAndRetainsFullClientIdentity()
    {
        var clientId = new string('c', 850);
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 5;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        await database.Admin.CreateClientAsync(new SqlOSCreateClientRequest(
            clientId,
            "Long Client",
            "long-client-api",
            ["https://long-client.example.test/callback"],
            IsFirstParty: true,
            AllowNativeHeadlessAuth: true));
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Long Client User",
            $"long-client-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        await using var actor = database.CreateActor();
        var failed = async () => await actor.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "wrong-password", clientId, null),
            CreateHttpContext("203.0.113.194", "long-client"));
        await failed.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);

        var result = await actor.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", clientId, null),
            CreateHttpContext("203.0.113.194", "long-client"));

        result.Tokens.Should().NotBeNull();
        database.Context.ChangeTracker.Clear();
        var bucket = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "client" && x.ClientKey == clientId);
        bucket.BucketKey.Should().StartWith("sha256:");
        bucket.BucketKey.Length.Should().BeLessThanOrEqualTo(512);
        bucket.ClientKey.Should().Be(clientId);
    }

    [TestMethod]
    public async Task IdempotentSuccess_CommitsExpiredCleanupBeforeUnlockedAuditWrite()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 10;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var email = SqlOSAdminService.NormalizeEmail("cleanup-before-audit@example.com");
        const string ipAddress = "203.0.113.199";

        await using var completedActor = database.CreateActor();
        var completed = completedActor.Abuse.CreateAttempt(
            email,
            CreateHttpContext(ipAddress, "completed"),
            "test-client",
            surface: "api");
        await completedActor.Abuse.ReserveAsync(completed);
        await completedActor.Abuse.RecordSuccessAsync(completed);

        await using var expiredActor = database.CreateActor();
        var expired = expiredActor.Abuse.CreateAttempt(
            email,
            CreateHttpContext(ipAddress, "expired"),
            "test-client",
            surface: "api");
        await expiredActor.Abuse.ReserveAsync(expired);
        database.Context.ChangeTracker.Clear();
        var expiredReservation = await database.Context.Set<SqlOSPasswordLoginReservation>()
            .SingleAsync(x => x.Id == expired.ReservationId);
        expiredReservation.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await database.Context.SaveChangesAsync();

        await using var auditBlocker = new SqlConnection(database.ConnectionString);
        await auditBlocker.OpenAsync();
        await using var auditTransaction = await auditBlocker.BeginTransactionAsync();
        await using (var lockCommand = auditBlocker.CreateCommand())
        {
            lockCommand.Transaction = (SqlTransaction)auditTransaction;
            lockCommand.CommandText =
                "SELECT COUNT_BIG(*) FROM [dbo].[SqlOSAuditEvents] WITH (TABLOCKX, HOLDLOCK);";
            _ = await lockCommand.ExecuteScalarAsync();
        }

        await using var retryActor = database.CreateActor();
        var retry = retryActor.Abuse.RecordSuccessAsync(completed);
        var cleanupCommitted = false;
        for (var poll = 0; poll < 50 && !cleanupCommitted; poll++)
        {
            await Task.Delay(50);
            database.Context.ChangeTracker.Clear();
            cleanupCommitted = !await database.Context.Set<SqlOSPasswordLoginReservation>()
                .AsNoTracking()
                .AnyAsync(x => x.Id == expired.ReservationId);
        }

        SqlOSPasswordLoginAttempt? replacement = null;
        if (cleanupCommitted)
        {
            await using var replacementActor = database.CreateActor();
            replacement = replacementActor.Abuse.CreateAttempt(
                email,
                CreateHttpContext(ipAddress, "replacement"),
                "test-client",
                surface: "api");
            await replacementActor.Abuse.ReserveAsync(replacement);
        }

        await auditTransaction.CommitAsync();
        await retry;

        cleanupCommitted.Should().BeTrue("cleanup must commit while the later audit insert is blocked");
        replacement.Should().NotBeNull();
        database.Context.ChangeTracker.Clear();
        var replacementBuckets = await database.Context.Set<SqlOSPasswordLoginReservationBucket>()
            .Where(x => x.ReservationId == replacement!.ReservationId)
            .Select(x => x.Bucket!.FailureCount)
            .ToListAsync();
        replacementBuckets.Should().NotBeEmpty().And.OnlyContain(x => x == 1);
    }

    [TestMethod]
    public async Task SuccessfulFinalization_RemovesOnlyUnreferencedZeroCountSharedBuckets()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 10;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        const string ipAddress = "203.0.113.200";
        const string userAgent = "shared-success-device";
        await using var firstActor = database.CreateActor();
        await using var secondActor = database.CreateActor();
        var first = firstActor.Abuse.CreateAttempt(
            SqlOSAdminService.NormalizeEmail("shared-success-1@example.com"),
            CreateHttpContext(ipAddress, userAgent),
            "test-client",
            surface: "api");
        var second = secondActor.Abuse.CreateAttempt(
            SqlOSAdminService.NormalizeEmail("shared-success-2@example.com"),
            CreateHttpContext(ipAddress, userAgent),
            "test-client",
            surface: "api");
        await firstActor.Abuse.ReserveAsync(first);
        await secondActor.Abuse.ReserveAsync(second);

        await firstActor.Abuse.RecordSuccessAsync(first);

        database.Context.ChangeTracker.Clear();
        var sharedAfterFirst = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .Where(x => x.Scope == "ip" || x.Scope == "client" || x.Scope == "device")
            .ToListAsync();
        sharedAfterFirst.Should().HaveCount(3).And.OnlyContain(x => x.FailureCount == 1);

        await secondActor.Abuse.RecordSuccessAsync(second);

        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPasswordLoginBucket>()
                .CountAsync(x => x.Scope == "ip" || x.Scope == "client" || x.Scope == "device"))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task InvalidClient_IsRejectedBeforePasswordReservation()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(_ => { });
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Invalid Client User",
            $"invalid-client-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        await using var actor = database.CreateActor();
        var act = async () => await actor.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "missing-client", null),
            CreateHttpContext("203.0.113.195", "invalid-client"));
        await act.Should().ThrowAsync<InvalidOperationException>();

        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPasswordLoginReservation>().CountAsync()).Should().Be(0);
        (await database.Context.Set<SqlOSPasswordLoginBucket>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task RebasedBucket_IsNotDecrementedByLaterExpiredCleanupBatch()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var email = SqlOSAdminService.NormalizeEmail("cleanup-batch@example.com");
        await using var activeActor = database.CreateActor();
        var active = activeActor.Abuse.CreateAttempt(
            email,
            CreateHttpContext("203.0.113.196", "active-cleanup"),
            surface: "api");
        await activeActor.Abuse.ReserveAsync(active);

        database.Context.ChangeTracker.Clear();
        var bucket = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Scope == "email" && x.BucketKey == email);
        var expiredAt = DateTime.UtcNow.AddSeconds(-1);
        bucket.FailureCount = 102;
        bucket.WindowStartedAt = expiredAt.AddMinutes(-5);
        bucket.LockedUntil = expiredAt;
        for (var index = 0; index < 101; index++)
        {
            var reservation = new SqlOSPasswordLoginReservation
            {
                Id = $"pla_expired_{index:D3}_{Guid.NewGuid():N}"[..28],
                CreatedAt = expiredAt.AddMinutes(-3),
                ExpiresAt = expiredAt
            };
            reservation.Buckets.Add(new SqlOSPasswordLoginReservationBucket
            {
                Reservation = reservation,
                ReservationId = reservation.Id,
                BucketId = bucket.Id
            });
            database.Context.Add(reservation);
        }
        await database.Context.SaveChangesAsync();

        for (var index = 0; index < 2; index++)
        {
            await using var replacementActor = database.CreateActor();
            var replacement = replacementActor.Abuse.CreateAttempt(
                email,
                CreateHttpContext($"203.0.113.{197 + index}", $"replacement-{index}"),
                surface: "api");
            var act = async () => await replacementActor.Abuse.ReserveAsync(replacement);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        database.Context.ChangeTracker.Clear();
        var preserved = await database.Context.Set<SqlOSPasswordLoginBucket>()
            .SingleAsync(x => x.Id == bucket.Id);
        preserved.FailureCount.Should().Be(1);
        preserved.LockedUntil.Should().BeAfter(DateTime.UtcNow);
    }

    private static DefaultHttpContext CreateHttpContext(string ipAddress, string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }

    private sealed class PasswordAdmissionDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqlOSAuthServerOptions _options;

        private PasswordAdmissionDatabase(
            TestSqlOSDbContext context,
            string connectionString,
            SqlOSAuthServerOptions options,
            SqlOSAdminService admin)
        {
            Context = context;
            _connectionString = connectionString;
            _options = options;
            Admin = admin;
        }

        public TestSqlOSDbContext Context { get; }
        public SqlOSAdminService Admin { get; }
        public string ConnectionString => _connectionString;

        public static async Task<PasswordAdmissionDatabase> CreateAsync(
            Action<SqlOSAuthServerOptions> configure)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync("PasswordAdmission");
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The password-admission database has no connection string.");
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            configure(options);
            var actor = BuildActor(context, options, ownsContext: false);
            await actor.Crypto.EnsureActiveSigningKeyAsync();
            await actor.Admin.UpsertSeededClientsAsync();
            _ = await actor.Settings.GetAuthPageSettingsAsync();
            return new PasswordAdmissionDatabase(context, connectionString, options, actor.Admin);
        }

        public PasswordAdmissionActor CreateActor()
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseSqlServer(_connectionString)
                    .Options);
            return BuildActor(context, _options, ownsContext: true);
        }

        private static PasswordAdmissionActor BuildActor(
            TestSqlOSDbContext context,
            SqlOSAuthServerOptions optionsValue,
            bool ownsContext)
        {
            var options = Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var sender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, sender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, sender, options);
            var abuse = new SqlOSPasswordLoginAbuseService(context, admin, crypto, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                passwordLoginAbuseService: abuse);
            return new PasswordAdmissionActor(context, admin, crypto, settings, auth, abuse, ownsContext);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class PasswordAdmissionActor(
        TestSqlOSDbContext context,
        SqlOSAdminService admin,
        SqlOSCryptoService crypto,
        SqlOSSettingsService settings,
        SqlOSAuthService auth,
        SqlOSPasswordLoginAbuseService abuse,
        bool ownsContext) : IAsyncDisposable
    {
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSSettingsService Settings { get; } = settings;
        public SqlOSAuthService Auth { get; } = auth;
        public SqlOSPasswordLoginAbuseService Abuse { get; } = abuse;

        public async ValueTask DisposeAsync()
        {
            if (ownsContext)
            {
                await context.DisposeAsync();
            }
        }
    }
}
