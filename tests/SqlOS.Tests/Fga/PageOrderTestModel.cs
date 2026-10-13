using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SqlOS.Fga.Extensions;
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

    /// <summary>The products model most tests use, with its page orders.</summary>
    public static PageOrderModelContext CreateProducts(
        Action<EntityTypeBuilder<PagedProduct>> pageOrders,
        bool postgreSql = false,
        [CallerMemberName] string scenario = "")
        => Create(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            ConfigureProduct(entity);
            pageOrders(entity);
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
