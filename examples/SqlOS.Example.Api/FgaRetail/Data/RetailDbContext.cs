using Microsoft.EntityFrameworkCore;
using SqlOS.Example.Api.FgaRetail.Models;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Example.Api.FgaRetail.Data;

public class RetailDbContext : DbContext, ISqlOSFgaDbContext
{
    public RetailDbContext(DbContextOptions<RetailDbContext> options) : base(options) { }

    public DbSet<Chain> Chains => Set<Chain>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel(GetType());

        // Chain
        modelBuilder.Entity<Chain>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.ResourceId);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.ResourceId).HasMaxLength(100).IsRequired();
        });

        // Location
        modelBuilder.Entity<Location>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.ResourceId);
            e.HasIndex(l => l.ChainId);
            e.Property(l => l.Name).HasMaxLength(200).IsRequired();
            e.Property(l => l.ResourceId).HasMaxLength(100).IsRequired();
            e.HasOne(l => l.Chain)
                .WithMany(c => c.Locations)
                .HasForeignKey(l => l.ChainId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // InventoryItem
        modelBuilder.Entity<InventoryItem>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.ResourceId);
            e.HasIndex(i => i.LocationId);
            e.Property(i => i.Name).HasMaxLength(200).IsRequired();
            e.Property(i => i.Sku).HasMaxLength(50).IsRequired();
            e.Property(i => i.ResourceId).HasMaxLength(100).IsRequired();
            e.Property(i => i.Price).HasColumnType("decimal(18,2)");
            e.HasOne(i => i.Location)
                .WithMany(l => l.InventoryItems)
                .HasForeignKey(i => i.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
