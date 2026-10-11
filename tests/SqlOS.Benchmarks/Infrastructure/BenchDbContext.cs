using Microsoft.EntityFrameworkCore;
using SqlOS.Benchmarks.Data;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Benchmarks.Infrastructure;

/// <summary>
/// An application context the way SqlOS consumers write one: the app's own tables and the FGA model. Queries go
/// through <c>BuildFilterAsync</c>, so the benchmark measures the SQL EF Core generates for real callers, not a
/// hand-written copy of it. The point-check function is mapped here only so the harness can time it directly;
/// applications call <c>CheckAccessAsync</c>.
/// </summary>
internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options), ISqlOSFgaDbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Store> Stores => Set<Store>();

    /// <summary><c>fn_IsResourceAccessible</c> as SqlOS ships it: the grant that decides a point check, or no row.</summary>
    public IQueryable<AccessibleRow> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

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

            // The indexes an application table like this carries: the unique resource id (which the filter's
            // EXISTS joins the visible resources to), the store foreign key, and the one order the catalog
            // pages in besides the key: price, then the key, as a keyset page by price orders (an index on
            // price alone makes every page read the whole run of equal prices, hundreds of rows at 50M).
            product.HasIndex(p => p.ResourceId).IsUnique();
            product.HasOne<Store>().WithMany().HasForeignKey(p => p.StoreId);
            product.HasIndex(p => new { p.Price, p.Id }).HasDatabaseName("IX_Products_Price");
        });

        // The app's entities first, then SqlOS: every entity above with a ResourceId gets its resource id
        // indexed when the application declared none (ApplySqlOSFgaModel documents the order).
        modelBuilder.ApplySqlOSFgaModel(options => options.RootResourceId = BenchmarkModel.RootResourceId);
        modelBuilder.Entity<AccessibleRow>(row =>
        {
            row.HasNoKey();
            row.ToView(null);
        });
        modelBuilder.HasDbFunction(GetType().GetMethod(nameof(IsResourceAccessible))!)
            .HasName("fn_IsResourceAccessible")
            .HasSchema("dbo");
    }
}

/// <summary>A row of a point-check function: the id of the resource that holds the deciding grant.</summary>
internal sealed class AccessibleRow
{
    public string Id { get; set; } = string.Empty;
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
