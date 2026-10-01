using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
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

namespace SqlOS.IntegrationTests;

/// <summary>
/// #424 against real SQL: email-code and sign-in-link sends are admitted with one atomic bucket
/// reservation before anything is written, so requests sent together, each on its own connection,
/// never exceed a limit between them: the per-address, per-IP and per-client limits all hold. A
/// resend the cooldown refuses gives its reservation back.
/// </summary>
[TestClass]
public sealed class EmailCodeAndLinkAdmissionIntegrationTests
{
    private const string ClientId = "test-client";
    private const string CodeLimited = "Too many sign-in code requests. Try again later.";
    private const string LinkLimited = "Too many sign-in link requests. Try again later.";
    private const string Sent = "sent";

    [TestMethod]
    public async Task Parallel_code_requests_for_one_address_are_admitted_up_to_its_limit()
    {
        await using var database = await SendDatabase.CreateAsync(options => options.EmailOtp.MaxChallengesPerHour = 5);
        var email = await database.CreateUserAsync();

        // Each request has its own sign-in context, so none supersedes another's challenge.
        var outcomes = await database.TogetherAsync(8, (actor, index) => actor.StartCodeAsync(email, $"203.0.113.{10 + index}", organizationId: $"org_{index}"));

        outcomes.Count(outcome => outcome == Sent).Should().Be(5, "7.2.1 admitted every request that counted before any inserted: {0}", string.Join(" | ", outcomes));
        outcomes.Count(outcome => outcome == CodeLimited).Should().Be(3);
        database.Sender.CountTo(email).Should().Be(5);
        (await database.CountAsync<SqlOSEmailOtpChallenge>()).Should().Be(5);
        (await database.ReadRateLimitedLimitsAsync("email_otp.rate_limit_rejected")).Should().Equal("email", "email", "email");
    }

    [TestMethod]
    public async Task Parallel_code_requests_from_one_ip_address_and_one_client_respect_those_limits()
    {
        await using var byIp = await SendDatabase.CreateAsync(options => options.EmailOtp.MaxChallengesPerIpPerHour = 3);
        var ipOutcomes = await byIp.TogetherAsync(6, (actor, index) => actor.StartCodeAsync(UnknownAddress(index), "203.0.113.50"));
        ipOutcomes.Count(outcome => outcome == Sent).Should().Be(3);
        (await byIp.ReadRateLimitedLimitsAsync("email_otp.rate_limit_rejected")).Should().Equal("ip", "ip", "ip");

        await using var byClient = await SendDatabase.CreateAsync(options => options.EmailOtp.MaxChallengesPerClientPerHour = 2);
        var clientOutcomes = await byClient.TogetherAsync(6, (actor, index) => actor.StartCodeAsync(UnknownAddress(index), $"198.51.100.{index + 1}"));
        clientOutcomes.Count(outcome => outcome == Sent).Should().Be(2);
        (await byClient.CountAsync<SqlOSEmailOtpChallenge>()).Should().Be(2);
        (await byClient.ReadRateLimitedLimitsAsync("email_otp.rate_limit_rejected")).Should().Equal("client", "client", "client", "client");
    }

    [TestMethod]
    public async Task Parallel_link_requests_for_one_address_and_from_one_ip_address_respect_those_limits()
    {
        await using var byAddress = await SendDatabase.CreateAsync(options => options.MagicLink.MaxLinksPerEmailPerWindow = 5);
        var email = await byAddress.CreateUserAsync();
        // Each request has its own sign-in context, so none retires another's link.
        var addressOutcomes = await byAddress.TogetherAsync(8, (actor, index) => actor.StartLinkAsync(email, $"203.0.113.{10 + index}", organizationId: $"org_{index}"));
        addressOutcomes.Count(outcome => outcome == Sent).Should().Be(5, string.Join(" | ", addressOutcomes));
        addressOutcomes.Count(outcome => outcome == LinkLimited).Should().Be(3);
        byAddress.Sender.CountTo(email).Should().Be(5);
        (await byAddress.ReadRateLimitedLimitsAsync("magic_link.rate_limit_rejected")).Should().Equal("email", "email", "email");

        await using var byIp = await SendDatabase.CreateAsync(options => options.MagicLink.MaxLinksPerIpPerWindow = 2);
        var ipOutcomes = await byIp.TogetherAsync(5, (actor, index) => actor.StartLinkAsync(UnknownAddress(index), "203.0.113.60"));
        ipOutcomes.Count(outcome => outcome == Sent).Should().Be(2, string.Join(" | ", ipOutcomes));
        (await byIp.CountAsync<SqlOSTemporaryToken>(x => x.Purpose == SqlOSMagicLinkService.TokenPurpose)).Should().Be(2);
        (await byIp.ReadRateLimitedLimitsAsync("magic_link.rate_limit_rejected")).Should().Equal("ip", "ip", "ip");
    }

    [TestMethod]
    public async Task A_resend_the_cooldown_refuses_does_not_count_against_the_limit()
    {
        await using var database = await SendDatabase.CreateAsync(options =>
        {
            options.EmailOtp.MaxChallengesPerHour = 2;
            options.EmailOtp.ResendCooldown = TimeSpan.FromMinutes(5);
            options.MagicLink.MaxLinksPerEmailPerWindow = 2;
            options.MagicLink.ResendCooldown = TimeSpan.FromMinutes(5);
        });
        var email = await database.CreateUserAsync();
        await using var actor = database.CreateActor();

        (await actor.StartCodeAsync(email, "203.0.113.70")).Should().Be(Sent);
        (await actor.StartCodeAsync(email, "203.0.113.70")).Should().StartWith("Wait 300 seconds");
        (await actor.StartCodeAsync(email, "203.0.113.70", organizationId: "org_other")).Should().Be(Sent, "the refused resend gave its reservation back");
        (await actor.StartCodeAsync(email, "203.0.113.70", organizationId: "org_third")).Should().Be(CodeLimited);

        (await actor.StartLinkAsync(email, "203.0.113.71")).Should().Be(Sent);
        (await actor.StartLinkAsync(email, "203.0.113.71")).Should().StartWith("Wait 300 seconds");
        (await actor.StartLinkAsync(email, "203.0.113.71", organizationId: "org_other")).Should().Be(Sent);
        (await actor.StartLinkAsync(email, "203.0.113.71", organizationId: "org_third")).Should().Be(LinkLimited);
    }

