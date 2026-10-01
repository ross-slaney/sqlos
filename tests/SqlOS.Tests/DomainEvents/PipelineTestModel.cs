using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Database;
using SqlOS.Domain;

namespace SqlOS.Tests.DomainEvents;

/// <summary>A test-only aggregate: an account whose deposits raise events.</summary>
internal sealed class LedgerAccount : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();

    private LedgerAccount()
    {
    }

    public LedgerAccount(string id) => Id = id;

    public string Id { get; private set; } = string.Empty;

    public int Balance { get; private set; }

    /// <summary>A concurrency token another writer bumps to make a save conflict.</summary>
    public int Version { get; private set; }

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    public IReadOnlyList<ISqlOSDomainEvent> PendingEvents => _events.Pending;

    public void Deposit(int amount)
    {
        Balance += amount;
        _events.Raise(new Deposited(Id, amount));
    }

    public void Raise(ISqlOSDomainEvent domainEvent) => _events.Raise(domainEvent);
}

internal sealed record Deposited(string AccountId, int Amount) : ISqlOSDomainEvent;

internal sealed record DepositAudited(string AccountId) : ISqlOSDomainEvent;

internal sealed record DepositRejected(string AccountId, string Reason) : ISqlOSDomainEvent;

internal sealed record Unregistered(string AccountId) : ISqlOSDomainEvent;

internal sealed record Silent(string AccountId) : ISqlOSDomainEvent;

/// <summary>Audit rows of the test events, in the 7.x authserver shape.</summary>
internal static class PipelineProjections
{
    public static SqlOSAuditProjectionBuilder Standard(Action<SqlOSAuditProjectionBuilder>? configure = null)
    {
        var builder = new SqlOSAuditProjectionBuilder()
            .Audit<Deposited>((domainEvent, context) => SqlOSAuditRows.Create(
                SqlOSAuditRows.AuthServerRequest(
                    "ledger.deposited",
                    "system",
                    null,
                    ipAddress: context.Request.IpAddress,
                    data: new { accountId = domainEvent.AccountId, amount = domainEvent.Amount }),
                SqlOSIds.New("evt"),
                context.Now))
            .Audit<DepositRejected>((domainEvent, context) => SqlOSAuditRows.Create(
                SqlOSAuditRows.AuthServerRequest(
                    "ledger.deposit_rejected",
                    "system",
                    null,
                    ipAddress: context.Request.IpAddress,
                    data: new { accountId = domainEvent.AccountId, reason = domainEvent.Reason }),
                SqlOSIds.New("evt"),
                context.Now))
            .Unaudited<Silent>();
        configure?.Invoke(builder);
        return builder;
    }
}

/// <summary>A relational test context with the aggregate and the audit table only.</summary>
internal sealed class PipelineDbContext(DbContextOptions<PipelineDbContext> options) : DbContext(options), ISqlOSAuthServerDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerAccount>(entity =>
        {
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Version).IsConcurrencyToken();
            entity.Ignore(account => account.PendingEvents);
        });
        modelBuilder.Entity<SqlOSAuditEvent>(entity =>
        {
            entity.ToTable("SqlOSAuditEvents");
            entity.HasKey(row => row.Id);
        });
    }

    /// <summary>A context on an open in-memory SQLite connection, without dependency injection.</summary>
    public static PipelineDbContext Create(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>().UseSqlite(connection);
        SqlOSDomainEventsInterceptor.AttachTo(options);
        var context = new PipelineDbContext(options.Options);
        context.Database.EnsureCreated();
        return context;
    }

    public static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }
}
