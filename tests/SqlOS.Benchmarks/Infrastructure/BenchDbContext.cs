using Microsoft.EntityFrameworkCore;
using SqlOS.Benchmarks.Data;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Benchmarks.Infrastructure;

/// <summary>
/// An application context the way SqlOS consumers write one: the app's own tables, the FGA model, and the TVF
/// method. Queries go through <c>BuildFilterAsync</c>, so the benchmark measures the SQL EF Core generates for
/// real callers, not a hand-written copy of it.
/// </summary>
internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options), ISqlOSFgaDbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Store> Stores => Set<Store>();

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    /// <summary>The previous release's row filter (see <see cref="ReferenceFunction"/>), for the regression gate.</summary>
    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessibleReference(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessibleReference(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Store>(store =>
        {
            store.ToTable("Stores");
            store.HasKey(s => s.Id);
            store.Property(s => s.Id).ValueGeneratedNever();
            store.Property(s => s.ResourceId).HasMaxLength(128).IsRequired();
            store.Property(s => s.Name).HasMaxLength(200).IsRequired();
            store.HasIndex(s => s.ResourceId).IsUnique();
        });

        modelBuilder.Entity<Product>(product =>
        {
            product.ToTable("Products");
            product.HasKey(p => p.Id);
            product.Property(p => p.Id).ValueGeneratedNever();
            product.Property(p => p.ResourceId).HasMaxLength(128).IsRequired();
            product.Property(p => p.Name).HasMaxLength(200).IsRequired();
            product.Property(p => p.Price).HasPrecision(10, 2);

            // The indexes an application table like this carries: the unique resource id (which the scope
            // triggers use), the store foreign key, and the one order the catalog pages in besides the key:
            // price, then the key, as a keyset page by price orders (an index on price alone makes every page
            // read the whole run of equal prices, hundreds of rows at 50M). SqlOS mirrors the key and the price
            // index per level of the scope column; the foreign key's index and the unique index are not orders
            // and are left alone.
            product.HasIndex(p => p.ResourceId).IsUnique();
            product.HasOne<Store>().WithMany().HasForeignKey(p => p.StoreId);
            product.HasIndex(p => new { p.Price, p.Id }).HasDatabaseName("IX_Products_Price");
        });

        // The app's entities first, then SqlOS: every entity above with a ResourceId gets the scope column
        // (ApplySqlOSFgaModel documents the order).
        modelBuilder.ApplySqlOSFgaModel(GetType(), Database.ProviderName, options => options.RootResourceId = BenchmarkModel.RootResourceId);
        modelBuilder.HasDbFunction(GetType().GetMethod(nameof(IsResourceAccessibleReference))!)
            .HasName(ReferenceFunction.Name)
            .HasSchema("dbo");
    }
}

/// <summary>A catalog row. Every product is its own FGA resource, as with <c>ISqlOSResourceEntity</c>.</summary>
internal sealed class Product : IHasResourceId
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

internal sealed class Store : IHasResourceId
{
    public int Id { get; set; }
    public int Chain { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
