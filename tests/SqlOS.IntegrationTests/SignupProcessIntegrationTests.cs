using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// The sign-up processes against real SQL on the configured provider
/// (<c>docs/architecture/domain-model.md</c> §12, §15 Amendment 4). A sign-up is one unit of work:
/// one refused after its account was saved leaves nothing behind, so its code and sign-up token
/// still work, and concurrent completions of one sign-up create one account. Each completion runs
/// on its own unit of work and connection, as concurrent requests do, and a barrier holds each at
/// its first write to the challenge until all of them have loaded it.
/// </summary>
[TestClass]
public sealed class SignupProcessIntegrationTests
{
    private const string ClientId = "signup-process-client";
    private const string EmailCodeSignupAudit = "user.signup.email_otp";

    [TestMethod]
    public async Task Concurrent_completions_of_one_email_code_sign_up_create_one_account()
    {
        const int completions = 4;
        await using var database = await SignupProcessDatabase.CreateAsync();
        var email = $"race-{Guid.NewGuid():N}@example.com";
        var started = await database.StartEmailCodeSignUpAsync(email, organizationName: null);

        var barrier = new EmailOtpAttemptIntegrationTests.FirstChallengeWriteBarrier(completions);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, completions).Select(async _ =>
        {
            await using var actor = database.CreateActor(barrier);
            await start.Task;
            return await actor.CompleteEmailCodeSignUpAsync(started);
        }).ToList();
        barrier.Arm();
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        barrier.Held.Should().Be(completions, "every completion loaded the open challenge before any spent it");

        var signedUp = outcomes.OfType<SignUpOutcome.SignedUp>().Should().ContainSingle("the code and its sign-up token are spent once").Subject;
        signedUp.Email.Should().Be(email);
        signedUp.Evidence.Methods.Should().Equal(AuthenticationMethods.EmailOtp);
        signedUp.Evidence.Proofs.Should().ContainSingle().Which.Covers(email).Should().BeTrue("the code proved the address it signs up");
        signedUp.Completion.Should().BeOfType<LoginCompletion.DirectLoginCompleted>()
            .Which.Result.Tokens.Should().NotBeNull("the first-party client's direct login answers with tokens");
        outcomes.OfType<SignUpOutcome.Refused>().Should().HaveCount(completions - 1)
            .And.OnlyContain(refused => refused.Refusal == IdentityRefusals.InvalidCode);

