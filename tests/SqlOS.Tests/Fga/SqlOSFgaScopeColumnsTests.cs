using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The scope columns pass: with the option on, every application entity with a ResourceId gets the lineage
/// as database-owned shadow columns, one filtered index per level over its key and over each order it
/// declared, the triggers declared, and the table listed for the database routines.
/// </summary>
[TestClass]
public class SqlOSFgaScopeColumnsTests
{
    [TestMethod]
    public void ScopeColumnsOn_AddsColumnsIndexesAndTriggersToEveryResourceBackedEntity()
    {
        using var context = Create(scopeColumns: true, SqlOSDatabase.SqlServerProviderName);
        var model = DesignTimeModel(context);
        var orders = model.FindEntityType(typeof(Order))!;

        SqlOSFgaScopeColumns.Has(orders).Should().BeTrue();
        foreach (var name in new[] { "SqlOSFgaAncestor0", "SqlOSFgaAncestor1", "SqlOSFgaAncestor2", "SqlOSFgaReach", "SqlOSFgaTypeSeq" })
        {
            var property = orders.FindProperty(name);
            property.Should().NotBeNull(name);
            property!.IsShadowProperty().Should().BeTrue(name);
            property.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore, name);
        }

        orders.FindProperty("SqlOSFgaAncestor3").Should().BeNull("depth 2 means levels 0..2");
        orders.GetDeclaredTriggers().Select(t => t.GetDatabaseName())
            .Should().BeEquivalentTo(SqlOSFgaLineage.ScopeTriggerNames("Orders"));

        // Per level: the key index, and a mirror of the declared (PlacedAt) order; not of the ResourceId lookup,
        // the unique Number, or the StoreId foreign key's index.
        var indexes = orders.GetIndexes().ToDictionary(i => i.GetDatabaseName(), i => i);
        for (var level = 0; level <= 2; level++)
        {
            var key = indexes.Should().ContainKey($"IX_Orders_SqlOSFgaAncestor{level}").WhoseValue;
            key.Properties.Select(p => p.Name).Should().Equal($"SqlOSFgaAncestor{level}", "Id");
            key.GetFilter().Should().Be($"[SqlOSFgaAncestor{level}] IS NOT NULL");
            SqlServerIndexExtensions.GetIncludeProperties(key).Should().BeEquivalentTo(["SqlOSFgaReach", "SqlOSFgaTypeSeq"]);

            var mirrored = indexes.Should().ContainKey($"IX_Orders_SqlOSFgaAncestor{level}_PlacedAt").WhoseValue;
            mirrored.Properties.Select(p => p.Name).Should().Equal($"SqlOSFgaAncestor{level}", "PlacedAt", "Id");
            mirrored.GetFilter().Should().Be($"[SqlOSFgaAncestor{level}] IS NOT NULL");
        }

        indexes.Keys.Should().NotContain(k => k.Contains("Number", StringComparison.Ordinal) && k.Contains("SqlOSFga", StringComparison.Ordinal));
        indexes.Keys.Should().NotContain(k => k.Contains("StoreId", StringComparison.Ordinal) && k.Contains("SqlOSFga", StringComparison.Ordinal));
        indexes.Keys.Should().NotContain(k => k.Contains("ResourceId", StringComparison.Ordinal) && k.Contains("SqlOSFgaAncestor", StringComparison.Ordinal));

        // The resource id is indexed for the triggers, and a SqlOS entity is never touched.
        orders.GetIndexes().Should().Contain(i => i.Properties.Count == 1 && i.Properties[0].Name == "ResourceId");
        model.FindEntityType(typeof(Note))!.GetIndexes().Should().Contain(i => i.Name == "IX_Notes_SqlOSFgaResourceId");

