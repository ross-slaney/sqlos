using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.Models;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Example.Api.Data;

public sealed class ExampleAppDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public ExampleAppDbContext(DbContextOptions<ExampleAppDbContext> options) : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Chain> Chains => Set<Chain>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<ExampleUserProfile> ExampleUserProfiles => Set<ExampleUserProfile>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        if (Database.IsRelational())
        {
            modelBuilder.UseSqlOS(GetType());
        }
        else
        {
            modelBuilder.UseAuthServer();
            modelBuilder.UseFGA();
        }

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.OrganizationId);
            entity.HasIndex(x => x.ResourceId);
        });

        modelBuilder.Entity<Chain>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ChainId);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
            entity.HasOne(x => x.Chain)
                .WithMany(x => x.Locations)
                .HasForeignKey(x => x.ChainId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.LocationId);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Sku).HasMaxLength(50).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Price).HasColumnType("decimal(18,2)");
            entity.HasOne(x => x.Location)
                .WithMany(x => x.InventoryItems)
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExampleUserProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SqlOSUserId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DefaultEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(64);
            entity.Property(x => x.OrganizationName).HasMaxLength(200);
            entity.Property(x => x.ReferralSource).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.SqlOSUserId).IsUnique();
            entity.HasIndex(x => x.OrganizationId);
        });

        // Retail entities participate in FGA via IHasResourceId, so let SqlOS add
        // the baseline ResourceId indexes unless the app overrides them explicitly.
        modelBuilder.ApplySqlOSFgaConventions();
    }
}
