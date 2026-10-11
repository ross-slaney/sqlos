using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests.Fga;

/// <summary>
/// What SqlOS does to an application entity with a resource id: it indexes the resource id (unless the
/// application did), and nothing else. No column, no trigger, no shadow property: the filter joins the row's
/// resource id to the resources SqlOS keeps in its own tables.
/// </summary>
[TestClass]
public class SqlOSFgaResourceEntitiesTests
{
    [TestMethod]
    public void EveryResourceEntity_GetsOnlyTheResourceIdIndex_TheSameOnBothEngines()
    {
        foreach (var context in new DbContext[] { Create<OnSqlServer>(SqlOSDatabase.SqlServerProviderName), Create<OnPostgreSql>(SqlOSDatabase.PostgreSqlProviderName) })
        {
            using var _ = context;
            var orders = context.Model.FindEntityType(typeof(Order))!;
            var notes = context.Model.FindEntityType(typeof(Note))!;

            // The entity's own properties and nothing more: the table is the application's.
            orders.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["Id", "StoreId", "ResourceId", "Number", "PlacedAt"]);
            notes.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["Id", "ResourceId"]);
            orders.GetDeclaredTriggers().Should().BeEmpty();
            notes.GetDeclaredTriggers().Should().BeEmpty();

            // The resource id is indexed: Orders declared its own, Notes gets SqlOS's.
            orders.GetIndexes().Select(i => i.GetDatabaseName()).Should().BeEquivalentTo(["IX_Orders_ResourceId", "IX_Orders_Number", "IX_Orders_PlacedAt", "IX_Orders_StoreId"]);
            notes.GetIndexes().Select(i => i.GetDatabaseName()).Should().BeEquivalentTo(["IX_Notes_SqlOSFgaResourceId"]);

            // An entity without a resource id is left alone.
            context.Model.FindEntityType(typeof(Store))!.GetIndexes().Should().BeEmpty();
        }
    }

    [TestMethod]
    public void AnEntityDerivingFromTheBaseClass_GetsNoResourceDescriptionColumns()
    {
        // SqlOSResourceEntity brings ResourceId with it; the members that describe the backing resource
        // (ResourceTypeId, ResourceName, ...) feed synchronization and are never columns, even when an override
        // has a backing field EF Core's conventions would map.
        using var context = new BaseClassDbContext(new DbContextOptionsBuilder<BaseClassDbContext>()
            .UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=True").Options);
        var entity = context.Model.FindEntityType(typeof(BasedProject))!;

        entity.GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["Id", "Name", "ResourceId"]);
        entity.GetIndexes().Select(i => i.GetDatabaseName()).Should().BeEquivalentTo(["IX_BasedProjects_SqlOSFgaResourceId"]);
    }

    [TestMethod]
    public void MappingTheBaseClassItself_Fails()
    {
        // A DbSet of the base class would make every derived entity part of one EF Core hierarchy and SqlOS
        // would protect none of them; the model fails loudly instead.
        var act = () => new MappedBaseClassDbContext(new DbContextOptionsBuilder<MappedBaseClassDbContext>()
            .UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=True").Options).Model;
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{nameof(SqlOSResourceEntity)}*base class*");
    }

    [TestMethod]
    public void WithoutAProvider_TheModelIsTheSame()
    {
        // ApplySqlOSFgaModel without a provider name, or an in-memory test context: nothing depends on it.
        using var context = Create<OnNoProvider>(providerName: null);
        context.Model.FindEntityType(typeof(Note))!.GetIndexes().Select(i => i.GetDatabaseName()).Should().BeEquivalentTo(["IX_Notes_SqlOSFgaResourceId"]);
    }

    private static DbContext Create<TMarker>(string? providerName)
        => new ResourceModelDbContext<TMarker>(Options<ResourceModelDbContext<TMarker>>(providerName), providerName);

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

    private sealed class OnPostgreSql;

    private sealed class OnNoProvider;

    // One context type per configuration: EF Core caches the model by context type.
    private sealed class ResourceModelDbContext<TMarker>(DbContextOptions<ResourceModelDbContext<TMarker>> options, string? providerName) : DbContext(options)
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

            // The application configured its entities first; the SqlOS model comes last.
            modelBuilder.UseSqlOS(providerName, new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 });
        }
    }

    private sealed class BaseClassDbContext(DbContextOptions<BaseClassDbContext> options) : DbContext(options)
    {
        public DbSet<BasedProject> Projects => Set<BasedProject>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BasedProject>(project =>
            {
                project.ToTable("BasedProjects");
                project.HasKey(p => p.Id);
            });
            modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName);
        }
    }

    private sealed class MappedBaseClassDbContext(DbContextOptions<MappedBaseClassDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SqlOSResourceEntity>();
            modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName);
        }
    }

    private sealed class BasedProject : SqlOSResourceEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        public override string ResourceTypeId => "project";

        // An override with a backing field: EF Core's conventions would map it as a column.
        public override string ResourceName { get; } = "fixed";
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
