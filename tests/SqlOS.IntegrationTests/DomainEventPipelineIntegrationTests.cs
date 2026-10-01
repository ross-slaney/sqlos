using System.Transactions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Database;
using SqlOS.Domain;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// The domain-event pipeline against real SQL (SQL Server, or PostgreSQL with
/// <c>SQLOS_TEST_PROVIDER=postgresql</c>), with a test-only aggregate: rows commit or roll back
/// with the change, post-commit handlers see committed data, and the interceptor is attached once
/// however the host registers its context.
/// </summary>
[TestClass]
public sealed class DomainEventPipelineIntegrationTests
{
    private const string Issuer = "https://pipeline.integration.test/sqlos/auth";
    private static string _databaseName = null!;
    private static string _connectionString = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _databaseName = $"SqlOSPipeline_{Guid.NewGuid():N}";
        await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, _databaseName);
        _connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, _databaseName);
        await using var context = CreateManualContext();

        // EnsureCreated makes the probe table; SqlOS tables are excluded from EF migrations and come
        // from SqlOS's own schema scripts, as in every host.
        await context.Database.EnsureCreatedAsync();
        await new SqlOSSchemaInitializer(
                context,
                Microsoft.Extensions.Options.Options.Create(AspireFixture.Options),
                NullLogger<SqlOSSchemaInitializer>.Instance)
            .EnsureSchemaAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_databaseName != null)
        {
            TestDatabase.ClearPools();
            await TestDatabase.DropDatabaseAsync(AspireFixture.SqlConnectionString, _databaseName);
        }
    }

    [TestMethod]
    public async Task An_event_writes_exactly_one_audit_row_in_the_same_transaction_as_its_change()
    {
        await using var services = CreateHostServices(ProbeModel.Projection());
        var accountId = NewId();
        await using (var scope = services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<SqlOSRequestContextAccessor>().Current =
                new SqlOSRequestContext(SqlOSRequestSurface.Hosted, "203.0.113.10", "Mozilla/5.0", "req-1", null);
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            var account = new ProbeAccount(accountId);
            account.Deposit(25);
            context.Add(account);

            await context.SaveChangesAsync();
        }

        var rows = await ReadRowsAsync(accountId);
        var row = rows.Should().ContainSingle().Subject;
        row.EventType.Should().Be("probe.deposited");
        row.Source.Should().Be("authserver");
        row.IpAddress.Should().Be("203.0.113.10");
        row.MetadataJson.Should().Be($"{{\"accountId\":\"{accountId}\",\"amount\":25}}");
        row.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        (await AccountExistsAsync(accountId)).Should().BeTrue();
    }

    [TestMethod]
    public async Task A_rolled_back_transaction_keeps_neither_the_change_nor_its_row()
    {
        await using var services = CreateHostServices(ProbeModel.Projection());
        var accountId = NewId();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var account = new ProbeAccount(accountId);
            account.Deposit(5);
            context.Add(account);
            await context.SaveChangesAsync();

            (await context.Set<SqlOSAuditEvent>().CountAsync(row => row.MetadataJson!.Contains(accountId)))
                .Should().Be(1, "inside the transaction the row is already written");

            await transaction.RollbackAsync();
        }

        (await ReadRowsAsync(accountId)).Should().BeEmpty();
        (await AccountExistsAsync(accountId)).Should().BeFalse();
    }

    [TestMethod]
    public async Task A_failed_save_leaves_no_row()
    {
        await using var services = CreateHostServices(ProbeModel.Projection());
        var takenId = NewId();
        var accountId = NewId();
        await using (var seed = services.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            context.Add(new ProbeAccount(takenId));
            await context.SaveChangesAsync();
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            var account = new ProbeAccount(accountId);
            account.Deposit(10);
            context.Add(account);
            context.Add(new ProbeAccount(takenId));

            await FluentActions.Invoking(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

            account.PendingEvents.Should().ContainSingle("the failed save returned the event to its aggregate");
        }

        (await ReadRowsAsync(accountId)).Should().BeEmpty();
        (await AccountExistsAsync(accountId)).Should().BeFalse();
    }

    [TestMethod]
    public async Task An_event_raised_while_projecting_is_drained_in_the_same_save()
    {
        ProbeAccount? account = null;
        await using var services = CreateHostServices(ProbeModel.Projection(
            whileProjecting: deposited => account!.Raise(new ProbeDepositAudited(deposited.AccountId))));
        var accountId = NewId();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            account = new ProbeAccount(accountId);
            account.Deposit(3);
            context.Add(account);

            await context.SaveChangesAsync();
        }

        (await ReadRowsAsync(accountId)).OrderBy(row => row.OccurredAt).Select(row => row.EventType)
            .Should().Equal("probe.deposited", "probe.deposit_audited");
    }

    [TestMethod]
    public async Task Post_commit_handlers_run_only_after_commit_and_see_the_committed_change()
    {
        var observations = new List<string>();
        await using var services = CreateHostServices(
            ProbeModel.Projection(),
            collection => collection
                .AddSingleton(observations)
                .AddScoped<ISqlOSPostCommitHandler<ProbeDeposited>, CommittedChangeObserver>());
        var accountId = NewId();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var account = new ProbeAccount(accountId);
            account.Deposit(9);
            context.Add(account);
            await context.SaveChangesAsync();

            observations.Should().BeEmpty("the caller's transaction has not committed");

            await transaction.CommitAsync();
        }

        observations.Should().Equal($"{accountId}: account committed, 1 audit row committed");

        var rolledBackId = NewId();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var account = new ProbeAccount(rolledBackId);
            account.Deposit(1);
            context.Add(account);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        observations.Should().HaveCount(1, "a rolled-back change never reaches its handlers");

        var autoCommittedId = NewId();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            var account = new ProbeAccount(autoCommittedId);
            account.Deposit(2);
            context.Add(account);
            await context.SaveChangesAsync();
        }

        observations.Should().EndWith($"{autoCommittedId}: account committed, 1 audit row committed");
    }

    [TestMethod]
    public async Task A_transaction_that_ends_without_committing_never_hands_its_events_to_the_next_one()
    {
        var observations = new List<string>();
        await using var services = CreateHostServices(
            ProbeModel.Projection(),
            collection => collection
                .AddSingleton(observations)
                .AddScoped<ISqlOSPostCommitHandler<ProbeDeposited>, CommittedChangeObserver>());
        var abandonedId = NewId();
        var committedId = NewId();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();

        // One open connection for both transactions: Npgsql then hands both the same pooled
        // DbTransaction object, which is what a pooled connection does across requests.
        await context.Database.OpenConnectionAsync();
        await using (await context.Database.BeginTransactionAsync())
        {
            var abandoned = new ProbeAccount(abandonedId);
            abandoned.Deposit(1);
            context.Add(abandoned);
            await context.SaveChangesAsync();

            // Disposed without a commit or a rollback, which EF reports to no interceptor.
        }

        context.ChangeTracker.Clear();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var committed = new ProbeAccount(committedId);
            committed.Deposit(2);
            context.Add(committed);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await context.Database.CloseConnectionAsync();

        observations.Should().Equal($"{committedId}: account committed, 1 audit row committed");
        (await AccountExistsAsync(abandonedId)).Should().BeFalse();
        (await ReadRowsAsync(abandonedId)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task Post_commit_handlers_wait_for_an_ambient_transaction_and_skip_one_that_aborts()
    {
        var observations = new List<string>();
        await using var services = CreateHostServices(
            ProbeModel.Projection(),
            collection => collection
                .AddSingleton(observations)
                .AddScoped<ISqlOSPostCommitHandler<ProbeDeposited>, CommittedChangeObserver>());
        var committedId = NewId();
        using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            var account = new ProbeAccount(committedId);
            account.Deposit(5);
            context.Add(account);
            await context.SaveChangesAsync();

            observations.Should().BeEmpty("the ambient transaction has not committed");
            ambient.Complete();
        }

        observations.Should().Equal($"{committedId}: account committed, 1 audit row committed");

        var abortedId = NewId();
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();
            var account = new ProbeAccount(abortedId);
            account.Deposit(6);
            context.Add(account);
            await context.SaveChangesAsync();
        }

        observations.Should().HaveCount(1, "an aborted change never reaches its handlers");
        (await AccountExistsAsync(abortedId)).Should().BeFalse();
        (await ReadRowsAsync(abortedId)).Should().BeEmpty("the audit row rolled back with the change");
    }

    [TestMethod]
    public async Task A_host_that_registers_its_own_context_gets_one_interceptor()
    {
        await using var services = CreateHostServices(ProbeModel.Projection());
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProbeHostDbContext>();

        CountInterceptors(context).Should().Be(1);
        await AssertOneRowPerDepositAsync(context);
    }

    [TestMethod]
    public async Task The_AddSqlOS_overload_that_registers_the_context_gets_one_interceptor()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Services.AddSingleton(ProbeModel.Projection());
        builder.AddSqlOS<ProbeSqlOSDbContext>(
            database => database.UseTestProvider(_connectionString),
            options => options.AuthServer.Issuer = Issuer);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProbeSqlOSDbContext>();

        CountInterceptors(context).Should().Be(1, "AddSqlOS and SqlOSDbContext both attach it, once");
        await AssertOneRowPerDepositAsync(context);
    }

    [TestMethod]
    public async Task A_SqlOSDbContext_built_without_dependency_injection_runs_the_pipeline()
    {
        await using var context = CreateManualContext();

        CountInterceptors(context).Should().Be(1);
        context.Add(new ProbeAccount(NewId()));
        await context.SaveChangesAsync();

        var account = new ProbeAccount(NewId());
        account.Deposit(1);
        context.Add(account);

        // Without dependency injection the context uses SqlOSAuditProjection.Default, which
        // registers no test event: the failure proves the interceptor runs on this path.
        await FluentActions.Invoking(() => context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("No audit projection is registered*");
        (await AccountExistsAsync(account.Id)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Options_that_already_carry_the_interceptor_are_not_given_a_second_one()
    {
        var options = new DbContextOptionsBuilder<ProbeSqlOSDbContext>().UseTestProvider(_connectionString);
        SqlOSDomainEventsInterceptor.AttachTo(options);

        await using var context = new ProbeSqlOSDbContext(options.Options);

        CountInterceptors(context).Should().Be(1);
    }

    private static async Task AssertOneRowPerDepositAsync(DbContext context)
    {
        var accountId = NewId();
        var account = new ProbeAccount(accountId);
        account.Deposit(4);
        context.Add(account);

        await context.SaveChangesAsync();

        (await ReadRowsAsync(accountId)).Should().ContainSingle();
    }

    private static ServiceProvider CreateHostServices(SqlOSAuditProjection projection, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(projection);
        services.AddDbContext<ProbeHostDbContext>(database => database.UseTestProvider(_connectionString));
        services.AddSqlOS<ProbeHostDbContext>(options => options.AuthServer.Issuer = Issuer);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ProbeSqlOSDbContext CreateManualContext()
        => new(new DbContextOptionsBuilder<ProbeSqlOSDbContext>().UseTestProvider(_connectionString).Options);

    private static async Task<List<SqlOSAuditEvent>> ReadRowsAsync(string accountId)
    {
        await using var context = CreateManualContext();
        return await context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .Where(row => row.MetadataJson != null && row.MetadataJson.Contains(accountId))
            .ToListAsync();
    }

    private static async Task<bool> AccountExistsAsync(string accountId)
    {
        await using var context = CreateManualContext();
        return await context.Set<ProbeAccount>().AnyAsync(account => account.Id == accountId);
    }

    private static int CountInterceptors(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?
            .Count(interceptor => interceptor is SqlOSDomainEventsInterceptor) ?? 0;

    private static string NewId() => SqlOSIds.New("acct");

    /// <summary>Reads, on its own connection, whether the deposit and its audit row are committed.</summary>
    private sealed class CommittedChangeObserver(List<string> observations, ProbeHostDbContext context)
        : ISqlOSPostCommitHandler<ProbeDeposited>
    {
        public async Task HandleAsync(ProbeDeposited domainEvent, CancellationToken cancellationToken)
        {
            var accountCommitted = await context.Set<ProbeAccount>().AnyAsync(account => account.Id == domainEvent.AccountId, cancellationToken);
            var rows = await context.Set<SqlOSAuditEvent>().CountAsync(
                row => row.MetadataJson != null && row.MetadataJson.Contains(domainEvent.AccountId),
                cancellationToken);
            observations.Add($"{domainEvent.AccountId}: account {(accountCommitted ? "committed" : "missing")}, {rows} audit row committed");
        }
    }
}
