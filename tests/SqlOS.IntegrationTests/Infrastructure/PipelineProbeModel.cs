using Microsoft.EntityFrameworkCore;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;
using SqlOS.Domain;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>A test-only aggregate for the domain-event pipeline: an account whose deposits raise events.</summary>
internal sealed class ProbeAccount : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();

    private ProbeAccount()
    {
    }

    public ProbeAccount(string id) => Id = id;

    public string Id { get; private set; } = string.Empty;

    public int Balance { get; private set; }

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    public IReadOnlyList<ISqlOSDomainEvent> PendingEvents => _events.Pending;

    public void Deposit(int amount)
    {
        Balance += amount;
        _events.Raise(new ProbeDeposited(Id, amount));
    }

    public void Raise(ISqlOSDomainEvent domainEvent) => _events.Raise(domainEvent);
}

internal sealed record ProbeDeposited(string AccountId, int Amount) : ISqlOSDomainEvent;

internal sealed record ProbeDepositAudited(string AccountId) : ISqlOSDomainEvent;

internal static class ProbeModel
{
    public const string Table = "PipelineProbeAccounts";

    public static void Map(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ProbeAccount>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Id).HasMaxLength(64);
            entity.Ignore(account => account.PendingEvents);
        });

    /// <summary>
    /// The 7.x authserver row for each probe event. <paramref name="whileProjecting"/> runs while a
    /// deposit is projected, for tests that raise events from a projection.
    /// </summary>
    public static SqlOSAuditProjection Projection(Action<ProbeDeposited>? whileProjecting = null)
        => new SqlOSAuditProjectionBuilder()
            .Audit<ProbeDeposited>((domainEvent, context) =>
            {
                whileProjecting?.Invoke(domainEvent);
                return SqlOSAuditRows.Create(
                    SqlOSAuditRows.AuthServerRequest(
                        "probe.deposited",
                        "system",
                        null,
                        ipAddress: context.Request.IpAddress,
                        data: new { accountId = domainEvent.AccountId, amount = domainEvent.Amount }),
                    SqlOSIds.New("evt"),
                    context.Now);
            })
            .Audit<ProbeDepositAudited>((domainEvent, context) => SqlOSAuditRows.Create(
                SqlOSAuditRows.AuthServerRequest("probe.deposit_audited", "system", null, data: new { accountId = domainEvent.AccountId }),
                SqlOSIds.New("evt"),
                context.Now))
            .Build();
}

/// <summary>A host context derived from <see cref="SqlOSDbContext{TContext}"/>, with the probe table.</summary>
internal sealed class ProbeSqlOSDbContext(DbContextOptions<ProbeSqlOSDbContext> options)
    : SqlOSDbContext<ProbeSqlOSDbContext>(options)
{
    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder) => ProbeModel.Map(modelBuilder);
}

/// <summary>A host context that implements the SqlOS interfaces itself instead of deriving from <see cref="SqlOSDbContext{TContext}"/>.</summary>
internal sealed class ProbeHostDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public ProbeHostDbContext(DbContextOptions<ProbeHostDbContext> options)
        : base(options)
    {
        if (SqlOSDatabase.IsPostgreSql(Database.ProviderName))
        {
            SqlOSDatabase.EnablePostgreSqlTimestampCompatibility();
        }
    }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ProbeModel.Map(modelBuilder);
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName);
    }
}
