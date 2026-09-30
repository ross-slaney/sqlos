using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.BehaviorLock.Host;

/// <summary>
/// The documented EF integration: the host inherits <see cref="SqlOSDbContext{TContext}"/> and adds
/// its own entities in <see cref="OnApplicationModelCreating"/>. <see cref="Workspace"/> is an
/// <see cref="ISqlOSResourceEntity"/>, so saves synchronize its backing FGA resource.
/// </summary>
public sealed class BehaviorLockDbContext(DbContextOptions<BehaviorLockDbContext> options)
    : SqlOSDbContext<BehaviorLockDbContext>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        => Workspace.Configure(modelBuilder);
}

/// <summary>
/// The other supported EF integration: the host implements <see cref="ISqlOSAuthServerDbContext"/>
/// and <see cref="ISqlOSFgaDbContext"/> itself and applies the SqlOS model with <c>UseSqlOS</c>.
/// It has no automatic resource synchronization.
/// </summary>
public sealed class ManualBehaviorLockDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public ManualBehaviorLockDbContext(DbContextOptions<ManualBehaviorLockDbContext> options)
        : base(options)
    {
        // Hosts that register their own context set Npgsql's legacy timestamp switch before first
        // use, as the provider guide describes; UseSqlOS below also sets it.
        if (string.Equals(Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName);
        Workspace.Configure(modelBuilder);
    }
}

/// <summary>An application entity protected by FGA, as in the configuration guide.</summary>
public sealed class Workspace : ISqlOSResourceEntity
{
    public const string TableName = "BehaviorLockWorkspaces";

    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentResourceId { get; set; }
    public bool IsActive { get; set; } = true;

    public string ResourceTypeId => BehaviorLockAuthorization.WorkspaceType;
    public string ResourceName => Name;
    public string? ResourceDescription => null;
    public bool ResourceIsActive => IsActive;

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.ToTable(TableName, BehaviorLockHost.ApplicationSchema);
            entity.HasKey(workspace => workspace.Id);
            entity.Property(workspace => workspace.Id).HasMaxLength(64);
            entity.Property(workspace => workspace.ResourceId).HasMaxLength(128);
            entity.Property(workspace => workspace.Name).HasMaxLength(200);
            entity.Property(workspace => workspace.ParentResourceId).HasMaxLength(128);
        });
    }
}
