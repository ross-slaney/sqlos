using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
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
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// The recovery and MFA processes against real SQL on the configured provider
/// (<c>docs/architecture/domain-model.md</c> §5, §12): the state requests contest is spent once.
/// Each request runs on its own unit of work and connection, as concurrent requests do, and a
/// barrier holds each one at its first write to a temporary token until all of them have loaded it.
/// </summary>
[TestClass]
public sealed class RecoveryAndMfaProcessIntegrationTests
{
    private const string ClientId = "recovery-process-client";
    private const string Password = "P@ssword123!";
    private const string MfaCodeInvalid = "MFA code is invalid.";

    [TestMethod]
    public async Task Concurrent_resets_with_one_link_set_one_password_and_audit_every_refusal()
    {
        const int resets = 4;
        await using var database = await RecoveryDatabase.CreateAsync();
        var (user, link) = await database.CreateUserWithResetLinkAsync();

        var barrier = new FirstTokenWriteBarrier(resets);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, resets).Select(async index =>
        {
            await using var actor = database.CreateActor(barrier);
            await start.Task;
            var newPassword = $"Concurrent-Password-{index}!";
            return (newPassword, await actor.Processes.ResetPassword().ExecuteAsync(new ResetPasswordCommand(link, newPassword), CancellationToken.None));
        }).ToList();
        barrier.Arm();
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        barrier.Held.Should().Be(resets, "every reset loaded the open link before any spent it");
        var winner = outcomes.Where(x => x.Item2 is PasswordResetOutcome.Reset).Should().ContainSingle("a link is spent once").Subject;
        outcomes.Select(x => x.Item2).OfType<PasswordResetOutcome.Refused>().Should().HaveCount(resets - 1)
            .And.OnlyContain(refused => refused.Refusal == ResetPassword.LinkInvalid);

