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
    public async Task PasswordReset_ConcurrentAdmission_EnforcesCapsFailuresExpiryAndPrivacy()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePasswordResetAsync(options =>
        {
            options.PasswordReset.MaxRequestsPerEmailPerWindow = 1;
            options.PasswordReset.MaxRequestsPerIpPerWindow = 2;
            options.PasswordReset.MaxRequestsPerClientPerWindow = 20;
        });
        var parallelUser = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Parallel Reset User",
            $"parallel-reset-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var sender = new ConcurrentAuthEmailSender();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallel = Enumerable.Range(0, 8).Select(async index =>
        {
            await using var actor = database.CreatePasswordResetActor(sender);
            await start.Task;
            return await actor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(parallelUser.DefaultEmail!, "test-client"),
                CreateHttpContext($"203.0.113.{10 + index}"));
        }).ToArray();
        start.SetResult();
        var parallelResults = await Task.WhenAll(parallel);
        parallelResults.Should().OnlyContain(result =>
            result.Message == "If an account can be reset, you'll receive a password reset email shortly.");
        sender.Messages.Should().HaveCount(1);

        var overlapUsers = new List<SqlOSUser>();
        for (var index = 0; index < 5; index++)
        {
            overlapUsers.Add(await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"Overlap Reset {index}",
                $"overlap-reset-{index}-{Guid.NewGuid():N}@example.com",
                "P@ssword123!")));
        }

        var overlapStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlap = overlapUsers.Select(async user =>
        {
            await using var actor = database.CreatePasswordResetActor(sender);
            await overlapStart.Task;
            return await actor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(user.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.40"));
        }).ToArray();
        overlapStart.SetResult();
        await Task.WhenAll(overlap);
        sender.Messages.Should().HaveCount(3);

        var unknownEmail = $"unknown-reset-{Guid.NewGuid():N}@example.com";
        var failedUser = await database.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Known Reset Failure",
            $"known-reset-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        sender.IsConfigured = false;
        await using (var unknownActor = database.CreatePasswordResetActor(sender))
        {
            (await unknownActor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(unknownEmail, "test-client"),
                CreateHttpContext("203.0.113.50"))).Message
                .Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        }

        await using (var failedActor = database.CreatePasswordResetActor(sender))
        {
            (await failedActor.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(failedUser.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.51"))).Message
                .Should().Be("If an account can be reset, you'll receive a password reset email shortly.");
        }

        sender.IsConfigured = true;
        await using (var retry = database.CreatePasswordResetActor(sender))
        {
            var unknownRetry = await retry.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(unknownEmail, "test-client"),
                CreateHttpContext("203.0.113.50"));
            var failedRetry = await retry.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(failedUser.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.51"));
            unknownRetry.Message.Should().Be(failedRetry.Message);
        }

        sender.Messages.Should().HaveCount(3);
        await ExpireRateLimitBucketsAsync(database);
        await using (var afterExpiry = database.CreatePasswordResetActor(sender))
        {
            await afterExpiry.Auth.RequestPasswordResetEmailAsync(
                new SqlOSForgotPasswordRequest(failedUser.DefaultEmail!, "test-client"),
                CreateHttpContext("203.0.113.51"));
        }

        sender.Messages.Should().HaveCount(4);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSTemporaryToken>().CountAsync(x => x.Purpose == "password_reset" && x.ConsumedAt == null))
            .Should().BeGreaterThan(0);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.rate_limit_rejected"))
            .Should().BeGreaterThan(8);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "password_reset.email_send_failed"))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PhoneOtp_ConcurrentAdmission_EnforcesCapsFailuresExpiryAndPrivacy()
    {
        await using var database = await DeliveryAdmissionDatabase.CreatePhoneOtpAsync(options =>
        {
            options.PhoneOtp.MaxSendsPerPhone = 1;
            options.PhoneOtp.MaxSendsPerAccount = 2;
            options.PhoneOtp.MaxSendsPerIp = 20;
            options.PhoneOtp.MaxSendsPerClient = 20;
        });

        const string parallelPhone = "+12025550170";
        await database.CreateUserWithPhoneAsync(parallelPhone);
        var channel = new ConcurrentOtpDeliveryChannel();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallel = Enumerable.Range(0, 8).Select(async index =>
        {
            await using var actor = database.CreatePhoneOtpActor(channel);
            await start.Task;
            return await StartPhoneAsync(actor, parallelPhone, $"203.0.113.{70 + index}");
        }).ToArray();
        start.SetResult();
        var parallelResults = await Task.WhenAll(parallel);
        parallelResults.Count(x => x == "sent").Should().Be(1);
        parallelResults.Count(x => x == "limited").Should().Be(7);
        channel.StartCount.Should().Be(1);

        var overlapUser = await database.CreateUserWithPhoneAsync("+12025550180");
        var overlapPhones = new[] { "+12025550180", "+12025550181", "+12025550182", "+12025550183", "+12025550184" };
        await using (var setup = database.CreatePhoneOtpActor(new ConcurrentOtpDeliveryChannel()))
        {
            foreach (var phone in overlapPhones.Skip(1))
            {
                await setup.PhoneOtp.AddVerifiedPhoneNumberAsync(overlapUser, phone);
            }
        }

        var overlapChannel = new ConcurrentOtpDeliveryChannel();
        var overlapStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlap = overlapPhones.Select(async (phone, index) =>
        {
            await using var actor = database.CreatePhoneOtpActor(overlapChannel);
            await overlapStart.Task;
            return await StartPhoneAsync(actor, phone, $"203.0.113.{80 + index}");
        }).ToArray();
        overlapStart.SetResult();
        var overlapResults = await Task.WhenAll(overlap);
        overlapResults.Count(x => x == "sent").Should().Be(2);
        overlapResults.Count(x => x == "limited").Should().Be(3);
        overlapChannel.StartCount.Should().Be(2);

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
            (await StartPhoneAsync(retry, "+12025550190", "203.0.113.90")).Should().Be("limited");
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
            (await StartPhoneAsync(retry, "+12025550191", "203.0.113.91")).Should().Be("limited");
        }

        failed.StartCount.Should().Be(1);
        timedOut.StartCount.Should().Be(1);

        await database.CreateUserWithPhoneAsync("+12025550195");
        var privacy = new ConcurrentOtpDeliveryChannel();
        await using (var unknown = database.CreatePhoneOtpActor(privacy))
        {
            (await unknown.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550196", "test-client", null),
                CreateHttpContext("203.0.113.96"))).Message
                .Should().Be("If an account exists for that phone number, check your messages for a sign-in code.");
        }

        await using (var known = database.CreatePhoneOtpActor(privacy))
        {
            (await known.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest("+12025550195", "test-client", null),
                CreateHttpContext("203.0.113.95"))).Message
                .Should().Be("If an account exists for that phone number, check your messages for a sign-in code.");
        }

        await using (var unknownRetry = database.CreatePhoneOtpActor(privacy))
        {
            (await StartPhoneAsync(unknownRetry, "+12025550196", "203.0.113.96")).Should().Be("limited");
        }

        privacy.StartCount.Should().Be(1);
        await ExpireRateLimitBucketsAsync(database);
        await using (var afterExpiry = database.CreatePhoneOtpActor(privacy))
        {
            (await StartPhoneAsync(afterExpiry, "+12025550195", "203.0.113.95")).Should().Be("sent");
        }

        privacy.StartCount.Should().Be(2);
        database.Context.ChangeTracker.Clear();
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.send_failed"))
            .Should().Be(1);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "phone_otp.rate_limit_rejected"))
            .Should().BeGreaterThan(10);
    }

    [TestMethod]
    public async Task EmailOtp_ConcurrentStarts_AdmitAtMostTheEmailIpAndClientCaps()
    {
        await using var database = await DeliveryAdmissionDatabase.CreateEmailDeliveryAsync(options =>
        {
            options.EmailOtp.ResendCooldown = TimeSpan.Zero;
            options.EmailOtp.MaxChallengesPerHour = 5;
            options.EmailOtp.MaxChallengesPerIpPerHour = 7;
            options.EmailOtp.MaxChallengesPerClientPerHour = 15;
        });
        var sender = new ConcurrentAuthEmailSender();

        await AssertConcurrentEmailStartsAsync(
            database,
            sender,
            (actor, email, ip) => StartEmailOtpAsync(actor, email, ip),
            limitedAudit: "email_otp.rate_limit_rejected",
            countPersisted: static (context, emails) => context.Set<SqlOSEmailOtpChallenge>()
                .CountAsync(x => emails.Contains(x.NormalizedEmail)));

        // The resend cooldown still applies sequentially, rejects without spending capacity, and
        // a resend after it succeeds.
        database.AuthOptions.EmailOtp.ResendCooldown = TimeSpan.FromMinutes(5);
        await ExpireRateLimitBucketsAsync(database);
        var resend = await database.CreateUserAsync("otp-resend");
        var normalizedResend = SqlOSAdminService.NormalizeEmail(resend);
        await using (var actor = database.CreateEmailDeliveryActor(sender))
        {
            (await StartEmailOtpAsync(actor, resend, "198.51.100.200")).Should().Be("sent");
            (await StartEmailOtpAsync(actor, resend, "198.51.100.200")).Should().Be("cooldown");
        }

        await database.Context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedResend)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastSentAt, x => x.LastSentAt.AddMinutes(-10))
                .SetProperty(x => x.CreatedAt, x => x.CreatedAt.AddMinutes(-10)));
        await using (var actor = database.CreateEmailDeliveryActor(sender))
        {
            (await StartEmailOtpAsync(actor, resend, "198.51.100.200")).Should().Be("sent");
        }

        database.Context.ChangeTracker.Clear();
        var resent = await database.Context.Set<SqlOSEmailOtpChallenge>()
            .Where(x => x.NormalizedEmail == normalizedResend)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        resent.Should().HaveCount(2, "the cooldown rejection creates no challenge");
        resent[0].InvalidatedReason.Should().Be("superseded");
        resent[1].InvalidatedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task MagicLink_ConcurrentStarts_AdmitAtMostTheEmailIpAndClientCaps()
    {
        await using var database = await DeliveryAdmissionDatabase.CreateEmailDeliveryAsync(options =>
        {
            options.MagicLink.ResendCooldown = TimeSpan.Zero;
            options.MagicLink.MaxLinksPerEmailPerWindow = 5;
            options.MagicLink.MaxLinksPerIpPerWindow = 7;
            options.MagicLink.MaxLinksPerClientPerWindow = 15;
        });
        var sender = new ConcurrentAuthEmailSender();

        await AssertConcurrentEmailStartsAsync(
            database,
            sender,
            (actor, email, ip) => StartMagicLinkAsync(actor, email, ip),
            limitedAudit: "magic_link.rate_limit_rejected",
            countPersisted: static async (context, emails) =>
            {
                var tokens = await context.Set<SqlOSTemporaryToken>()
                    .Where(x => x.Purpose == SqlOSMagicLinkService.TokenPurpose)
                    .Select(x => x.PayloadJson!)
                    .ToListAsync();
                return tokens.Count(json => emails.Contains(
                    System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("NormalizedEmail").GetString()!));
            });

        database.AuthOptions.MagicLink.ResendCooldown = TimeSpan.FromMinutes(5);
        await ExpireRateLimitBucketsAsync(database);
        var resend = await database.CreateUserAsync("link-resend");
        await using (var actor = database.CreateEmailDeliveryActor(sender))
        {
            (await StartMagicLinkAsync(actor, resend, "198.51.100.200")).Should().Be("sent");
            (await StartMagicLinkAsync(actor, resend, "198.51.100.200")).Should().Be("cooldown");
        }

        await database.Context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == SqlOSMagicLinkService.TokenPurpose && x.ConsumedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CreatedAt, x => x.CreatedAt.AddMinutes(-10)));
        await using (var actor = database.CreateEmailDeliveryActor(sender))
        {
            (await StartMagicLinkAsync(actor, resend, "198.51.100.200")).Should().Be("sent");
        }

        database.Context.ChangeTracker.Clear();
        var links = (await database.Context.Set<SqlOSTemporaryToken>()
                .Where(x => x.Purpose == SqlOSMagicLinkService.TokenPurpose)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync())
            .Where(x => x.PayloadJson!.Contains(resend, StringComparison.Ordinal))
            .ToList();
        links.Should().HaveCount(2, "the cooldown rejection creates no link");
        links[0].ConsumedAt.Should().NotBeNull("the resend supersedes the earlier link");
        links[1].ConsumedAt.Should().BeNull();
    }

    /// <summary>
    /// Fires three bursts of 50 concurrent starts against one database with email cap 5, IP cap 7,
    /// and client cap 15: one address from 50 IPs (email cap), 50 addresses from one IP (IP cap),
    /// then 50 addresses from 50 IPs (the 3 client slots left). Every address has an account, so
    /// each admitted start sends exactly one email, and every limited start writes one audit event.
    /// </summary>
    private static async Task AssertConcurrentEmailStartsAsync(
        DeliveryAdmissionDatabase database,
        ConcurrentAuthEmailSender sender,
        Func<EmailDeliveryActor, string, string, Task<string>> start,
        string limitedAudit,
        Func<TestSqlOSDbContext, IReadOnlyCollection<string>, Task<int>> countPersisted)
    {
        const int burst = 50;
        var single = await database.CreateUserAsync("burst-email");
        var sameIp = new List<string>();
        var spread = new List<string>();
        for (var index = 0; index < burst; index++)
        {
            sameIp.Add(await database.CreateUserAsync($"burst-ip-{index}"));
            spread.Add(await database.CreateUserAsync($"burst-client-{index}"));
        }

        var byEmail = await RunBurstAsync(database, sender, start,
            Enumerable.Range(0, burst).Select(index => (single, $"10.1.0.{index + 1}")));
        byEmail.Count(x => x == "sent").Should().Be(5, "the per-email cap admits 5 of 50 concurrent starts");
        // A start can also see a sibling that committed after its own clock read and hit the
        // zero cooldown; either way it is not admitted.
        byEmail.Should().OnlyContain(x => x == "sent" || x == "limited" || x == "cooldown");
        sender.Messages.Should().HaveCount(5);

        var byIp = await RunBurstAsync(database, sender, start,
            sameIp.Select(email => (email, "10.2.0.1")));
        byIp.Count(x => x == "sent").Should().Be(7, "the per-IP cap admits 7 of 50 concurrent starts");
        byIp.Count(x => x == "limited").Should().Be(burst - 7);
        sender.Messages.Should().HaveCount(12);

        var byClient = await RunBurstAsync(database, sender, start,
            spread.Select((email, index) => (email, $"10.3.0.{index + 1}")));
        byClient.Count(x => x == "sent").Should().Be(3, "the client cap of 15 has 3 starts left");
        byClient.Count(x => x == "limited").Should().Be(burst - 3);
        sender.Messages.Should().HaveCount(15);

        database.Context.ChangeTracker.Clear();
        var all = sameIp.Concat(spread).Append(single).Select(SqlOSAdminService.NormalizeEmail).ToHashSet(StringComparer.Ordinal);
        (await countPersisted(database.Context, all)).Should().Be(15, "a rejected start persists nothing");
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == limitedAudit))
            .Should().Be(byEmail.Concat(byIp).Concat(byClient).Count(x => x == "limited"));
    }

    private static async Task<string[]> RunBurstAsync(
        DeliveryAdmissionDatabase database,
        ConcurrentAuthEmailSender sender,
        Func<EmailDeliveryActor, string, string, Task<string>> start,
        IEnumerable<(string Email, string Ip)> requests)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = requests.Select(async request =>
        {
            await using var actor = database.CreateEmailDeliveryActor(sender);
            await gate.Task;
            return await start(actor, request.Email, request.Ip);
        }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<string> StartEmailOtpAsync(EmailDeliveryActor actor, string email, string ip)
    {
        try
        {
            await actor.EmailOtp.StartForClientAsync(
                new SqlOSEmailOtpStartRequest(email, "test-client", null),
                CreateHttpContext(ip));
            return "sent";
        }
        catch (InvalidOperationException ex) when (ex.Message == "Too many sign-in code requests. Try again later.")
        {
            return "limited";
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Wait ", StringComparison.Ordinal))
        {
            return "cooldown";
        }
    }

    private static async Task<string> StartMagicLinkAsync(EmailDeliveryActor actor, string email, string ip)
    {
        try
        {
            await actor.MagicLink.StartForClientAsync(
                new SqlOSMagicLinkStartRequest(email, "test-client", null),
                CreateHttpContext(ip));
            return "sent";
        }
        catch (InvalidOperationException ex) when (ex.Message == "Too many sign-in link requests. Try again later.")
        {
            return "limited";
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Wait ", StringComparison.Ordinal))
        {
            return "cooldown";
        }
    }

    private static async Task<string> StartPhoneAsync(PhoneOtpActor actor, string phone, string ip)
    {
        try
        {
            await actor.PhoneOtp.StartForClientAsync(
                new SqlOSPhoneOtpStartRequest(phone, "test-client", null),
                CreateHttpContext(ip));
            return "sent";
        }
        catch (InvalidOperationException ex) when (ex.Message == "Too many sign-in code requests. Try again later.")
        {
            return "limited";
        }
    }

    private static async Task ExpireRateLimitBucketsAsync(DeliveryAdmissionDatabase database)
    {
        await database.Context.Database.ExecuteSqlRawAsync(
            TestDatabase.Rewrite(
                """
                UPDATE [dbo].[SqlOSRateLimitBuckets]
                SET [WindowStartedAt] = DATEADD(day, -2, SYSUTCDATETIME()),
                    [LockedUntil] = DATEADD(day, -1, SYSUTCDATETIME()),
                    [UpdatedAt] = DATEADD(day, -1, SYSUTCDATETIME());
                """));
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
        public SqlOSAuthServerOptions AuthOptions => _options;

        public static Task<DeliveryAdmissionDatabase> CreateEmailDeliveryAsync(
            Action<SqlOSAuthServerOptions> configure)
            => CreateAsync(options =>
            {
                options.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["password", "email_otp", "magic_link"];
                    page.EnablePasswordSignup = true;
                });
                options.EmailOtp.BuildMessage = context => new SqlOSAuthEmailMessage(
                    context.Email,
                    "Your sign-in code",
                    $"<p>{context.Code}</p>",
                    context.Code);
                options.MagicLink.BuildMessage = context => new SqlOSAuthEmailMessage(
                    context.Email,
                    "Your sign-in link",
                    $"<p>{context.LoginUrl}</p>",
                    context.LoginUrl);
                configure(options);
            }, "DeliveryEmail");

        /// <summary>Creates a passwordless account and returns its address.</summary>
        public async Task<string> CreateUserAsync(string label)
        {
            var email = $"{label}-{Guid.NewGuid():N}@example.com";
            await Admin.CreateUserAsync(new SqlOSCreateUserRequest(label, email, Password: null));
            return email;
        }

        public EmailDeliveryActor CreateEmailDeliveryActor(ConcurrentAuthEmailSender sender)
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseTestProvider(_connectionString)
                    .Options);
            var options = Options.Create(_options);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, sender);
            var admission = new SqlOSDeliveryAdmissionService(new SqlOSDistributedRateLimitStore(context, options));
            return new EmailDeliveryActor(
                context,
                new SqlOSEmailOtpService(context, admin, crypto, settings, sender, options, deliveryAdmissionService: admission),
                new SqlOSMagicLinkService(context, admin, crypto, settings, sender, options, deliveryAdmissionService: admission));
        }

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
                    .UseTestProvider(_connectionString)
                    .Options);
            return BuildPasswordResetActor(context, _options, sender, ownsContext: true);
        }

        public PhoneOtpActor CreatePhoneOtpActor(ConcurrentOtpDeliveryChannel channel)
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseTestProvider(_connectionString)
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

    private sealed class EmailDeliveryActor(
        TestSqlOSDbContext context,
        SqlOSEmailOtpService emailOtp,
        SqlOSMagicLinkService magicLink) : IAsyncDisposable
    {
        public SqlOSEmailOtpService EmailOtp { get; } = emailOtp;
        public SqlOSMagicLinkService MagicLink { get; } = magicLink;

        public ValueTask DisposeAsync() => context.DisposeAsync();
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
