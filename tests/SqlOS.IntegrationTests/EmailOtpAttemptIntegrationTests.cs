using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using System.Transactions;
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
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #424 against real SQL. An email-code attempt is spent with one conditional update
/// (<c>AttemptCount &lt; MaxAttempts</c>) before the code is compared, so concurrent verifications
/// spend exactly one attempt each and never more than the limit between them, the wrong code that
/// spends the last attempt closes the challenge, and nothing opens it again. Every concurrent
/// verification runs on its own connection, and a barrier holds each one at its first write to the
/// challenge until all of them have loaded it: the interleaving that lost attempts in 7.2.1.
/// </summary>
[TestClass]
public sealed class EmailOtpAttemptIntegrationTests
{
    private const int MaxAttempts = 5;
    private const string InvalidCodeMessage = "The sign-in code is invalid or expired.";
    private const string SignedIn = "signed in";

    [TestMethod]
    public async Task Concurrent_wrong_codes_beyond_the_limit_spend_exactly_the_limit_and_close_the_challenge()
    {
        const int guesses = 12;
        await using var database = await EmailOtpDatabase.CreateAsync();
        var challenge = await database.StartSignInAsync();

        var barrier = new FirstChallengeWriteBarrier(guesses);
        var outcomes = await database.VerifyTogetherAsync(challenge.Token, Enumerable.Repeat(WrongCode(challenge.Code), guesses), barrier);

        barrier.Held.Should().Be(guesses, "every guess loaded the open challenge before any of them spent an attempt");
        outcomes.Should().HaveCount(guesses).And.OnlyContain(outcome => outcome == InvalidCodeMessage);
        var stored = await database.ReadChallengeAsync(challenge.Token);
        stored.AttemptCount.Should().Be(MaxAttempts, "no attempt is lost and none is spent beyond the limit");
        stored.InvalidatedReason.Should().Be("max_attempts");
        stored.InvalidatedAt.Should().NotBeNull();
        stored.ConsumedAt.Should().BeNull();
        (await database.ReadVerifyFailureReasonsAsync()).Should().BeEquivalentTo(
            new[] { "max_attempts", "wrong_code", "wrong_code", "wrong_code", "wrong_code" },
            "only the guesses that spent an attempt compared their code, and exactly one spent the last");

        (await database.VerifyAsync(challenge.Token, challenge.Code)).Should().Be(InvalidCodeMessage, "a closed challenge never signs in");
        (await database.ReadChallengeAsync(challenge.Token)).AttemptCount.Should().Be(MaxAttempts);
        (await database.CountAuditAsync("email_otp.verify_succeeded")).Should().Be(0);
    }

    [TestMethod]
    public async Task Concurrent_wrong_codes_within_the_limit_each_spend_one_attempt()
    {
        const int guesses = 3;
        await using var database = await EmailOtpDatabase.CreateAsync();
        var challenge = await database.StartSignInAsync();

        var barrier = new FirstChallengeWriteBarrier(guesses);
        var outcomes = await database.VerifyTogetherAsync(challenge.Token, Enumerable.Repeat(WrongCode(challenge.Code), guesses), barrier);

        barrier.Held.Should().Be(guesses);
        outcomes.Should().OnlyContain(outcome => outcome == InvalidCodeMessage);
        var stored = await database.ReadChallengeAsync(challenge.Token);
        stored.AttemptCount.Should().Be(guesses, "7.2.1 counted these three as one");
        stored.InvalidatedAt.Should().BeNull();
        (await database.ReadVerifyFailureReasonsAsync()).Should().Equal("wrong_code", "wrong_code", "wrong_code");

        (await database.VerifyAsync(challenge.Token, challenge.Code)).Should().Be(SignedIn, "two attempts are left");
        stored = await database.ReadChallengeAsync(challenge.Token);
        stored.ConsumedAt.Should().NotBeNull();
        stored.AttemptCount.Should().Be(guesses + 1, "the right code spends its attempt too");
        (await database.CountAuditAsync("email_otp.verify_succeeded")).Should().Be(1);
    }

    [TestMethod]
    public async Task Sequential_wrong_codes_close_the_challenge_at_the_limit()
    {
        await using var database = await EmailOtpDatabase.CreateAsync();
        var challenge = await database.StartSignInAsync();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            (await database.VerifyAsync(challenge.Token, WrongCode(challenge.Code))).Should().Be(InvalidCodeMessage);
            (await database.ReadChallengeAsync(challenge.Token)).AttemptCount.Should().Be(attempt);
        }

