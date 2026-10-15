using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests.Fga;

public enum PagedProductStatus
{
    Draft,
    Published,
    Archived
}

public class PagedProduct : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Rank { get; set; }
    public DateTime CreatedAt { get; set; }
    public PagedProductStatus Status { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? LocationId { get; set; }
    public PagedLocation? Location { get; set; }
    public PagedDetails? Details { get; set; }
}

public sealed class PagedSpecialProduct : PagedProduct
{
    public string Flavor { get; set; } = string.Empty;
}

public sealed class PagedLocation
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class PagedDetails : IHasResourceId
{
    public string ResourceId { get; set; } = string.Empty;
    public int Rank { get; set; }
}

/// <summary>A protected entity whose sort index comes from an attribute and whose foreign key gets its index by convention.</summary>
[Index(nameof(Rank))]
public sealed class PagedIndexedProduct : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public int Rank { get; set; }
    public Guid LocationId { get; set; }
    public PagedLocation? Location { get; set; }
}

/// <summary>A protected entity with columns no order can use.</summary>
public sealed class PagedOddRow : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public int NodeId { get; set; }
    public byte[] Blob { get; set; } = [];
}

/// <summary>A protected entity with a string key that has no maximum length, as the examples have.</summary>
public sealed class PagedStringKeyRow : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>A protected entity split from the products table.</summary>
public sealed class PagedProductSummary : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
}

/// <summary>The root of a hierarchy that only its derived type protects.</summary>
public class PagedEntry
{
    public Guid Id { get; set; }
}

public sealed class PagedProtectedEntry : PagedEntry, IHasResourceId
{
    public string ResourceId { get; set; } = string.Empty;
}

/// <summary>
/// A SqlOS context whose application model each test supplies. The model is built without a
/// database connection; every instance gets its own model.
/// </summary>
internal sealed class PageOrderModelContext : SqlOSDbContext<PageOrderModelContext>
{
    // SqlOSDbContext reads Database.ProviderName in its constructor, which builds the model before
    // this constructor's body runs, so the scenario arrives through the constructing thread.
    [ThreadStatic]
    private static (string Scenario, Action<ModelBuilder> Configure)? _constructing;

    private readonly (string Scenario, Action<ModelBuilder> Configure) _setup;

    private PageOrderModelContext(DbContextOptions<PageOrderModelContext> options)
        : base(options)
    {
        _setup = _constructing!.Value;
    }

    public string Scenario => (_constructing ?? _setup).Scenario;

    public DbSet<PagedProduct> Products => Set<PagedProduct>();

    public static PageOrderModelContext Create(
        Action<ModelBuilder> configure,
        bool postgreSql = false,
        [CallerMemberName] string scenario = "")
    {
        var builder = new DbContextOptionsBuilder<PageOrderModelContext>();
        if (postgreSql)
        {
            builder.UseNpgsql("Host=localhost;Database=sqlos_page_order_model;Username=sqlos;Password=sqlos");
        }
        else
        {
            builder.UseSqlServer("Server=localhost;Database=sqlos_page_order_model;User Id=sqlos;Password=sqlos;TrustServerCertificate=True");
        }

        builder.ReplaceService<IModelCacheKeyFactory, ScenarioModelCacheKeyFactory>();
        _constructing = ($"{scenario}|{postgreSql}|{Guid.NewGuid():N}", configure);
        try
        {
            return new PageOrderModelContext(builder.Options);
        }
        finally
        {
            _constructing = null;
        }
    }

    /// <summary>The products model most tests use, with the indexes the test adds.</summary>
    public static PageOrderModelContext CreateProducts(
        Action<EntityTypeBuilder<PagedProduct>> indexes,
        bool postgreSql = false,
        [CallerMemberName] string scenario = "")
        => Create(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            ConfigureProduct(entity);
            indexes(entity);
        }), postgreSql, scenario);

    public static void ConfigureProduct(EntityTypeBuilder<PagedProduct> entity)
    {
        entity.ToTable("Products");
        entity.HasKey(product => product.Id);
        entity.Property(product => product.ResourceId).HasMaxLength(450);
        entity.Property(product => product.Name).HasMaxLength(200);
        entity.Property(product => product.Sku).HasMaxLength(50).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        entity.Property(product => product.Price).HasPrecision(18, 2);
        entity.HasIndex(product => product.ResourceId).IsUnique();
        entity.HasOne(product => product.Location).WithMany().HasForeignKey(product => product.LocationId);
        entity.Ignore(product => product.Details);
    }

    public static void ConfigureIndexedProduct(EntityTypeBuilder<PagedIndexedProduct> entity)
    {
        entity.ToTable("IndexedProducts");
        entity.HasKey(product => product.Id);
        entity.Property(product => product.ResourceId).HasMaxLength(450);
    }

    public static void ConfigureOddRow(EntityTypeBuilder<PagedOddRow> entity)
    {
        entity.ToTable("OddRows");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.ResourceId).HasMaxLength(450);
    }

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        => (_constructing ?? _setup).Configure(modelBuilder);

    private sealed class ScenarioModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is PageOrderModelContext scenario
                ? (context.GetType(), scenario.Scenario, designTime)
                : (context.GetType(), designTime);
    }
}