        var stored = await database.ReadSignUpAsync(started);
        stored.Accounts.Should().Be(1);
        stored.AddressVerified.Should().BeTrue();
        stored.SignupAudits.Should().Be(1, "a completion that lost the race records nothing");
        stored.Sessions.Should().Be(1);
        stored.SignupTokenSpent.Should().BeTrue();
        stored.ChallengeSpent.Should().BeTrue();
    }

    [TestMethod]
    public async Task A_sign_up_the_host_refuses_leaves_nothing_behind_and_its_code_still_works()
    {
        var refuse = true;
        await using var database = await SignupProcessDatabase.CreateAsync(options =>
            options.Headless.OnHeadlessSignupAsync = (_, _) => refuse
                ? throw new SqlOSHeadlessValidationException("Company name is already in use.")
                : Task.CompletedTask);
        var email = $"refused-{Guid.NewGuid():N}@example.com";
        var organizationName = $"Refused Org {Guid.NewGuid():N}";
        var started = await database.StartEmailCodeSignUpAsync(email, organizationName);

        await using (var actor = database.CreateActor())
        {
            // The host's hook runs once the account and its organization are saved, inside the sign-up.
            var refused = async () => await actor.CompleteEmailCodeSignUpAsync(started);
            await refused.Should().ThrowAsync<SqlOSHeadlessValidationException>().WithMessage("Company name is already in use.");
        }

        var rolledBack = await database.ReadSignUpAsync(started, organizationName);
        rolledBack.Accounts.Should().Be(0);
        rolledBack.Organizations.Should().Be(0, "the organization the sign-up created rolls back with it");
        rolledBack.SignupAudits.Should().Be(0);
        rolledBack.Sessions.Should().Be(0);
        rolledBack.SignupTokenSpent.Should().BeFalse();
        rolledBack.ChallengeSpent.Should().BeFalse("the code was spent inside the sign-up that rolled back");

        refuse = false;
        await using (var actor = database.CreateActor())
        {
            (await actor.CompleteEmailCodeSignUpAsync(started)).Should().BeOfType<SignUpOutcome.SignedUp>();
        }

        var completed = await database.ReadSignUpAsync(started, organizationName);
        completed.Accounts.Should().Be(1);
        completed.Organizations.Should().Be(1);
        completed.SignupAudits.Should().Be(1);
        completed.Sessions.Should().Be(1);
        completed.SignupTokenSpent.Should().BeTrue();
        completed.ChallengeSpent.Should().BeTrue();
    }

    private sealed record StartedSignUp(string Email, string SignupToken, string ChallengeToken, string Code);

    private sealed record StoredSignUp(
        int Accounts,
        bool AddressVerified,
        int Organizations,
        int SignupAudits,
        int Sessions,
        bool SignupTokenSpent,
        bool ChallengeSpent);

    private sealed class SignupProcessDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqlOSAuthServerOptions _options;
        private readonly CodeCapturingEmailSender _sender = new();

        private SignupProcessDatabase(TestSqlOSDbContext context, string connectionString, SqlOSAuthServerOptions options)
        {
            Context = context;
            _connectionString = connectionString;
            _options = options;
        }

        private TestSqlOSDbContext Context { get; }

        public static async Task<SignupProcessDatabase> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync("SignupProcesses");
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The sign-up database has no connection string.");
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient(ClientId, "Test Client", "https://client.example.test/callback");
            options.SeedAuthPage(page => page.EnabledCredentialTypes = ["password", "email_otp"]);
            options.EmailOtp.BuildMessage = message => new SqlOSAuthEmailMessage(message.Email, "Your code", message.Code, message.Code);
            configure?.Invoke(options);

            var database = new SignupProcessDatabase(context, connectionString, options);
            await using var setup = database.CreateActor();
            await setup.Crypto.EnsureActiveSigningKeyAsync();
            await setup.Admin.UpsertSeededClientsAsync();
            _ = await setup.Settings.GetAuthPageSettingsAsync();
            await setup.Settings.UpsertSeededAuthPageSettingsAsync();
            return database;
        }

        /// <summary>Starts a first-party client's email-code sign-up, returning its tokens and the emailed code.</summary>
        public async Task<StartedSignUp> StartEmailCodeSignUpAsync(string email, string? organizationName)
        {
            await using var actor = CreateActor();
            var outcome = await actor.Processes.StartEmailOtpSignUp().ExecuteAsync(
                new StartEmailOtpSignUpCommand(
                    "Process User",
                    email,
                    organizationName,
                    CustomFields: null,
                    new LoginTarget.DirectLogin(ClientId),
                    RequestContext()),
                CancellationToken.None);
            var sent = outcome.Should().BeOfType<EmailCodeSignUpStartOutcome.Sent>().Subject.Result;
            return new StartedSignUp(email, sent.SignupToken, sent.ChallengeToken, _sender.LatestCodeTo(email));
        }

        public async Task<StoredSignUp> ReadSignUpAsync(StartedSignUp started, string? organizationName = null)
        {
            await using var actor = CreateActor();
            var context = actor.Context;
            var normalizedEmail = SqlOSAdminService.NormalizeEmail(started.Email);
            var addresses = await context.Set<SqlOSUserEmail>()
                .AsNoTracking()
                .Where(x => x.NormalizedEmail == normalizedEmail)
                .ToListAsync();
            var userIds = addresses.Select(x => x.UserId).ToList();
            var signupToken = await context.Set<SqlOSTemporaryToken>()
                .AsNoTracking()
                .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.EmailOtpSignup))
                .Where(SqlOSTemporaryToken.Presented(started.SignupToken))
                .SingleAsync();
            var challengeHash = actor.Crypto.HashToken(started.ChallengeToken);
            var challenge = await context.Set<SqlOSEmailOtpChallenge>()
                .AsNoTracking()
                .SingleAsync(x => x.ChallengeTokenHash == challengeHash);
            return new StoredSignUp(
                Accounts: userIds.Distinct().Count(),
                AddressVerified: addresses.Count == 1 && addresses[0].IsVerified,
                Organizations: organizationName is null ? 0 : await context.Set<SqlOSOrganization>().CountAsync(x => x.Name == organizationName),
                SignupAudits: await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == EmailCodeSignupAudit),
                Sessions: await context.Set<SqlOSSession>().CountAsync(x => userIds.Contains(x.UserId)),
                SignupTokenSpent: signupToken.ConsumedAt != null,
                ChallengeSpent: challenge.ConsumedAt != null);
        }

        public SignupProcessActor CreateActor(IInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(_connectionString);
            if (interceptor != null)
            {
                builder.AddInterceptors(interceptor);
            }

            var context = new TestSqlOSDbContext(builder.Options);
            var options = Options.Create(_options);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var settings = new SqlOSSettingsService(context, options, _sender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, _sender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var processes = new SqlOSIdentityProcesses(context, admin, crypto, settings, _options)
            {
                Auth = auth,
                EmailCodes = emailOtp
            };
            return new SignupProcessActor(context, crypto, admin, settings, processes);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class SignupProcessActor(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        SqlOSIdentityProcesses processes) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSSettingsService Settings { get; } = settings;
        public SqlOSIdentityProcesses Processes { get; } = processes;

        /// <summary>Presents the started sign-up's code with its sign-up token, as the public API's direct login does.</summary>
        public Task<SignUpOutcome> CompleteEmailCodeSignUpAsync(StartedSignUp started)
            => Processes.CompleteEmailOtpSignUp(CreateHttpContext()).ExecuteAsync(
                new CompleteEmailOtpSignUpCommand(
                    started.SignupToken,
                    started.ChallengeToken,
                    started.Code,
                    Invitation: null,
                    new LoginTarget.DirectLogin(),
                    RequestContext()),
                CancellationToken.None);

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static SqlOSRequestContext RequestContext()
        => new(SqlOSRequestSurface.PublicApi, "203.0.113.40", "SignupProcessTests", RequestId: null, CorrelationId: null);

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tests");
        context.Request.Headers.UserAgent = "SignupProcessTests";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.40");
        return context;
    }

    /// <summary>Captures every code sent, by recipient, from any thread.</summary>
    private sealed class CodeCapturingEmailSender : ISqlOSAuthEmailSender
    {
        private readonly ConcurrentQueue<SqlOSAuthEmailMessage> _messages = new();

        public bool IsConfigured => true;

        public string LatestCodeTo(string email)
            => _messages.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase)).TextBody
                ?? throw new InvalidOperationException($"No code was sent to {email}.");

        public Task SendAsync(SqlOSAuthEmailMessage message, CancellationToken cancellationToken = default)
        {
            _messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
