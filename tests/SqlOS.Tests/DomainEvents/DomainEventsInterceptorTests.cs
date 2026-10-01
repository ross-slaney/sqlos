using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Database;
using SqlOS.Domain;

namespace SqlOS.Tests.DomainEvents;

/// <summary>
/// The domain-event pipeline on a relational provider with real transactions (SQLite).
/// <c>DomainEventPipelineIntegrationTests</c> proves the same on SQL Server and PostgreSQL.
/// </summary>
[TestClass]
public sealed class DomainEventsInterceptorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task An_event_writes_exactly_one_audit_row_in_the_same_save()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;
        var account = new LedgerAccount("acc_1");
        account.Deposit(25);
        context.Add(account);

        await context.SaveChangesAsync();

        var row = (await pipeline.ReadRowsAsync()).Should().ContainSingle().Subject;
        row.Id.Should().MatchRegex("^evt_[0-9a-f]{24}$");
        row.EventType.Should().Be("ledger.deposited");
        row.Source.Should().Be("authserver");
        row.MetadataJson.Should().Be("{\"accountId\":\"acc_1\",\"amount\":25}");
        row.OccurredAt.Should().Be(Now.UtcDateTime, "the save reads the scope's clock once");
        row.IngestedAt.Should().Be(row.OccurredAt);
        account.PendingEvents.Should().BeEmpty();

        await context.SaveChangesAsync();
        (await pipeline.ReadRowsAsync()).Should().HaveCount(1, "a drained event is never projected twice");
    }

    [TestMethod]
    public async Task Rows_follow_the_order_events_were_raised_across_aggregates_and_failure_records()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;
        var a = new LedgerAccount("a");
        var b = new LedgerAccount("b");
        context.Add(b);
        context.Add(a);

        a.Deposit(1);
        scope.Recorder.Record(new DepositRejected("a", "limit"));
        b.Deposit(2);
        a.Deposit(3);
        await context.SaveChangesAsync();

        var rows = (await pipeline.ReadRowsAsync()).OrderBy(row => row.OccurredAt).ToList();
        rows.Select(row => row.MetadataJson).Should().Equal(
            "{\"accountId\":\"a\",\"amount\":1}",
            "{\"accountId\":\"a\",\"reason\":\"limit\"}",
            "{\"accountId\":\"b\",\"amount\":2}",
            "{\"accountId\":\"a\",\"amount\":3}");
        rows.Select(row => row.OccurredAt).Should().Equal(
            Now.UtcDateTime,
            Now.UtcDateTime.AddTicks(10),
            Now.UtcDateTime.AddTicks(20),
            Now.UtcDateTime.AddTicks(30));
    }

    [TestMethod]
    public async Task Rows_carry_the_request_context_of_the_saving_scope()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlOSRequestContextAccessor>().Current =
            new SqlOSRequestContext(SqlOSRequestSurface.Hosted, "203.0.113.10", "Mozilla/5.0", "req-1", "corr-1");
        var account = new LedgerAccount("acc_ip");
        account.Deposit(5);
        scope.Context.Add(account);

        await scope.Context.SaveChangesAsync();

        var row = (await pipeline.ReadRowsAsync()).Should().ContainSingle().Subject;
        row.IpAddress.Should().Be("203.0.113.10");
        row.ContextJson.Should().Be("{\"ipAddress\":\"203.0.113.10\",\"userAgent\":null,\"sessionId\":null,\"requestId\":null,\"correlationId\":null}");
    }

    [TestMethod]
    public async Task A_failed_save_writes_no_row_and_leaves_the_unit_of_work_as_it_was()
    {
        await using var pipeline = new Pipeline();
        await using (var seed = pipeline.CreateScope())
        {
            seed.Context.Add(new LedgerAccount("taken"));
            await seed.Context.SaveChangesAsync();
        }

        await using var scope = pipeline.CreateScope();
        var context = scope.Context;
        var account = new LedgerAccount("acc_fail");
        account.Deposit(10);
        context.Add(account);
        var conflicting = new LedgerAccount("taken");
        context.Add(conflicting);

        await FluentActions.Invoking(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        context.ChangeTracker.Entries<SqlOSAuditEvent>().Should().BeEmpty("the failed save's rows are detached");
        account.PendingEvents.Should().Equal(new Deposited("acc_fail", 10));

        context.Entry(conflicting).State = EntityState.Detached;
        await context.SaveChangesAsync();

        (await pipeline.ReadRowsAsync()).Should().ContainSingle(row => row.MetadataJson == "{\"accountId\":\"acc_fail\",\"amount\":10}");
    }

    [TestMethod]
    public async Task A_canceled_save_writes_no_row_and_keeps_the_events()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_cancel");
        account.Deposit(1);
        scope.Context.Add(account);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await FluentActions.Invoking(() => scope.Context.SaveChangesAsync(canceled.Token)).Should().ThrowAsync<OperationCanceledException>();

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        account.PendingEvents.Should().ContainSingle();
        scope.Context.ChangeTracker.Entries<SqlOSAuditEvent>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_save_retried_after_a_concurrency_conflict_writes_each_row_once()
    {
        await using var pipeline = new Pipeline();
        await pipeline.SeedAccountAsync("acc_contested");
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;
        var account = await context.Set<LedgerAccount>().SingleAsync(candidate => candidate.Id == "acc_contested");
        account.Deposit(5);
        await pipeline.BumpVersionAsync("acc_contested");

        // EF reports a concurrency conflict to no interceptor: the next save undoes this one first.
        var conflict = await FluentActions.Invoking(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await pipeline.ReadRowsAsync()).Should().BeEmpty();

        var entry = conflict.Which.Entries.Should().ContainSingle().Subject;
        entry.OriginalValues.SetValues((await entry.GetDatabaseValuesAsync())!);
        await context.SaveChangesAsync();

        (await pipeline.ReadRowsAsync()).Select(row => row.MetadataJson).Should().Equal("{\"accountId\":\"acc_contested\",\"amount\":5}");
        context.ChangeTracker.Entries<SqlOSAuditEvent>().Should().ContainSingle();
        pipeline.Log.Entries.Should().Equal(new HandlerEntry("deposited acc_contested:5", InTransaction: false));
    }

    [TestMethod]
    public async Task A_unit_of_work_discarded_after_a_concurrency_conflict_writes_none_of_its_rows()
    {
        await using var pipeline = new Pipeline();
        await pipeline.SeedAccountAsync("acc_lost");
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;
        var account = await context.Set<LedgerAccount>().SingleAsync(candidate => candidate.Id == "acc_lost");
        account.Deposit(5);
        await pipeline.BumpVersionAsync("acc_lost");
        await FluentActions.Invoking(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();

        context.ChangeTracker.Clear();
        scope.Recorder.Record(new DepositRejected("acc_lost", "conflict"));
        await context.SaveChangesAsync();

        (await pipeline.ReadRowsAsync()).Select(row => row.EventType).Should().Equal("ledger.deposit_rejected");
        pipeline.Log.Entries.Should().BeEmpty("the discarded deposit never committed");
    }

    [TestMethod]
    public async Task Failure_records_a_pooled_context_never_saved_do_not_reach_its_next_lease()
    {
        using var connection = PipelineDbContext.OpenDatabase();
        var services = new ServiceCollection();
        services.AddDbContextPool<PipelineDbContext>(options => options.UseSqlite(connection));
        services.ConfigureDbContext<PipelineDbContext>(
            static (_, options) => SqlOSDomainEventsInterceptor.AttachTo(options),
            ServiceLifetime.Singleton);
        services.AddScoped<ISqlOSAuthServerDbContext>(provider => provider.GetRequiredService<PipelineDbContext>());
        services.AddSingleton(PipelineProjections.Standard().Build());
        services.AddScoped<IAuditRecorder, SqlOSAuditRecorder>();
        await using var provider = services.BuildServiceProvider();
        DbContextId firstLease;
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
            await context.Database.EnsureCreatedAsync();
            firstLease = context.ContextId;
            scope.ServiceProvider.GetRequiredService<IAuditRecorder>().Record(new DepositRejected("acc", "never saved"));
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
            context.ContextId.InstanceId.Should().Be(firstLease.InstanceId, "the pool hands the same context to the next scope");
            context.ContextId.Lease.Should().NotBe(firstLease.Lease);
            SqlOSUnitOfWorkEvents.TryGet(context, out _).Should().BeFalse();
            context.Add(new LedgerAccount("next_lease"));

            await context.SaveChangesAsync();

            (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(0);
        }
    }

    [TestMethod]
    public async Task A_rolled_back_transaction_keeps_no_row_and_runs_no_handler()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_rollback");
        account.Deposit(7);
        scope.Context.Add(account);

        await using (var transaction = await scope.Context.Database.BeginTransactionAsync())
        {
            await scope.Context.SaveChangesAsync();
            pipeline.Log.Entries.Should().BeEmpty("the transaction has not committed");
            await transaction.RollbackAsync();
        }

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        pipeline.Log.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Post_commit_handlers_run_after_the_save_commits_in_raise_order()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var a = new LedgerAccount("a");
        var b = new LedgerAccount("b");
        scope.Context.AddRange(a, b);
        b.Deposit(2);
        a.Deposit(1);
        a.Raise(new Silent("a"));

        await scope.Context.SaveChangesAsync();

        pipeline.Log.Entries.Should().Equal(
            new HandlerEntry("deposited b:2", InTransaction: false),
            new HandlerEntry("deposited a:1", InTransaction: false),
            new HandlerEntry("silent a", InTransaction: false));
        (await pipeline.ReadRowsAsync()).Should().HaveCount(2, "an unaudited event writes no row but still reaches its handlers");
    }

    [TestMethod]
    public async Task Post_commit_handlers_wait_for_the_callers_transaction_to_commit()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_tx");
        scope.Context.Add(account);

        await using var transaction = await scope.Context.Database.BeginTransactionAsync();
        account.Deposit(1);
        await scope.Context.SaveChangesAsync();
        account.Deposit(2);
        await scope.Context.SaveChangesAsync();
        pipeline.Log.Entries.Should().BeEmpty();

        await transaction.CommitAsync();

        pipeline.Log.Entries.Should().Equal(
            new HandlerEntry("deposited acc_tx:1", InTransaction: false),
            new HandlerEntry("deposited acc_tx:2", InTransaction: false));
        (await pipeline.ReadRowsAsync()).Should().HaveCount(2);
    }

    [TestMethod]
    public async Task Post_commit_handlers_run_in_their_own_scope_with_the_request_context()
    {
        await using var pipeline = new Pipeline(configure: services => services.AddScoped<ISqlOSPostCommitHandler<DepositRejected>, ScopeCapturingHandler>());
        await using var scope = pipeline.CreateScope();
        scope.ServiceProvider.GetRequiredService<SqlOSRequestContextAccessor>().Current =
            new SqlOSRequestContext(SqlOSRequestSurface.Headless, "198.51.100.7", null, null, null);
        scope.Recorder.Record(new DepositRejected("acc", "limit"));

        await scope.Context.SaveChangesAsync();

        var captured = pipeline.Captured.Should().ContainSingle().Subject;
        captured.Context.Should().NotBeSameAs(scope.Context, "a handler never re-enters the context that saved");
        captured.Request.Should().Be(new SqlOSRequestContext(SqlOSRequestSurface.Headless, "198.51.100.7", null, null, null));
    }

    [TestMethod]
    public async Task A_handler_that_throws_is_logged_and_the_other_handlers_still_run()
    {
        await using var pipeline = new Pipeline(configure: services => services
            .AddScoped<ISqlOSPostCommitHandler<DepositRejected>, ThrowingHandler>()
            .AddScoped<ISqlOSPostCommitHandler<DepositRejected>, RejectionHandler>());
        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_throw");
        scope.Context.Add(account);
        scope.Recorder.Record(new DepositRejected("acc_throw", "limit"));
        account.Deposit(5);

        await scope.Context.SaveChangesAsync();

        (await pipeline.ReadRowsAsync()).Should().HaveCount(2, "the change committed before any handler ran");
        pipeline.Log.Entries.Should().Equal(
            new HandlerEntry("rejected acc_throw:limit", InTransaction: false),
            new HandlerEntry("deposited acc_throw:5", InTransaction: false));
        var failure = pipeline.Logged.Should().ContainSingle().Subject;
        failure.Level.Should().Be(LogLevel.Error);
        failure.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("handler failed");
        failure.Message.Should().Be(
            $"Post-commit handler {typeof(ThrowingHandler).FullName} failed for domain event {typeof(DepositRejected).FullName}. The change it reacts to is committed.");
    }

    [TestMethod]
    public async Task A_handler_that_throws_at_commit_leaves_the_commit_complete_and_the_context_usable()
    {
        await using var pipeline = new Pipeline(configure: services => services.AddScoped<ISqlOSPostCommitHandler<DepositRejected>, ThrowingHandler>());
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            scope.Recorder.Record(new DepositRejected("acc", "limit"));
            await context.SaveChangesAsync();

            await transaction.CommitAsync();

            context.Database.CurrentTransaction.Should().BeNull("EF finished the commit");
        }

        (await pipeline.ReadRowsAsync()).Should().ContainSingle();
        pipeline.Logged.Should().ContainSingle();
        await using var next = await context.Database.BeginTransactionAsync();
    }

    [TestMethod]
    public async Task A_synchronous_save_writes_its_rows_and_runs_handlers_after_it_commits()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_sync");
        account.Deposit(4);
        scope.Context.Add(account);

        scope.Context.SaveChanges();

        (await pipeline.ReadRowsAsync()).Select(row => row.MetadataJson).Should().Equal("{\"accountId\":\"acc_sync\",\"amount\":4}");
        pipeline.Log.Entries.Should().Equal(new HandlerEntry("deposited acc_sync:4", InTransaction: false));
        account.PendingEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_failed_synchronous_save_leaves_the_unit_of_work_as_it_was()
    {
        await using var pipeline = new Pipeline();
        await using (var seed = pipeline.CreateScope())
        {
            seed.Context.Add(new LedgerAccount("taken"));
            await seed.Context.SaveChangesAsync();
        }

        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_sync_fail");
        account.Deposit(10);
        scope.Context.AddRange(account, new LedgerAccount("taken"));

        scope.Context.Invoking(context => context.SaveChanges()).Should().Throw<DbUpdateException>();

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        scope.Context.ChangeTracker.Entries<SqlOSAuditEvent>().Should().BeEmpty();
        account.PendingEvents.Should().Equal(new Deposited("acc_sync_fail", 10));
        pipeline.Log.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_synchronous_transaction_runs_handlers_on_commit_and_none_on_rollback()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;

        using (var transaction = context.Database.BeginTransaction())
        {
            var discarded = new LedgerAccount("acc_discarded");
            discarded.Deposit(1);
            context.Add(discarded);
            context.SaveChanges();
            transaction.Rollback();
        }

        context.ChangeTracker.Clear();
        using (var transaction = context.Database.BeginTransaction())
        {
            var kept = new LedgerAccount("acc_kept");
            kept.Deposit(2);
            context.Add(kept);
            context.SaveChanges();
            pipeline.Log.Entries.Should().BeEmpty("the transaction has not committed");
            transaction.Commit();
        }

        pipeline.Log.Entries.Should().Equal(new HandlerEntry("deposited acc_kept:2", InTransaction: false));
        (await pipeline.ReadRowsAsync()).Select(row => row.MetadataJson).Should().Equal("{\"accountId\":\"acc_kept\",\"amount\":2}");
    }

    [TestMethod]
    public async Task A_transaction_that_ends_without_committing_hands_nothing_to_the_next_one()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;

        await using (await context.Database.BeginTransactionAsync())
        {
            var abandoned = new LedgerAccount("acc_abandoned");
            abandoned.Deposit(1);
            context.Add(abandoned);
            await context.SaveChangesAsync();

            // Disposed without a commit or a rollback, which EF reports to no interceptor.
        }

        context.ChangeTracker.Clear();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var next = new LedgerAccount("acc_next");
            next.Deposit(2);
            context.Add(next);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        pipeline.Log.Entries.Should().Equal(new HandlerEntry("deposited acc_next:2", InTransaction: false));
        (await pipeline.ReadRowsAsync()).Select(row => row.MetadataJson).Should().Equal("{\"accountId\":\"acc_next\",\"amount\":2}");
    }

    [TestMethod]
    public async Task A_commit_that_fails_keeps_its_events_for_a_commit_that_succeeds()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var context = scope.Context;

        // SQLite checks a deferred foreign key at COMMIT, and keeps the transaction open when it fails.
        await context.Database.ExecuteSqlRawAsync("""
            PRAGMA foreign_keys = ON;
            CREATE TABLE Parents (Id INTEGER PRIMARY KEY);
            CREATE TABLE Children (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES Parents (Id) DEFERRABLE INITIALLY DEFERRED);
            """);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var account = new LedgerAccount("acc_retry");
        account.Deposit(3);
        context.Add(account);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("INSERT INTO Children (Id, ParentId) VALUES (1, 404);");

        await FluentActions.Invoking(() => transaction.CommitAsync()).Should().ThrowAsync<SqliteException>();
        pipeline.Log.Entries.Should().BeEmpty("nothing has committed");

        await context.Database.ExecuteSqlRawAsync("DELETE FROM Children;");
        await transaction.CommitAsync();

        pipeline.Log.Entries.Should().Equal(new HandlerEntry("deposited acc_retry:3", InTransaction: false));
        (await pipeline.ReadRowsAsync()).Should().ContainSingle();
    }

    [TestMethod]
    public async Task An_event_raised_while_projecting_is_drained_in_the_same_save()
    {
        LedgerAccount? account = null;
        var projection = new SqlOSAuditProjectionBuilder()
            .Audit<Deposited>((domainEvent, context) =>
            {
                account!.Raise(new DepositAudited(domainEvent.AccountId));
                return Row("ledger.deposited", context);
            })
            .Audit<DepositAudited>((_, context) => Row("ledger.deposit_audited", context))
            .Build();
        await using var pipeline = new Pipeline(projection);
        await using var scope = pipeline.CreateScope();
        account = new LedgerAccount("acc_chain");
        account.Deposit(3);
        scope.Context.Add(account);

        await scope.Context.SaveChangesAsync();

        (await pipeline.ReadRowsAsync()).OrderBy(row => row.OccurredAt).Select(row => row.EventType)
            .Should().Equal("ledger.deposited", "ledger.deposit_audited");
        account.PendingEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_projection_that_keeps_raising_events_fails_the_save_without_writing()
    {
        LedgerAccount? account = null;
        var projection = new SqlOSAuditProjectionBuilder()
            .Audit<Deposited>((domainEvent, context) =>
            {
                account!.Deposit(domainEvent.Amount);
                return Row("ledger.deposited", context);
            })
            .Build();
        await using var pipeline = new Pipeline(projection);
        await using var scope = pipeline.CreateScope();
        account = new LedgerAccount("acc_loop");
        account.Deposit(1);
        scope.Context.Add(account);

        await FluentActions.Invoking(() => scope.Context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*kept raising new events*");

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        scope.Context.ChangeTracker.Entries<SqlOSAuditEvent>().Should().BeEmpty();
        account.PendingEvents.Should().Equal(new[] { new Deposited("acc_loop", 1) }, "only the events raised before the save come back");
    }

    [TestMethod]
    public async Task An_unregistered_event_fails_the_save_and_restores_the_unit_of_work()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        var account = new LedgerAccount("acc_unregistered");
        account.Deposit(1);
        account.Raise(new Unregistered("acc_unregistered"));
        scope.Context.Add(account);

        await FluentActions.Invoking(() => scope.Context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No audit projection is registered for domain event 'SqlOS.Tests.DomainEvents.Unregistered'*");

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        (await pipeline.CountAccountsAsync()).Should().Be(0, "the save was never attempted");
        account.PendingEvents.Should().HaveCount(2);
        scope.Context.ChangeTracker.Entries<SqlOSAuditEvent>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task A_context_built_without_dependency_injection_uses_sqlos_defaults()
    {
        using var connection = PipelineDbContext.OpenDatabase();
        await using var context = PipelineDbContext.Create(connection);
        context.Add(new LedgerAccount("plain"));

        await context.SaveChangesAsync();
        var account = new LedgerAccount("plain_event");
        account.Deposit(1);
        context.Add(account);

        // SqlOSAuditProjection.Default registers no events yet, so a test event is unregistered.
        await FluentActions.Invoking(() => context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("No audit projection is registered*");
        (await context.Set<LedgerAccount>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public void Attaching_is_idempotent()
    {
        using var connection = PipelineDbContext.OpenDatabase();
        var builder = new DbContextOptionsBuilder<PipelineDbContext>().UseSqlite(connection);

        SqlOSDomainEventsInterceptor.AttachTo(builder);
        SqlOSDomainEventsInterceptor.AttachTo(builder);
        var attached = builder.Options;

        CountInterceptors(attached).Should().Be(1);
        SqlOSDomainEventsInterceptor.AttachTo(attached).Should().BeSameAs(attached);
        var bare = new DbContextOptionsBuilder<PipelineDbContext>().UseSqlite(connection).Options;
        var copy = SqlOSDomainEventsInterceptor.AttachTo(bare);
        copy.Should().NotBeSameAs(bare);
        CountInterceptors(copy).Should().Be(1);
        CountInterceptors(bare).Should().Be(0);
    }

    [TestMethod]
    public async Task Configuring_the_context_twice_still_attaches_one_interceptor()
    {
        await using var pipeline = new Pipeline(configure: services => services.ConfigureDbContext<PipelineDbContext>(
            static (_, options) => SqlOSDomainEventsInterceptor.AttachTo(options),
            ServiceLifetime.Singleton));
        await using var scope = pipeline.CreateScope();

        CountInterceptors(scope.Context.GetService<IDbContextOptions>()).Should().Be(1);
    }

    [TestMethod]
    public async Task A_save_without_events_adds_nothing()
    {
        await using var pipeline = new Pipeline();
        await using var scope = pipeline.CreateScope();
        scope.Context.Add(new LedgerAccount("quiet"));

        await scope.Context.SaveChangesAsync();

        (await pipeline.ReadRowsAsync()).Should().BeEmpty();
        pipeline.Log.Entries.Should().BeEmpty();
    }

    private static int CountInterceptors(IDbContextOptions options)
        => options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Count(interceptor => interceptor is SqlOSDomainEventsInterceptor) ?? 0;

    private static SqlOSAuditEvent Row(string eventType, SqlOSAuditProjectionContext context)
        => SqlOSAuditRows.Create(SqlOSAuditRows.AuthServerRequest(eventType, "system", null), SqlOSIds.New("evt"), context.Now);

    internal sealed record HandlerEntry(string Description, bool InTransaction);

    internal sealed class HandlerLog
    {
        private readonly List<HandlerEntry> _entries = [];

        public IReadOnlyList<HandlerEntry> Entries => _entries;

        public void Add(string description, DbContext context)
            => _entries.Add(new HandlerEntry(description, context.Database.CurrentTransaction is not null));
    }

    private sealed class DepositedHandler(HandlerLog log, PipelineDbContext context) : ISqlOSPostCommitHandler<Deposited>
    {
        public Task HandleAsync(Deposited domainEvent, CancellationToken cancellationToken)
        {
            log.Add($"deposited {domainEvent.AccountId}:{domainEvent.Amount}", context);
            return Task.CompletedTask;
        }
    }

    private sealed class SilentHandler(HandlerLog log, PipelineDbContext context) : ISqlOSPostCommitHandler<Silent>
    {
        public Task HandleAsync(Silent domainEvent, CancellationToken cancellationToken)
        {
            log.Add($"silent {domainEvent.AccountId}", context);
            return Task.CompletedTask;
        }
    }

    internal sealed record CapturedScope(DbContext Context, SqlOSRequestContext Request);

    private sealed class ScopeCapturingHandler(
        List<CapturedScope> captured,
        PipelineDbContext context,
        SqlOSRequestContextAccessor requestContext) : ISqlOSPostCommitHandler<DepositRejected>
    {
        public Task HandleAsync(DepositRejected domainEvent, CancellationToken cancellationToken)
        {
            captured.Add(new CapturedScope(context, requestContext.Current));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : ISqlOSPostCommitHandler<DepositRejected>
    {
        public Task HandleAsync(DepositRejected domainEvent, CancellationToken cancellationToken)
            => throw new InvalidOperationException("handler failed");
    }

    private sealed class RejectionHandler(HandlerLog log, PipelineDbContext context) : ISqlOSPostCommitHandler<DepositRejected>
    {
        public Task HandleAsync(DepositRejected domainEvent, CancellationToken cancellationToken)
        {
            log.Add($"rejected {domainEvent.AccountId}:{domainEvent.Reason}", context);
            return Task.CompletedTask;
        }
    }

    internal sealed record LoggedEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<SqlOSPostCommitDispatch>
    {
        public List<LoggedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LoggedEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A service provider wired like AddSqlOS wires the pipeline, on in-memory SQLite.</summary>
    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = PipelineDbContext.OpenDatabase();
        private readonly ServiceProvider _services;

        public Pipeline(SqlOSAuditProjection? projection = null, Action<IServiceCollection>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddDbContext<PipelineDbContext>(options => options.UseSqlite(_connection));
            services.ConfigureDbContext<PipelineDbContext>(
                static (_, options) => SqlOSDomainEventsInterceptor.AttachTo(options),
                ServiceLifetime.Singleton);
            services.AddScoped<ISqlOSAuthServerDbContext>(provider => provider.GetRequiredService<PipelineDbContext>());
            services.AddSingleton(projection ?? PipelineProjections.Standard().Build());
            services.AddSingleton<TimeProvider>(new FixedTime(Now));
            services.AddScoped<SqlOSRequestContextAccessor>();
            services.AddScoped<IAuditRecorder, SqlOSAuditRecorder>();
            services.AddSingleton<HandlerLog>();
            services.AddSingleton<List<CapturedScope>>();
            services.AddSingleton<CapturingLogger>();
            services.AddSingleton<ILogger<SqlOSPostCommitDispatch>>(provider => provider.GetRequiredService<CapturingLogger>());
            services.AddScoped<ISqlOSPostCommitHandler<Deposited>, DepositedHandler>();
            services.AddScoped<ISqlOSPostCommitHandler<Silent>, SilentHandler>();
            configure?.Invoke(services);
            _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

            using var scope = _services.CreateScope();
            scope.ServiceProvider.GetRequiredService<PipelineDbContext>().Database.EnsureCreated();
        }

        public HandlerLog Log => _services.GetRequiredService<HandlerLog>();

        public IReadOnlyList<CapturedScope> Captured => _services.GetRequiredService<List<CapturedScope>>();

        public IReadOnlyList<LoggedEntry> Logged => _services.GetRequiredService<CapturingLogger>().Entries;

        public Scope CreateScope() => new(_services.CreateAsyncScope());

        public async Task<List<SqlOSAuditEvent>> ReadRowsAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PipelineDbContext>().Set<SqlOSAuditEvent>().AsNoTracking().ToListAsync();
        }

        public async Task<int> CountAccountsAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PipelineDbContext>().Set<LedgerAccount>().CountAsync();
        }

        public async Task SeedAccountAsync(string id)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
            context.Add(new LedgerAccount(id));
            await context.SaveChangesAsync();
        }

        /// <summary>Another writer changes the account, so a save holding the old version conflicts.</summary>
        public async Task BumpVersionAsync(string id)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PipelineDbContext>().Set<LedgerAccount>()
                .Where(account => account.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Version, account => account.Version + 1));
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class Scope(AsyncServiceScope scope) : IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => scope.ServiceProvider;

        public PipelineDbContext Context => scope.ServiceProvider.GetRequiredService<PipelineDbContext>();

        public IAuditRecorder Recorder => scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
