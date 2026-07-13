using Microsoft.EntityFrameworkCore;
using SqlOS;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Example.Api.Models;

namespace SqlOS.Example.Api.Data;

public sealed class ExampleAppDbContext : SqlOSDbContext<ExampleAppDbContext>
{
    public ExampleAppDbContext(DbContextOptions<ExampleAppDbContext> options) : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Chain> Chains => Set<Chain>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<ExampleUserProfile> ExampleUserProfiles => Set<ExampleUserProfile>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.OrganizationId);
            entity.HasIndex(x => x.ResourceId);
            entity.Ignore(x => x.ResourceTypeId);
            entity.Ignore(x => x.ResourceName);
            entity.Ignore(x => x.ParentResourceId);
            entity.Ignore(x => x.ResourceDescription);
            entity.Ignore(x => x.ResourceIsActive);
        });

        modelBuilder.Entity<Chain>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResourceId);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResourceId);
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
            entity.HasIndex(x => x.ResourceId);
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
    }
}