    private static string UnknownAddress(int index) => $"unknown-{index}-{Guid.NewGuid():N}@example.com";

    private sealed class SendDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqlOSAuthServerOptions _options;

        private SendDatabase(TestSqlOSDbContext context, string connectionString, SqlOSAuthServerOptions options)
        {
            Context = context;
            _connectionString = connectionString;
            _options = options;
        }

        public TestSqlOSDbContext Context { get; }

        public RecordingEmailSender Sender { get; } = new();

        public static async Task<SendDatabase> CreateAsync(Action<SqlOSAuthServerOptions> configure)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync("EmailSendAdmission");
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The send-admission database has no connection string.");
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth",
                PublicOrigin = "https://tests"
            };
            options.SeedBrowserClient(ClientId, "Test Client", "https://client.example.test/callback");
            options.SeedAuthPage(page => page.EnabledCredentialTypes = ["password", "email_otp", "magic_link"]);
            options.EmailOtp.ResendCooldown = TimeSpan.Zero;
            options.EmailOtp.BuildMessage = message => new SqlOSAuthEmailMessage(message.Email, "Your code", message.Code, message.Code);
            options.MagicLink.ResendCooldown = TimeSpan.Zero;
            options.MagicLink.BuildMessage = message => new SqlOSAuthEmailMessage(message.Email, "Your link", message.LoginUrl, message.LoginUrl);
            configure(options);

            var database = new SendDatabase(context, connectionString, options);
            await using var setup = database.CreateActor();
            await setup.Crypto.EnsureActiveSigningKeyAsync();
            await setup.Admin.UpsertSeededClientsAsync();
            _ = await setup.Settings.GetAuthPageSettingsAsync();
            await setup.Settings.UpsertSeededAuthPageSettingsAsync();
            return database;
        }

        public async Task<string> CreateUserAsync()
        {
            var email = $"sends-{Guid.NewGuid():N}@example.com";
            await using var actor = CreateActor();
            await actor.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Send User", email, "P@ssword123!"));
            return email;
        }

        /// <summary>Runs <paramref name="count"/> requests at once, each on its own unit of work and connection.</summary>
        public async Task<IReadOnlyList<string>> TogetherAsync(int count, Func<SendActor, int, Task<string>> request)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, count).Select(async index =>
            {
                await using var actor = CreateActor();
                await start.Task;
                return await request(actor, index);
            }).ToList();
            start.SetResult();
            return await Task.WhenAll(requests);
        }

        public async Task<int> CountAsync<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, bool>>? filter = null)
            where TEntity : class
        {
            await using var actor = CreateActor();
            var rows = actor.Context.Set<TEntity>().AsQueryable();
            return await (filter == null ? rows : rows.Where(filter)).CountAsync();
        }

        /// <summary>The limit each rate-limit audit row names, sorted.</summary>
        public async Task<IReadOnlyList<string>> ReadRateLimitedLimitsAsync(string eventType)
        {
            await using var actor = CreateActor();
            var metadata = await actor.Context.Set<SqlOSAuditEvent>()
                .AsNoTracking()
                .Where(x => x.EventType == eventType)
                .Select(x => x.MetadataJson)
                .ToListAsync();
            return metadata
                .Select(json => JsonDocument.Parse(json!).RootElement.GetProperty("details").GetProperty("limit").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        public SendActor CreateActor()
        {
            var context = new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(_connectionString).Options);
            var options = Options.Create(_options);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, Sender);
            return new SendActor(
                context,
                crypto,
                admin,
                settings,
                new SqlOSEmailOtpService(context, admin, crypto, settings, Sender, options),
                new SqlOSMagicLinkService(context, admin, crypto, settings, Sender, options));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class SendActor(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        SqlOSEmailOtpService emailOtp,
        SqlOSMagicLinkService magicLink) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSSettingsService Settings { get; } = settings;

        public Task<string> StartCodeAsync(string email, string ipAddress, string? organizationId = null)
            => OutcomeAsync(() => emailOtp.StartForClientAsync(new SqlOSEmailOtpStartRequest(email, ClientId, organizationId), Http(ipAddress)));

        public Task<string> StartLinkAsync(string email, string ipAddress, string? organizationId = null)
            => OutcomeAsync(() => magicLink.StartForClientAsync(new SqlOSMagicLinkStartRequest(email, ClientId, organizationId), Http(ipAddress)));

        public ValueTask DisposeAsync() => Context.DisposeAsync();

        private static async Task<string> OutcomeAsync(Func<Task> start)
        {
            try
            {
                await start();
                return Sent;
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }

        private static DefaultHttpContext Http(string ipAddress)
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
            http.Request.Headers.UserAgent = "SqlOSSendAdmissionTests";
            return http;
        }
    }

    /// <summary>Records every message, from any thread.</summary>
    private sealed class RecordingEmailSender : ISqlOSAuthEmailSender
    {
        private readonly ConcurrentQueue<SqlOSAuthEmailMessage> _messages = new();

        public bool IsConfigured => true;

        public int CountTo(string email) => _messages.Count(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));

        public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
        {
            _messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
