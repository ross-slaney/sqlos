using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Security;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class DeliveryAdmissionIntegrationTests
{
    [TestMethod]
    public async Task PasswordReset_ParallelRequests_AdmitExactlyTheEmailCapAcrossInstances()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePasswordResetAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerEmailPerWindow = 3;
            options.PasswordReset.MaxRequestsPerIpPerWindow = 20;
            options.PasswordReset.MaxRequestsPerClientPerWindow = 20;
        });
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Parallel Reset User",
            $"parallel-reset-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var sender = new ConcurrentAuthEmailSender();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 10).Select(async index =>
        {
            await using var actor = database.CreatePasswordResetActor(sender);
            await start.Task;
            return await actor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(user.DefaultEmail!, "test-client"),
                CreateHttpContext($"203.0.113.{10 + index}"));
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Should().OnlyContain(result =>
            result.Message == "If an account can be reset, you'll receive a password reset email shortly.");
        sender.Messages.Should().HaveCount(3);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset_request"))
            .Should().Be(3);
        (await database.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset"))
            .Should().Be(3);
        var audits = await database.Context.Set<SqlOSAuditEvent>()
            .Select(x => x.EventType)
            .ToListAsync();
        audits.Count(x => x == "password_reset.email_sent").Should().Be(3);
        audits.Count(x => x == "password_reset.rate_limit_rejected").Should().Be(7);
    }

    [TestMethod]
    public async Task PasswordReset_OverlappingIpCap_AdmitsOnlyTheLowestLimit()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePasswordResetAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerEmailPerWindow = 10;
            options.PasswordReset.MaxRequestsPerIpPerWindow = 2;
            options.PasswordReset.MaxRequestsPerClientPerWindow = 20;
        });
        var users = new List<SqlOSUser>();
        for (var index = 0; index < 6; index++)
        {
            users.Add(await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"Overlap Reset {index}",
                $"overlap-reset-{index}-{Guid.NewGuid():N}@example.com",
                "P@ssword123!")));
        }

        var sender = new ConcurrentAuthEmailSender();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = users.Select(async (user, index) =>
        {
            await using var actor = database.CreatePasswordResetActor(sender);
            await start.Task;
            return await actor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(user.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.40"));
        }).ToArray();

        start.SetResult();
        await Task.WhenAll(attempts);

        sender.Messages.Should().HaveCount(2);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSAuditEvent>()
                .CountAsync(x => x.EventType == "password_reset.email_sent" && x.IpAddress == "203.0.113.40"))
            .Should().Be(2);
        (await database.Context.Set<SqlOSAuditEvent>()
                .CountAsync(x => x.EventType == "password_reset.rate_limit_rejected" && x.IpAddress == "203.0.113.40"))
            .Should().Be(4);
    }

    [TestMethod]
    public async Task PasswordReset_UnknownAccountAndProviderFailure_StayPrivateAndCharged()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePasswordResetAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerEmailPerWindow = 1;
            options.PasswordReset.MaxRequestsPerIpPerWindow = 20;
            options.PasswordReset.MaxRequestsPerClientPerWindow = 20;
        });
        var unknownEmail = $"unknown-reset-{Guid.NewGuid():N}@example.com";
        var known = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Known Reset Failure",
            $"known-reset-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var sender = new ConcurrentAuthEmailSender { IsConfigured = false };

        await using (var unknownActor = database.CreatePasswordResetActor(sender))
        {
            var unknown = await unknownActor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(unknownEmail, "test-client"),
                CreateHttpContext("203.0.113.50"));
            unknown.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        }

        await using (var knownActor = database.CreatePasswordResetActor(sender))
        {
            var failed = await knownActor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(known.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.51"));
            failed.Message.Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        }

        sender.Messages.Should().BeEmpty();
        await using (var retry = database.CreatePasswordResetActor(sender))
        {
            retry.Sender.IsConfigured = true;
            var unknownRetry = await retry.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(unknownEmail, "test-client"),
                CreateHttpContext("203.0.113.50"));
            var knownRetry = await retry.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(known.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.51"));
            unknownRetry.Message.Should().Be(knownRetry.Message);
        }

        sender.Messages.Should().BeEmpty();
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset_request"))
            .Should().Be(2);
        (await database.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset" && x.ConsumedAt == null))
            .Should().Be(0);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.rate_limit_rejected"))
            .Should().Be(2);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.email_send_failed"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PasswordReset_ExpiredWindow_AllowsALaterSend()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePasswordResetAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerEmailPerWindow = 1;
            options.PasswordReset.MaxRequestsPerIpPerWindow = 20;
            options.PasswordReset.MaxRequestsPerClientPerWindow = 20;
        });
        var user = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Expired Reset",
            $"expired-reset-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var sender = new ConcurrentAuthEmailSender();
        await using (var first = database.CreatePasswordResetActor(sender))
        {
            await first.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(user.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.52"));
        }

        await ExpireRateLimitBucketsAsync(database);
        await using (var second = database.CreatePasswordResetActor(sender))
        {
            await second.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(user.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.52"));
        }

        sender.Messages.Should().HaveCount(2);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset_request"))
            .Should().Be(2);
    }

    [TestMethod]
    public async Task PhoneOtp_ParallelRequests_AdmitExactlyThePhoneCapAcrossInstances()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePhoneOtpAsync(options =>
        {
            options.PhoneOtp.MaxSendsPerPhone = 3;
            options.PhoneOtp.MaxSendsPerAccount = 20;
            options.PhoneOtp.MaxSendsPerIp = 20;
            options.PhoneOtp.MaxSendsPerClient = 20;
        });
        const string phone = "+12025550170";
        await database.CreateUserWithPhoneAsync(phone);
        var channel = new ConcurrentOtpDeliveryChannel();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 10).Select(async index =>
        {
            await using var actor = database.CreatePhoneOtpActor(channel);
            await start.Task;
            try
            {
                await actor.PhoneOtp.StartForClientAsync(
                    new SqlOSPhoneOtpStartRequest(phone, "test-client", null),
                    CreateHttpContext($"203.0.113.{70 + index}"));
                return "sent";
            }
            catch (InvalidOperationException ex) when (ex.Message == "Too many sign-in code requests. Try again later.")
            {
                return "limited";
            }
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(x => x == "sent").Should().Be(3);
        results.Count(x => x == "limited").Should().Be(7);
        channel.StartCount.Should().Be(3);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPhoneOtpChallenge>().CountAsync()).Should().Be(3);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.challenge_started"))
            .Should().Be(3);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.rate_limit_rejected"))
            .Should().Be(7);
    }

    [TestMethod]
    public async Task PhoneOtp_OverlappingAccountCap_AdmitsOnlyTheLowestLimit()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePhoneOtpAsync(options =>
        {
            options.PhoneOtp.MaxSendsPerPhone = 10;
            options.PhoneOtp.MaxSendsPerAccount = 2;
            options.PhoneOtp.MaxSendsPerIp = 20;
            options.PhoneOtp.MaxSendsPerClient = 20;
        });
        var user = await database.CreateUserWithPhoneAsync("+12025550180");
        var phones = new[] { "+12025550180", "+12025550181", "+12025550182", "+12025550183", "+12025550184" };
        await using (var setup = database.CreatePhoneOtpActor(new ConcurrentOtpDeliveryChannel()))
        {
            foreach (var phone in phones.Skip(1))
            {
                await setup.PhoneOtp.AddVerifiedPhoneNumberAsync(user, phone);
            }
        }

        var channel = new ConcurrentOtpDeliveryChannel();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = phones.Select(async (phone, index) =>
        {
            await using var actor = database.CreatePhoneOtpActor(channel);
            await start.Task;
            try
            {
                await actor.PhoneOtp.StartForClientAsync(
                    new SqlOSPhoneOtpStartRequest(phone, "test-client", null),
                    CreateHttpContext($"203.0.113.{80 + index}"));
                return "sent";
            }
            catch (InvalidOperationException ex) when (ex.Message == "Too many sign-in code requests. Try again later.")
            {
                return "limited";
            }
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(x => x == "sent").Should().Be(2);
        results.Count(x => x == "limited").Should().Be(3);
        channel.StartCount.Should().Be(2);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPhoneOtpChallenge>().CountAsync()).Should().Be(2);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.rate_limit_rejected"))
            .Should().Be(3);
    }

    [TestMethod]
    public async Task PhoneOtp_ProviderFailureAndTimeout_KeepTheCharge()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePhoneOtpAsync(options =>
        {
            options.PhoneOtp.MaxSendsPerPhone = 1;
            options.PhoneOtp.MaxSendsPerAccount = 20;
            options.PhoneOtp.MaxSendsPerIp = 20;
            options.PhoneOtp.MaxSendsPerClient = 20;
        });
        await database.CreateUserWithPhoneAsync("+12025550190");
        await database.CreateUserWithPhoneAsync("+12025550191");

        var failed = new ConcurrentOtpDeliveryChannel { RejectStarts = true };
        await using (var actor = database.CreatePhoneOtpActor(failed))
        {
            var act = async () => await actor.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550190", "test-client", null),
                CreateHttpContext("203.0.113.90"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("We couldn't send a sign-in code right now.");
        }

        await using (var retry = database.CreatePhoneOtpActor(new ConcurrentOtpDeliveryChannel()))
        {
            var act = async () => await retry.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550190", "test-client", null),
                CreateHttpContext("203.0.113.90"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Too many sign-in code requests. Try again later.");
        }

        var timedOut = new ConcurrentOtpDeliveryChannel { TimeoutStarts = true };
        await using (var actor = database.CreatePhoneOtpActor(timedOut))
        {
            var act = async () => await actor.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550191", "test-client", null),
                CreateHttpContext("203.0.113.91"));
            await act.Should().ThrowAsync<TimeoutException>();
        }

        await using (var retry = database.CreatePhoneOtpActor(new ConcurrentOtpDeliveryChannel()))
        {
            var act = async () => await retry.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550191", "test-client", null),
                CreateHttpContext("203.0.113.91"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Too many sign-in code requests. Try again later.");
        }

        failed.StartCount.Should().Be(1);
        timedOut.StartCount.Should().Be(1);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPhoneOtpChallenge>().CountAsync()).Should().Be(2);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.send_failed"))
            .Should().Be(1);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.rate_limit_rejected"))
            .Should().Be(2);
    }

    [TestMethod]
    public async Task PhoneOtp_UnknownAccount_UsesTheSamePublicMessageAndStillCharges()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePhoneOtpAsync(options =>
        {
            options.PhoneOtp.MaxSendsPerPhone = 1;
            options.PhoneOtp.MaxSendsPerAccount = 20;
            options.PhoneOtp.MaxSendsPerIp = 20;
            options.PhoneOtp.MaxSendsPerClient = 20;
        });
        await database.CreateUserWithPhoneAsync("+12025550195");
        var channel = new ConcurrentOtpDeliveryChannel();

        await using (var unknown = database.CreatePhoneOtpActor(channel))
        {
            var result = await unknown.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550196", "test-client", null),
                CreateHttpContext("203.0.113.96"));
            result.Message.Should().Be("If an account exists for that phone number, check your messages for a sign-in code.");
        }

        await using (var known = database.CreatePhoneOtpActor(channel))
        {
            var result = await known.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550195", "test-client", null),
                CreateHttpContext("203.0.113.95"));
            result.Message.Should().Be("If an account exists for that phone number, check your messages for a sign-in code.");
        }

        await using (var unknownRetry = database.CreatePhoneOtpActor(channel))
        {
            var act = async () => await unknownRetry.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550196", "test-client", null),
                CreateHttpContext("203.0.113.96"));
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Too many sign-in code requests. Try again later.");
        }

        channel.StartCount.Should().Be(1);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPhoneOtpChallenge>().CountAsync()).Should().Be(2);
    }

    [TestMethod]
    public async Task PhoneOtp_ExpiredWindow_AllowsALaterSend()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePhoneOtpAsync(options =>
        {
            options.PhoneOtp.MaxSendsPerPhone = 1;
            options.PhoneOtp.MaxSendsPerAccount = 20;
            options.PhoneOtp.MaxSendsPerIp = 20;
            options.PhoneOtp.MaxSendsPerClient = 20;
        });
        const string phone = "+12025550197";
        await database.CreateUserWithPhoneAsync(phone);
        var channel = new ConcurrentOtpDeliveryChannel();
        await using (var first = database.CreatePhoneOtpActor(channel))
        {
            await first.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest(phone, "test-client", null),
                CreateHttpContext("203.0.113.97"));
        }

        await ExpireRateLimitBucketsAsync(database);
        await using (var second = database.CreatePhoneOtpActor(channel))
        {
            await second.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest(phone, "test-client", null),
                CreateHttpContext("203.0.113.97"));
        }

        channel.StartCount.Should().Be(2);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSPhoneOtpChallenge>().CountAsync()).Should().Be(2);
    }

    private static async Task ExpireRateLimitBucketsAsync(DeliveryAdmissionDatabase database)
    {
        await database.Context.Database.ExecuteSqlRawAsync(
            """
            UPDATE [dbo].[SqlOSRateLimitBuckets]
            SET [WindowStartedAt] = DATEADD(day, -2, SYSUTCDATETIME()),
                [LockedUntil] = DATEADD(day, -1, SYSUTCDATETIME()),
                [UpdatedAt] = DATEADD(day, -1, SYSUTCDATETIME());
            """);
    }

    private static DefaultHttpContext CreateHttpContext(string ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.Request.Headers.UserAgent = "SqlOSDeliveryAdmissionTests";
        return context;
    }

    private sealed class DeliveryAdmissionDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqlOSAuthServerOptions _options;

        private DeliveryAdmissionDatabase(
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

        public static Task<DeliveryAdmissionDatabase> CreatePasswordResetAsync(
            Action<SqlOSAuthServerOptions> configure)
            => CreateAsync(options =>
            {
                options.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["password"];
                    page.EnablePasswordSignup = true;
                });
                options.PasswordReset.BuildMessage = context => new SqlOSAuthEmailMessage(
                    context.Email,
                    "Reset your password",
                    $"<p>{context.ResetUrl}</p>",
                    context.ResetUrl);
                configure(options);
            }, "DeliveryReset");

        public static Task<DeliveryAdmissionDatabase> CreatePhoneOtpAsync(
            Action<SqlOSAuthServerOptions> configure)
            => CreateAsync(options =>
            {
                options.EnableLocalPasswordAuth = false;
                options.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["phone_otp"];
                    page.EnablePasswordSignup = false;
                });
                options.ConfigurePhoneOtp(phone =>
                {
                    phone.Enabled = true;
                    phone.TwilioAccountSid = "AC00000000000000000000000000000000";
                    phone.TwilioAuthToken = "test-token";
                    phone.TwilioVerifyServiceSid = "VA00000000000000000000000000000000";
                    phone.ResendCooldown = TimeSpan.Zero;
                });
                configure(options);
            }, "DeliveryPhone");

        private static async Task<DeliveryAdmissionDatabase> CreateAsync(
            Action<SqlOSAuthServerOptions> configure,
            string databasePrefix)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix);
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The delivery-admission database has no connection string.");
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            configure(options);
            var actor = BuildPasswordResetActor(context, options, new ConcurrentAuthEmailSender(), ownsContext: false);
            await actor.Crypto.EnsureActiveSigningKeyAsync();
            await actor.Admin.UpsertSeededClientsAsync();
            _ = await actor.Settings.GetAuthPageSettingsAsync();
            await actor.Settings.UpsertSeededAuthPageSettingsAsync();
            return new DeliveryAdmissionDatabase(context, connectionString, options, actor.Admin);
        }

        public async Task<SqlOSUser> CreateUserWithPhoneAsync(string phoneNumber)
        {
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"Phone User {Guid.NewGuid():N}",
                $"phone-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            await using var actor = CreatePhoneOtpActor(new ConcurrentOtpDeliveryChannel());
            await actor.PhoneOtp.AddVerifiedPhoneNumberAsync(user, phoneNumber);
            return user;
        }

        public PasswordResetActor CreatePasswordResetActor(ConcurrentAuthEmailSender sender)
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseSqlServer(_connectionString)
                    .Options);
            return BuildPasswordResetActor(context, _options, sender, ownsContext: true);
        }

        public PhoneOtpActor CreatePhoneOtpActor(ConcurrentOtpDeliveryChannel channel)
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseSqlServer(_connectionString)
                    .Options);
            return BuildPhoneOtpActor(context, _options, channel, ownsContext: true);
        }

        private static PasswordResetActor BuildPasswordResetActor(
            TestSqlOSDbContext context,
            SqlOSAuthServerOptions optionsValue,
            ConcurrentAuthEmailSender sender,
            bool ownsContext)
        {
            var options = Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, sender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, sender, options);
            var admission = new SqlOSDeliveryAdmissionService(new SqlOSDistributedRateLimitStore(context, options));
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                authEmailSender: sender,
                deliveryAdmissionService: admission);
            return new PasswordResetActor(context, admin, crypto, settings, auth, sender, ownsContext);
        }

        private static PhoneOtpActor BuildPhoneOtpActor(
            TestSqlOSDbContext context,
            SqlOSAuthServerOptions optionsValue,
            ConcurrentOtpDeliveryChannel channel,
            bool ownsContext)
        {
            var options = Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, new ConcurrentAuthEmailSender());
            var admission = new SqlOSDeliveryAdmissionService(new SqlOSDistributedRateLimitStore(context, options));
            var phoneOtp = new SqlOSPhoneOtpService(
                context,
                admin,
                crypto,
                settings,
                channel,
                options,
                admission);
            return new PhoneOtpActor(context, phoneOtp, ownsContext);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class PasswordResetActor(
        TestSqlOSDbContext context,
        SqlOSAdminService admin,
        SqlOSCryptoService crypto,
        SqlOSSettingsService settings,
        SqlOSAuthService auth,
        ConcurrentAuthEmailSender sender,
        bool ownsContext) : IAsyncDisposable
    {
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSSettingsService Settings { get; } = settings;
        public SqlOSAuthService Auth { get; } = auth;
        public ConcurrentAuthEmailSender Sender { get; } = sender;

        public async ValueTask DisposeAsync()
        {
            if (ownsContext)
            {
                await context.DisposeAsync();
            }
        }
    }

    private sealed class PhoneOtpActor(
        TestSqlOSDbContext context,
        SqlOSPhoneOtpService phoneOtp,
        bool ownsContext) : IAsyncDisposable
    {
        public SqlOSPhoneOtpService PhoneOtp { get; } = phoneOtp;

        public async ValueTask DisposeAsync()
        {
            if (ownsContext)
            {
                await context.DisposeAsync();
            }
        }
    }

    private sealed class ConcurrentAuthEmailSender : ISqlOSAuthEmailSender
    {
        private readonly ConcurrentBag<SqlOSAuthEmailMessage> _messages = [];

        public bool IsConfigured { get; set; } = true;

        public IReadOnlyCollection<SqlOSAuthEmailMessage> Messages => _messages;

        public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                throw new InvalidOperationException("Auth email delivery is not configured.");
            }

            _messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentOtpDeliveryChannel : ISqlOSOtpDeliveryChannel
    {
        private int _startCount;

        public bool RejectStarts { get; set; }
        public bool TimeoutStarts { get; set; }
        public int StartCount => _startCount;

        public Task<SqlOSOtpDeliveryStartResult> StartAsync(
            string e164PhoneNumber,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            if (TimeoutStarts)
            {
                throw new TimeoutException("Twilio Verify timed out.");
            }

            return Task.FromResult(RejectStarts
                ? new SqlOSOtpDeliveryStartResult(false, "test_verify", null, "failed", "provider_unavailable")
                : new SqlOSOtpDeliveryStartResult(true, "test_verify", $"ve-{_startCount}", "pending"));
        }

        public Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
            string e164PhoneNumber,
            string code,
            SqlOSOtpDeliveryContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SqlOSOtpDeliveryCheckResult(true, "test_verify", context.ProviderChallengeId, "approved"));
    }
}