        await using var reader = database.CreateActor();
        (await reader.CountAuditAsync("password_reset.completed")).Should().Be(1);
        (await reader.CountAuditAsync("password_reset.invalid_or_expired")).Should().Be(resets - 1, "every request that lost the link is audited");
        var signIn = await reader.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(user.DefaultEmail!, winner.newPassword, ClientId, null),
            CreateHttpContext());
        signIn.Tokens.Should().NotBeNull("the winner's password is the account's password");
    }

    [TestMethod]
    public async Task Concurrent_wrong_factors_count_every_comparison_and_withdraw_the_challenge_at_its_limit()
    {
        const int guesses = 6;
        const int limit = 3;
        await using var database = await RecoveryDatabase.CreateAsync(options =>
        {
            options.Mfa.Totp.MaxFailedAttemptsPerChallenge = limit;
            options.Mfa.Totp.MaxFailedAttemptsPerUser = 50;
            options.Mfa.Totp.MaxFailedAttemptsPerIp = 50;
        });
        var (user, secret) = await database.CreateEnrolledUserAsync();
        var challenge = await database.DirectLoginChallengeAsync(user);

        // The admission lets the limit's worth of comparisons through; the barrier holds those at
        // their first write to the challenge until all of them have loaded it.
        var barrier = new FirstTokenWriteBarrier(limit);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, guesses).Select(async _ =>
        {
            await using var actor = database.CreateActor(barrier);
            await start.Task;
            try
            {
                var outcome = await actor.Processes.VerifyMfaChallenge(CreateHttpContext()).ExecuteAsync(
                    new VerifyMfaChallengeCommand(challenge, "wrong-code", MfaChallengeTarget.DirectLogin, RequestContext()),
                    CancellationToken.None);
                return outcome is MfaChallengeOutcome.Refused refused ? refused.Refusal.Message : "signed in";
            }
            catch (InvalidOperationException exception)
            {
                // A comparison the admission refused compares nothing.
                return "admission: " + exception.Message;
            }
        }).ToList();
        barrier.Arm();
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        barrier.Held.Should().Be(limit, "every admitted guess loaded the open challenge before any counted its failure");
        outcomes.Count(outcome => outcome == MfaCodeInvalid).Should().Be(limit, "the admitted guesses compared and failed");
        outcomes.Count(outcome => outcome == "admission: " + MfaCodeInvalid).Should().Be(guesses - limit, "the rest were refused before comparing");

        await using var reader = database.CreateActor();
        var stored = await reader.ChallengeAsync(challenge);
        stored.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)!.FailedAttempts.Should().Be(limit, "no failure is lost between concurrent requests");
        stored.ConsumedAt.Should().NotBeNull("the failure that reached the limit withdrew the challenge");
        var failures = await reader.Context.Set<SqlOSAuditEvent>().AsNoTracking()
            .Where(x => x.EventType == "user.mfa.challenge_failed")
            .Select(x => x.MetadataJson!)
            .ToListAsync();
        failures.Should().HaveCount(limit);
        failures.Select(json => Regex.Match(json, "\"attemptCount\":(\\d+)").Groups[1].Value).Should().BeEquivalentTo(["1", "2", "3"]);
        failures.Count(json => json.Contains("\"challengeLocked\":true", StringComparison.Ordinal)).Should().Be(1);

        var right = await reader.Processes.VerifyMfaChallenge(CreateHttpContext()).ExecuteAsync(
            new VerifyMfaChallengeCommand(challenge, database.NextCode(secret), MfaChallengeTarget.DirectLogin, RequestContext()),
            CancellationToken.None);
        right.Should().BeOfType<MfaChallengeOutcome.Refused>().Which.Refusal.Should().Be(MfaChallenges.Invalid);
        (await reader.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    /// <summary>
    /// Each confirmation writes the account's new recovery codes before it spends the enrollment, in
    /// one transaction with the login it completes. On PostgreSQL every confirmation after the first
    /// is refused when it spends the enrollment. On SQL Server, whose reads take locks, the server
    /// instead rolls the confirmations queued behind the first one back as deadlock victims, as
    /// 7.2.1 does: through 7.2.1's public facade, two of three confirmations are deadlock victims in
    /// every round. Either way one login completes, and nothing the others wrote remains.
    /// </summary>
    [TestMethod]
    public async Task Concurrent_confirmations_of_a_challenges_enrollment_complete_one_login()
    {
        const int confirmations = 3;
        await using var database = await RecoveryDatabase.CreateAsync(options =>
        {
            options.Mfa.Enabled = true;
            options.Mfa.RequireForAllUsersByDefault = true;
        });
        var user = await database.CreateUserAsync();
        var challenge = await database.DirectLoginChallengeAsync(user, expectEnrollment: true);
        SqlOSTotpEnrollmentStartResult enrollment;
        await using (var setup = database.CreateActor())
        {
            var started = await setup.Processes.StartTotpEnrollment().ExecuteAsync(
                new StartTotpEnrollmentCommand(new TotpEnrollmentTarget.Challenge(challenge, MfaChallengeTarget.DirectLogin), null, RequestContext()),
                CancellationToken.None);
            enrollment = started.Should().BeOfType<TotpEnrollmentStartOutcome.Started>().Subject.Result;
        }

        var code = database.Code(enrollment.Secret);
        var barrier = new FirstTokenWriteBarrier(confirmations);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, confirmations).Select(async _ =>
        {
            await using var actor = database.CreateActor(barrier);
            await start.Task;
            try
            {
                return (Outcome: await actor.Processes.VerifyTotpEnrollment(CreateHttpContext()).ExecuteAsync(
                    new VerifyTotpEnrollmentCommand(
                        enrollment.EnrollmentToken,
                        code,
                        new TotpEnrollmentTarget.Challenge(challenge, MfaChallengeTarget.DirectLogin),
                        RequestContext()),
                    CancellationToken.None), DeadlockVictim: false);
            }
            catch (Exception exception) when (TestDatabase.IsSqlServer && IsDeadlockVictim(exception))
            {
                return (Outcome: (TotpEnrollmentVerifyOutcome?)null, DeadlockVictim: true);
            }
        }).ToList();
        barrier.Arm();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        barrier.Held.Should().Be(confirmations, "every confirmation loaded the open enrollment before any spent it");
        var signedIn = results.Select(x => x.Outcome).OfType<TotpEnrollmentVerifyOutcome.SignedIn>()
            .Should().ContainSingle("the enrollment and its challenge are spent once").Subject;
        signedIn.Evidence.Methods.Should().Equal("password", "totp");
        results.Where(x => x.Outcome is not TotpEnrollmentVerifyOutcome.SignedIn).Should().HaveCount(confirmations - 1)
            .And.OnlyContain(x => x.DeadlockVictim
                || (x.Outcome is TotpEnrollmentVerifyOutcome.Refused && ((TotpEnrollmentVerifyOutcome.Refused)x.Outcome).Refusal == VerifyTotpEnrollment.ChallengeUsed));

        await using var reader = database.CreateActor();
        (await reader.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(1);
        (await reader.Context.Set<SqlOSUserAuthenticator>().AsNoTracking().SingleAsync(x => x.Id == enrollment.AuthenticatorId))
            .IsConfirmed.Should().BeTrue();
        (await reader.Context.Set<SqlOSRecoveryCode>().CountAsync(x => x.UserId == user.Id && x.RevokedAt == null))
            .Should().Be(signedIn.Result.RecoveryCodes.Count, "the losers' recovery codes rolled back with their transactions");
        (await reader.CountAuditAsync("user.login.mfa")).Should().Be(1);
    }

    /// <summary>True when SQL Server rolled the request back as a deadlock victim (error 1205).</summary>
    private static bool IsDeadlockVictim(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1205 })
            {
                return true;
            }
        }

        return false;
    }

    private static SqlOSRequestContext RequestContext()
        => new(SqlOSRequestSurface.PublicApi, "203.0.113.41", "RecoveryProcessTests", RequestId: null, CorrelationId: null);

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tests");
        context.Request.Headers.UserAgent = "RecoveryProcessTests";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.41");
        return context;
    }

    private sealed class RecoveryDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqlOSAuthServerOptions _options;
        private readonly TestAuthEmailSender _sender = new();

        private RecoveryDatabase(TestSqlOSDbContext context, string connectionString, SqlOSAuthServerOptions options)
        {
            Context = context;
            _connectionString = connectionString;
            _options = options;
        }

        private TestSqlOSDbContext Context { get; }

        public static async Task<RecoveryDatabase> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync("RecoveryProcesses");
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The recovery database has no connection string.");
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient(ClientId, "Recovery Client", "https://client.example.test/callback");
            options.Mfa.Enabled = true;
            options.Mfa.AllowUserSelfEnrollmentByDefault = true;
            options.Mfa.RecoveryCodesEnabledByDefault = true;
            configure?.Invoke(options);

            var database = new RecoveryDatabase(context, connectionString, options);
            await using var setup = database.CreateActor();
            await setup.Crypto.EnsureActiveSigningKeyAsync();
            await setup.Admin.UpsertSeededClientsAsync();
            await setup.Settings.UpsertSeededAuthPageSettingsAsync();
            await setup.Settings.UpsertSeededMfaSettingsAsync();
            await new SqlOSEmailAdminService(setup.Context, setup.Crypto, new SqlOSEmailTemplateRenderer()).EnsureBuiltInTemplatesAsync();
            return database;
        }

        public async Task<SqlOSUser> CreateUserAsync()
        {
            await using var actor = CreateActor();
            return await actor.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Recovery User",
                $"recovery-{Guid.NewGuid():N}@example.com",
                Password));
        }

        public async Task<(SqlOSUser User, string Link)> CreateUserWithResetLinkAsync()
        {
            var user = await CreateUserAsync();
            await using var actor = CreateActor();
            var issued = await actor.Processes.IssuePasswordResetToken().ExecuteAsync(
                new IssuePasswordResetTokenCommand(user.DefaultEmail!, ClientId: null),
                CancellationToken.None);
            return (user, issued.Should().BeOfType<PasswordResetTokenOutcome.Issued>().Subject.RawToken);
        }

        public async Task<(SqlOSUser User, string Secret)> CreateEnrolledUserAsync()
        {
            var user = await CreateUserAsync();
            await using var actor = CreateActor();
            var enrollment = await actor.Authenticators.StartEnrollmentAsync(user.Id, displayName: "Phone");
            await actor.Authenticators.VerifyEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, Code(enrollment.Secret)));
            return (user, enrollment.Secret);
        }

        /// <summary>A first-party client's direct login of <paramref name="user"/>, paused at its MFA challenge.</summary>
        public async Task<string> DirectLoginChallengeAsync(SqlOSUser user, bool expectEnrollment = false)
        {
            await using var actor = CreateActor();
            var login = await actor.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, Password, ClientId, null),
                CreateHttpContext());
            login.RequiresMfa.Should().BeTrue();
            login.RequiresMfaEnrollment.Should().Be(expectEnrollment);
            return login.MfaToken!;
        }

        public string Code(string secret)
        {
            using var actor = CreateActor();
            return actor.Authenticators.GenerateCodeForTesting(secret);
        }

        /// <summary>The code of the next time step: the step an enrollment accepted cannot be used again.</summary>
        public string NextCode(string secret)
        {
            using var actor = CreateActor();
            return actor.Authenticators.GenerateCodeForTesting(secret, DateTimeOffset.UtcNow.AddSeconds(_options.Mfa.Totp.PeriodSeconds));
        }

        public RecoveryActor CreateActor(IInterceptor? interceptor = null)
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
            var transactional = new SqlOSTransactionalEmailService(context, crypto, _sender, new SqlOSEmailTemplateRenderer(), Options.Create(new SqlOSEmailOptions()));
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, _sender, options, transactional);
            var policy = new SqlOSMfaPolicyService(context, settings, options);
            var authenticators = new SqlOSTotpMfaService(context, crypto, policy, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                transactionalEmailService: transactional,
                authEmailSender: _sender,
                mfaPolicyService: policy,
                totpMfaService: authenticators);
            var processes = new SqlOSIdentityProcesses(context, admin, crypto, settings, _options)
            {
                PasswordResetAdmission = auth.Admission,
                MfaAdmission = auth.Admission,
                PasswordResetEmails = auth.PasswordResetEmails,
                VerificationEmails = auth.VerificationEmails,
                Authenticators = authenticators,
                Auth = auth
            };
            return new RecoveryActor(context, crypto, admin, settings, authenticators, auth, processes);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class RecoveryActor(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSSettingsService settings,
        SqlOSTotpMfaService authenticators,
        SqlOSAuthService auth,
        SqlOSIdentityProcesses processes) : IAsyncDisposable, IDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSSettingsService Settings { get; } = settings;
        public SqlOSTotpMfaService Authenticators { get; } = authenticators;
        public SqlOSAuthService Auth { get; } = auth;
        public SqlOSIdentityProcesses Processes { get; } = processes;

        public Task<int> CountAuditAsync(string eventType)
            => Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == eventType);

        public Task<SqlOSTemporaryToken> ChallengeAsync(string rawToken)
        {
            var hash = Crypto.HashToken(rawToken);
            return Context.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.TokenHash == hash);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();

        public void Dispose() => Context.Dispose();
    }

    /// <summary>
    /// Holds each unit of work at its first write to a temporary token until
    /// <paramref name="participants"/> units of work have reached theirs: every concurrent request has
    /// loaded the token before any of them changes it.
    /// </summary>
    private sealed class FirstTokenWriteBarrier(int participants) : DbCommandInterceptor
    {
        private static readonly Regex TokenUpdate = new(
            @"\bUPDATE\b[\s\S]*\bSqlOSTemporaryTokens\b",
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
                || !TokenUpdate.IsMatch(command.CommandText)
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