        SqlOSFgaScopeColumns.Tables(context.Model).Should().BeEquivalentTo(
        [
            new SqlOSFgaScopeTable(null, "Notes", "ResourceId", ["Id"]),
            new SqlOSFgaScopeTable("sales", "Orders", "ResourceId", ["Id"]),
        ]);
    }

    [TestMethod]
    public void ScopeColumnsOff_LeavesApplicationEntitiesAlone()
    {
        using var context = Create(scopeColumns: false, SqlOSDatabase.SqlServerProviderName);
        var orders = context.Model.FindEntityType(typeof(Order))!;

        SqlOSFgaScopeColumns.Has(orders).Should().BeFalse();
        orders.FindProperty("SqlOSFgaAncestor0").Should().BeNull();
        orders.GetDeclaredTriggers().Should().BeEmpty();
        SqlOSFgaScopeColumns.Tables(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void ScopeColumnsOn_PostgreSql_QuotesTheFilterAndIncludesThroughNpgsql()
    {
        using var context = Create(scopeColumns: true, SqlOSDatabase.PostgreSqlProviderName);
        var index = DesignTimeModel(context).FindEntityType(typeof(Order))!.GetIndexes().Single(i => i.Name == "IX_Orders_SqlOSFgaAncestor1");

        index.GetFilter().Should().Be("\"SqlOSFgaAncestor1\" IS NOT NULL");
        NpgsqlIndexExtensions.GetIncludeProperties(index).Should().BeEquivalentTo(
            ["SqlOSFgaReach", "SqlOSFgaTypeSeq"],
            "the index carries {0}",
            string.Join(", ", index.GetAnnotations().Select(a => a.Name + "=" + a.Value)));
    }

    [TestMethod]
    public void ScopeColumnsOn_WithoutAProvider_FailsWithGuidance()
    {
        var act = () => Create(scopeColumns: true, providerName: null).Model;
        act.Should().Throw<InvalidOperationException>().WithMessage("*Database.ProviderName*");
    }

    /// <summary>The model with its configuration, which the read-optimized runtime model leaves out (index includes, for one).</summary>
    private static IModel DesignTimeModel(DbContext context)
        => context.GetService<IDesignTimeModel>().Model;

    private static DbContext Create(bool scopeColumns, string? providerName)
    {
        // One context type per configuration: EF Core caches the model by context type.
        return (scopeColumns, providerName) switch
        {
            (true, SqlOSDatabase.SqlServerProviderName) => new ScopeModelDbContext<OnSqlServer>(Options<ScopeModelDbContext<OnSqlServer>>(providerName), true, providerName),
            (false, SqlOSDatabase.SqlServerProviderName) => new ScopeModelDbContext<Off>(Options<ScopeModelDbContext<Off>>(providerName), false, providerName),
            (true, SqlOSDatabase.PostgreSqlProviderName) => new ScopeModelDbContext<OnPostgreSql>(Options<ScopeModelDbContext<OnPostgreSql>>(providerName), true, providerName),
            _ => new ScopeModelDbContext<OnNoProvider>(Options<ScopeModelDbContext<OnNoProvider>>(providerName), true, providerName),
        };
    }

    private static DbContextOptions<TContext> Options<TContext>(string? providerName) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        if (providerName == SqlOSDatabase.PostgreSqlProviderName)
        {
            builder.UseNpgsql("Host=localhost;Database=sqlos_scope;Username=sqlos;Password=sqlos");
        }
        else
        {
            builder.UseSqlServer("Server=.;Database=SqlOS_Scope;Trusted_Connection=True;TrustServerCertificate=True");
        }

        return builder.Options;
    }

    private sealed class OnSqlServer;

    private sealed class Off;

    private sealed class OnPostgreSql;

    private sealed class OnNoProvider;

    private sealed class ScopeModelDbContext<TMarker>(DbContextOptions<ScopeModelDbContext<TMarker>> options, bool scopeColumns, string? providerName) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Store>(store =>
            {
                store.ToTable("Stores");
                store.HasKey(s => s.Id);
            });
            modelBuilder.Entity<Order>(order =>
            {
                order.ToTable("Orders", "sales");
                order.HasKey(o => o.Id);
                order.HasIndex(o => o.ResourceId).IsUnique();
                order.HasIndex(o => o.Number).IsUnique();
                order.HasIndex(o => o.PlacedAt).HasDatabaseName("IX_Orders_PlacedAt");
                order.HasOne<Store>().WithMany().HasForeignKey(o => o.StoreId);
            });
            modelBuilder.Entity<Note>(note =>
            {
                note.ToTable("Notes");
                note.HasKey(n => n.Id);
            });

            // The application configured its entities first; the SqlOS model and the scope pass come last.
            modelBuilder.UseSqlOS(GetType(), providerName, new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2, ScopeColumns = scopeColumns });
        }
    }

    private sealed class Store
    {
        public int Id { get; set; }
    }

    private sealed class Order : IHasResourceId
    {
        public int Id { get; set; }
        public int StoreId { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public DateTime PlacedAt { get; set; }
    }

    private sealed class Note : IHasResourceId
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
    }
}