        (await database.VerifyAsync(challenge.Token, challenge.Code)).Should().Be(InvalidCodeMessage);
        var stored = await database.ReadChallengeAsync(challenge.Token);
        stored.AttemptCount.Should().Be(MaxAttempts);
        stored.InvalidatedReason.Should().Be("max_attempts");
        (await database.ReadVerifyFailureReasonsAsync()).Should().Equal("wrong_code", "wrong_code", "wrong_code", "wrong_code", "max_attempts");
    }

    [TestMethod]
    public async Task Concurrent_right_codes_sign_in_once()
    {
        const int submissions = 4;
        await using var database = await EmailOtpDatabase.CreateAsync();
        var challenge = await database.StartSignInAsync();

        var barrier = new FirstChallengeWriteBarrier(submissions);
        var outcomes = await database.VerifyTogetherAsync(challenge.Token, Enumerable.Repeat(challenge.Code, submissions), barrier);

        barrier.Held.Should().Be(submissions);
        outcomes.Count(outcome => outcome == SignedIn).Should().Be(1, "the challenge is spent once");
        outcomes.Where(outcome => outcome != SignedIn).Should().OnlyContain(outcome => outcome == InvalidCodeMessage);
        var stored = await database.ReadChallengeAsync(challenge.Token);
        stored.ConsumedAt.Should().NotBeNull();
        stored.AttemptCount.Should().Be(submissions, "each submission spent its attempt before it compared");
        (await database.CountAuditAsync("email_otp.verify_succeeded")).Should().Be(1, "a sign-in that lost the race records nothing");
    }

    [TestMethod]
    public async Task Verification_refuses_to_run_inside_a_database_transaction()
    {
        await using var database = await EmailOtpDatabase.CreateAsync();
        var challenge = await database.StartSignInAsync();

        await using (var actor = database.CreateActor())
        {
            await using var transaction = await actor.Context.Database.BeginTransactionAsync();
            var act = async () => await actor.EmailOtp.VerifyAsync(new SqlOSEmailOtpVerifyRequest(challenge.Token, WrongCode(challenge.Code)));
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(SqlOSEmailOtpAttemptLedger.TransactionRefusedMessage);
        }

        await using (var actor = database.CreateActor())
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            var act = async () => await actor.EmailOtp.VerifyAsync(new SqlOSEmailOtpVerifyRequest(challenge.Token, challenge.Code));
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(SqlOSEmailOtpAttemptLedger.TransactionRefusedMessage);
        }

        var stored = await database.ReadChallengeAsync(challenge.Token);
        stored.AttemptCount.Should().Be(0, "a refused verification compares nothing and spends nothing");
        stored.ConsumedAt.Should().BeNull();
        (await database.VerifyAsync(challenge.Token, challenge.Code)).Should().Be(SignedIn);
    }

    private static string WrongCode(string code)
        => code[..^1] + (code[^1] == '9' ? '0' : (char)(code[^1] + 1));

    private sealed record StartedChallenge(string Token, string Code);

    private sealed class EmailOtpDatabase : IAsyncDisposable
    {
        private const string ClientId = "test-client";

        private readonly string _connectionString;
        private readonly SqlOSAuthServerOptions _options;
        private readonly CodeCapturingEmailSender _sender = new();

        private EmailOtpDatabase(TestSqlOSDbContext context, string connectionString, SqlOSAuthServerOptions options)
        {
            Context = context;
            _connectionString = connectionString;
            _options = options;
        }

        public TestSqlOSDbContext Context { get; }

        public static async Task<EmailOtpDatabase> CreateAsync()
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync("EmailOtpAttempts");
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The email-code database has no connection string.");
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient(ClientId, "Test Client", "https://client.example.test/callback");
            options.SeedAuthPage(page => page.EnabledCredentialTypes = ["password", "email_otp"]);
            options.EmailOtp.MaxAttempts = MaxAttempts;
            options.EmailOtp.BuildMessage = message => new SqlOSAuthEmailMessage(message.Email, "Your code", message.Code, message.Code);

            var database = new EmailOtpDatabase(context, connectionString, options);
            await using var setup = database.CreateActor();
            await setup.Crypto.EnsureActiveSigningKeyAsync();
            await setup.Admin.UpsertSeededClientsAsync();
            _ = await setup.Settings.GetAuthPageSettingsAsync();
            await setup.Settings.UpsertSeededAuthPageSettingsAsync();
            return database;
        }

        /// <summary>Creates an account and starts a sign-in challenge for it, returning the challenge token and the emailed code.</summary>
        public async Task<StartedChallenge> StartSignInAsync()
        {
            var email = $"attempts-{Guid.NewGuid():N}@example.com";
            await using var actor = CreateActor();
            await actor.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Attempt User", email, "P@ssword123!"));
            var started = await actor.EmailOtp.StartForClientAsync(
                new SqlOSEmailOtpStartRequest(email, ClientId, null),
                CreateHttpContext("203.0.113.30"));
            return new StartedChallenge(started.ChallengeToken, _sender.LatestCodeTo(email));
        }

        /// <summary>Verifies once on a fresh unit of work: "signed in", or the refusal's message.</summary>
        public async Task<string> VerifyAsync(string challengeToken, string code)
        {
            await using var actor = CreateActor();
            return await VerifyOnAsync(actor, challengeToken, code);
        }

        /// <summary>
        /// Verifies every code at once, each on its own unit of work and connection, with
        /// <paramref name="barrier"/> holding each at its first write to the challenge until all
        /// have loaded it.
        /// </summary>
        public async Task<IReadOnlyList<string>> VerifyTogetherAsync(
            string challengeToken,
            IEnumerable<string> codes,
            FirstChallengeWriteBarrier barrier)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var verifications = codes.Select(async code =>
            {
                await using var actor = CreateActor(barrier);
                await start.Task;
                return await VerifyOnAsync(actor, challengeToken, code);
            }).ToList();
            barrier.Arm();
            start.SetResult();
            return await Task.WhenAll(verifications);
        }

        public async Task<SqlOSEmailOtpChallenge> ReadChallengeAsync(string challengeToken)
        {
            await using var actor = CreateActor();
            var hash = actor.Crypto.HashToken(challengeToken);
            return await actor.Context.Set<SqlOSEmailOtpChallenge>().AsNoTracking().SingleAsync(x => x.ChallengeTokenHash == hash);
        }

        public async Task<IReadOnlyList<string>> ReadVerifyFailureReasonsAsync()
        {
            await using var actor = CreateActor();
            var metadata = await actor.Context.Set<SqlOSAuditEvent>()
                .AsNoTracking()
                .Where(x => x.EventType == "email_otp.verify_failed")
                .OrderBy(x => x.IngestedAt)
                .Select(x => x.MetadataJson)
                .ToListAsync();
            return metadata
                .Select(json => System.Text.Json.JsonDocument.Parse(json!).RootElement.GetProperty("details").GetProperty("reason").GetString()!)
                .ToList();
        }

        public async Task<int> CountAuditAsync(string eventType)
        {
            await using var actor = CreateActor();
            return await actor.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == eventType);
        }

        public EmailOtpActor CreateActor(IInterceptor? interceptor = null)
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
            return new EmailOtpActor(context, crypto, admin, settings, emailOtp);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }

        private static async Task<string> VerifyOnAsync(EmailOtpActor actor, string challengeToken, string code)
        {
            try
            {
                await actor.EmailOtp.VerifyAsync(new SqlOSEmailOtpVerifyRequest(challengeToken, code));
                return SignedIn;
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }

        private static DefaultHttpContext CreateHttpContext(string ipAddress)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
            context.Request.Headers.UserAgent = "SqlOSEmailOtpAttemptTests";
            return context;
        }
    }

    private sealed class EmailOtpActor(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        SqlOSEmailOtpService emailOtp) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSSettingsService Settings { get; } = settings;
        public SqlOSEmailOtpService EmailOtp { get; } = emailOtp;

        public ValueTask DisposeAsync() => Context.DisposeAsync();
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

    /// <summary>
    /// Holds every unit of work at its first write to the email-code challenges table (an update
    /// on either provider, including SQL Server's set-based <c>UPDATE [c] SET ... FROM [dbo].[...]</c>)
    /// until <c>participants</c> of them are waiting there, or ten seconds pass.
    /// </summary>
    private sealed class FirstChallengeWriteBarrier(int participants) : DbCommandInterceptor
    {
        private static readonly Regex ChallengeUpdate = new(
            @"\bUPDATE\b[\s\S]*\bSqlOSEmailOtpChallenges\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ConcurrentDictionary<Guid, byte> _held = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        public int Held => _held.Count;

        public void Arm() => _armed = true;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await HoldAsync(command, eventData);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await HoldAsync(command, eventData);
            return result;
        }

        private async Task HoldAsync(DbCommand command, CommandEventData eventData)
        {
            if (!_armed
                || eventData.Context is null
                || !ChallengeUpdate.IsMatch(command.CommandText)
                || !_held.TryAdd(eventData.Context.ContextId.InstanceId, 0))
            {
                return;
            }

            if (_held.Count >= participants)
            {
                _released.TrySetResult();
            }

            await Task.WhenAny(_released.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }
    }
}
