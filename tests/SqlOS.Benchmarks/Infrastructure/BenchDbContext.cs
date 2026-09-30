using Microsoft.EntityFrameworkCore;
using SqlOS.Benchmarks.Data;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Benchmarks.Infrastructure;

/// <summary>
/// An application context the way SqlOS consumers write one: the FGA model, the TVF method, and the app's
/// own tables. Queries go through <c>BuildFilterAsync</c>, so the benchmark measures the SQL EF Core
/// generates for real callers, not a hand-written copy of it.
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySqlOSFgaModel(GetType(), options => options.RootResourceId = BenchmarkModel.RootResourceId);

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

            // No index on ResourceId: the authorization filter reads each candidate row's ResourceId and walks
            // up from it; nothing looks products up by resource. The (StoreId, Id) index supports the
            // store-scoped listing a real app uses for "this store's products".
            product.HasIndex(p => new { p.StoreId, p.Id });
        });
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
