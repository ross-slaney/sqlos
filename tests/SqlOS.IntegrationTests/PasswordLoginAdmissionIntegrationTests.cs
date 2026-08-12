using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    public async Task SuccessfulThresholdReservation_DoesNotEmitLockoutAudit()
    {
        await using var database = await PasswordAdmissionDatabase.CreateAsync(options =>
        {
            options.PasswordLogin.MaxFailedAttemptsPerAccount = 1;
            options.PasswordLogin.MaxFailedAttemptsPerIp = 20;
            options.PasswordLogin.MaxFailedAttemptsPerClient = 20;
            options.PasswordLogin.MaxFailedAttemptsPerDevice = 20;
        });
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Success Threshold User",
            $"success-threshold-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));

        await using var actor = database.CreateActor();
        var result = await actor.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
            CreateHttpContext("203.0.113.145", "success-threshold"));

        result.Tokens.Should().NotBeNull();
        database.Context.ChangeTracker.Clear();
        var auditTypes = await database.Context.Set<SqlOSAuditEvent>()
            .Where(x => x.DataJson != null
                        && x.DataJson.Contains(SqlOSAdminService.NormalizeEmail(user.DefaultEmail!)))
            .Select(x => x.EventType)
            .ToListAsync();
        auditTypes.Should().Contain("password.login.succeeded");
        auditTypes.Should().NotContain("password.login.locked");
        auditTypes.Should().NotContain("password.login.suspicious_pattern");
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
