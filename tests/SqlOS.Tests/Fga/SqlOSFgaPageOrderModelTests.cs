using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaPageOrderModelTests
{
    [TestMethod]
    public void EntryTable_IsNamedAfterTableAndColumns_InTheFgaSchema()
    {
        using var context = PageOrderModelContext.CreateProducts(entity => entity.HasPageOrder(product => product.Price));

        var entry = context.Model.FindEntityType("SqlOSFgaPage_Products_Price");

        entry.Should().NotBeNull();
        entry!.ClrType.Should().Be(typeof(Dictionary<string, object>));
        entry.GetTableName().Should().Be("SqlOSFgaPage_Products_Price");
        entry.GetSchema().Should().Be("dbo");
    }

    [TestMethod]
    public void EntryTable_HasCompositeKeyOverNodeOrderAndRowKey_AndRowKeyIndex()
    {
        using var context = PageOrderModelContext.CreateProducts(entity => entity.HasPageOrder(product => new { product.Status, product.CreatedAt }));

        var entry = context.Model.FindEntityType("SqlOSFgaPage_Products_Status_CreatedAt")!;

        entry.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal("NodeId", "Status", "CreatedAt", "RowKey");
        var index = entry.GetIndexes().Should().ContainSingle().Subject;
        index.Properties.Select(property => property.Name).Should().Equal("RowKey");
        index.IsUnique.Should().BeFalse();
    }

    [TestMethod]
    public void EntryColumns_CopyTheSourceFacets_OnSqlServer()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasPageOrder(product => new { product.Sku, product.Price });
            entity.HasPageOrder(product => product.Status);
        });
        var model = context.GetService<IDesignTimeModel>().Model;
        var entry = model.FindEntityType("SqlOSFgaPage_Products_Sku_Price")!;

        var sku = entry.FindProperty("Sku")!;
        sku.ClrType.Should().Be(typeof(string));
        sku.GetMaxLength().Should().Be(50);
        sku.IsUnicode().Should().BeFalse();
        sku.GetCollation().Should().Be("Latin1_General_100_BIN2");
        var price = entry.FindProperty("Price")!;
        price.GetPrecision().Should().Be(18);
        price.GetScale().Should().Be(2);
        entry.FindProperty("NodeId")!.GetMaxLength().Should().Be(450);

        StoreTypes(context.Model, "SqlOSFgaPage_Products_Sku_Price").Should().Equal(
            ("NodeId", "nvarchar(450)"),
            ("Price", "decimal(18,2)"),
            ("RowKey", "uniqueidentifier"),
            ("Sku", "varchar(50)"));
        StoreTypes(context.Model, "SqlOSFgaPage_Products_Status").Should().Equal(
            ("NodeId", "nvarchar(450)"),
            ("RowKey", "uniqueidentifier"),
            ("Status", "int"));
        entry.GetProperties().Should().OnlyContain(property => !property.IsNullable);
    }

    [TestMethod]
    public void EntryColumns_CopyTheSourceFacets_OnPostgreSql()
    {
        using var context = PageOrderModelContext.CreateProducts(
            entity => entity.HasPageOrder(product => new { product.Sku, product.Price }),
            postgreSql: true);

        StoreTypes(context.Model, "SqlOSFgaPage_Products_Sku_Price").Should().Equal(
            ("NodeId", "character varying(450)"),
            ("Price", "numeric(18,2)"),
            ("RowKey", "uuid"),
            ("Sku", "character varying(50)"));
    }

    [TestMethod]
    public void EntryColumns_CopyAnExplicitColumnTypeAndValueConverter()
    {
        using var context = PageOrderModelContext.Create(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            PageOrderModelContext.ConfigureProduct(entity);
            entity.Property(product => product.Notes).HasColumnType("nvarchar(64)");
            entity.Property(product => product.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasPageOrder(product => new { product.Notes, product.Status });
        }));

        StoreTypes(context.Model, "SqlOSFgaPage_Products_Notes_Status").Should().Equal(
            ("NodeId", "nvarchar(450)"),
            ("Notes", "nvarchar(64)"),
            ("RowKey", "uniqueidentifier"),
            ("Status", "nvarchar(16)"));
    }

    [TestMethod]
    public void EntryType_HasNoProviderSpecificAnnotations()
    {
        foreach (var postgreSql in new[] { false, true })
        {
            using var context = PageOrderModelContext.CreateProducts(entity => entity.HasPageOrder(product => product.Price), postgreSql);
            var entry = context.GetService<IDesignTimeModel>().Model.FindEntityType("SqlOSFgaPage_Products_Price")!;

            var annotations = entry.GetAnnotations()
                .Concat(entry.FindPrimaryKey()!.GetAnnotations())
                .Concat(entry.GetIndexes().SelectMany(index => index.GetAnnotations()))
                .Select(annotation => annotation.Name);
            annotations.Should().NotContain(name => name.StartsWith("SqlServer:", StringComparison.Ordinal)
                || name.StartsWith("Npgsql:", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Declaration_AppendsTheKey_AndRecordsTheFullOrder()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasPageOrder(product => product.Price);
            entity.HasPageOrder(product => new { product.Status, product.CreatedAt, product.Id });
        });
        var products = context.Model.FindEntityType(typeof(PagedProduct))!;

        var recorded = JsonDocument.Parse((string)products.FindAnnotation(SqlOSFgaPageOrders.AnnotationName)!.Value!).RootElement;

        recorded.EnumerateArray().Select(order => (
                order.GetProperty("table").GetString(),
                string.Join(",", order.GetProperty("columns").EnumerateArray().Select(column => column.GetString()))))
            .Should().Equal(
                ("SqlOSFgaPage_Products_Price", "Price,Id"),
                ("SqlOSFgaPage_Products_Status_CreatedAt", "Status,CreatedAt,Id"),
                ("SqlOSFgaPage_Products_Id", "Id"));
        SqlOSFgaPageOrders.For(products).Select(order => order.Description)
            .Should().Equal("PagedProduct(Price, Id)", "PagedProduct(Status, CreatedAt, Id)", "PagedProduct(Id)");
    }

    [TestMethod]
    public void Declaration_RepeatedOrKeyExplicit_IsOneOrder()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasPageOrder(product => product.Price);
            entity.HasPageOrder(product => product.Price);
            entity.HasPageOrder(product => new { product.Price, product.Id });
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id", "SqlOSFgaPage_Products_Price");
        SqlOSFgaPageOrders.For(context.Model.FindEntityType(typeof(PagedProduct))!).Should().HaveCount(2);
    }

    [TestMethod]
    public void Declaration_OfTheKeyAlone_IsTheNodeIndex()
    {
        using var context = PageOrderModelContext.CreateProducts(entity => entity.HasPageOrder(product => product.Id));
        var products = context.Model.FindEntityType(typeof(PagedProduct))!;

        var entry = context.Model.FindEntityType("SqlOSFgaPage_Products_Id")!;

        entry.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal("NodeId", "RowKey");
        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        SqlOSFgaPageOrders.For(products).Should().ContainSingle()
            .Which.Should().BeSameAs(SqlOSFgaPageOrders.NodeIndex(products));
    }

    [TestMethod]
    public void NodeIndex_IsAddedForEveryProtectedEntity_WithoutADeclaration()
    {
        using var context = PageOrderModelContext.CreateProducts(_ => { });
        var products = context.Model.FindEntityType(typeof(PagedProduct))!;

        var entry = context.Model.FindEntityType("SqlOSFgaPage_Products_Id")!;

        entry.GetTableName().Should().Be("SqlOSFgaPage_Products_Id");
        entry.GetSchema().Should().Be("dbo");
        entry.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal("NodeId", "RowKey");
        entry.GetIndexes().Should().ContainSingle()
            .Which.Properties.Select(property => property.Name).Should().Equal("RowKey");
        StoreTypes(context.Model, "SqlOSFgaPage_Products_Id").Should().Equal(
            ("NodeId", "nvarchar(450)"),
            ("RowKey", "uniqueidentifier"));
        var nodeIndex = SqlOSFgaPageOrders.NodeIndex(products);
        nodeIndex.Should().NotBeNull();
        nodeIndex!.IsNodeIndex.Should().BeTrue();
        nodeIndex.Description.Should().Be("PagedProduct(Id)");
        SqlOSFgaPageOrders.For(products).Should().ContainSingle();
        SqlOSFgaPageOrders.Unindexed(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void NodeIndex_OfATphHierarchy_IsOneTable_OnTheRoot()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(PageOrderModelContext.ConfigureProduct);
            modelBuilder.Entity<PagedSpecialProduct>();
        });
        var products = context.Model.FindEntityType(typeof(PagedProduct))!;
        var special = context.Model.FindEntityType(typeof(PagedSpecialProduct))!;

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        SqlOSFgaPageOrders.NodeIndex(special).Should().BeSameAs(SqlOSFgaPageOrders.NodeIndex(products));
        SqlOSFgaPageOrders.NodeIndex(products)!.EntityType.Should().Be(products);
        SqlOSFgaPageOrders.Unindexed(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void SqlOSEntities_GetNoNodeIndex()
    {
        using var context = PageOrderModelContext.CreateProducts(_ => { });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        context.Model.GetEntityTypes()
            .Where(entityType => entityType.ClrType.Namespace?.StartsWith("SqlOS.", StringComparison.Ordinal) == true
                && !entityType.ClrType.Namespace.StartsWith("SqlOS.Tests", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(entityType => entityType.FindAnnotation(SqlOSFgaPageOrders.AnnotationName) == null);
    }

    [TestMethod]
    public async Task Startup_LogsEachProtectedEntityWithoutANodeIndex_WithTheReason()
    {
        // The view-mapped products are the model's only protected entity, so startup has no table to
        // look at and needs no database.
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.ToTable((string?)null);
            entity.ToView("ProductsView");
        });
        var logger = new ListLogger();

        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, logger, CancellationToken.None);

        logger.Messages.Should().ContainSingle().Which.Should().Be(
            (LogLevel.Warning, "PagedProduct implements IHasResourceId but has no node index: it is mapped to a view. Page orders need an entity mapped to a table. Without one, ToPageAsync pages PagedProduct by reading the table in the page's order and checking each row."));
    }

    [TestMethod]
    public void ProtectedEntityWithACompositeKey_GetsNoNodeIndex_AndDoesNotFailTheModel()
        => Unindexed(entity => entity.HasKey(product => new { product.Id, product.Sku }))
            .Should().Contain("its primary key is composite");

    [TestMethod]
    public void ProtectedEntityOnAView_GetsNoNodeIndex_AndDoesNotFailTheModel()
        => Unindexed(entity =>
        {
            entity.ToTable((string?)null);
            entity.ToView("ProductsView");
        }).Should().Contain("mapped to a view");

    [TestMethod]
    public void ProtectedEntityWithoutAMappedResourceId_GetsNoNodeIndex_AndDoesNotFailTheModel()
        => Unindexed(entity => entity.Ignore(product => product.ResourceId))
            .Should().Contain("has no mapped ResourceId");

    [TestMethod]
    public void ProtectedEntityInATptHierarchy_GetsNoNodeIndex_AndDoesNotFailTheModel()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(entity =>
            {
                PageOrderModelContext.ConfigureProduct(entity);
                entity.UseTptMappingStrategy();
            });
            modelBuilder.Entity<PagedSpecialProduct>().ToTable("SpecialProducts");
        });

        EntryTables(context.Model).Should().BeEmpty();
        SqlOSFgaPageOrders.Unindexed(context.Model).Should().ContainSingle()
            .Which.Reason.Should().Contain("TPT or TPC");
    }

    [TestMethod]
    public void OwnedProtectedType_GetsNoNodeIndex_AndDoesNotFailTheModel()
    {
        using var context = PageOrderModelContext.Create(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            PageOrderModelContext.ConfigureProduct(entity);
            entity.OwnsOne(product => product.Details);
        }));

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        var unindexed = SqlOSFgaPageOrders.Unindexed(context.Model).Should().ContainSingle().Subject;
        unindexed.EntityType.ClrType.Should().Be(typeof(PagedDetails));
        unindexed.Reason.Should().Contain("is an owned type");
    }

    [TestMethod]
    public void ProtectedDerivedTypeOfAnUnprotectedRoot_GetsNoNodeIndex_AndDoesNotFailTheModel()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(PageOrderModelContext.ConfigureProduct);
            modelBuilder.Entity<PagedEntry>(entity =>
            {
                entity.ToTable("Entries");
                entity.HasKey(entry => entry.Id);
            });
            modelBuilder.Entity<PagedProtectedEntry>().Property(entry => entry.ResourceId).HasMaxLength(450);
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        var unindexed = SqlOSFgaPageOrders.Unindexed(context.Model).Should().ContainSingle().Subject;
        unindexed.EntityType.ClrType.Should().Be(typeof(PagedProtectedEntry));
        unindexed.Reason.Should().Contain("derives from PagedEntry, which does not implement IHasResourceId");
    }

    [TestMethod]
    public void TwoProtectedEntitiesSplitFromOneTable_KeepOneNodeIndex_AndDoNotFailTheModel()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(entity =>
            {
                PageOrderModelContext.ConfigureProduct(entity);
                entity.HasOne<PagedProductSummary>().WithOne().HasForeignKey<PagedProductSummary>(summary => summary.Id);
            });
            modelBuilder.Entity<PagedProductSummary>(entity =>
            {
                entity.ToTable("Products");
                entity.HasKey(summary => summary.Id);
                entity.Property(summary => summary.ResourceId).HasMaxLength(450);
            });
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        SqlOSFgaPageOrders.Unindexed(context.Model).Should().ContainSingle()
            .Which.Reason.Should().Be("its node index table name 'SqlOSFgaPage_Products_Id' is already used by another entity type.");
    }

    [TestMethod]
    public void NullableColumn_FailsModelBuild()
        => ModelError(entity => entity.HasPageOrder(product => product.DeletedAt!))
            .Should().Contain("'DeletedAt' is nullable").And.Contain("make 'DeletedAt' required");

    [TestMethod]
    public void CompositeKey_FailsModelBuild()
        => ModelError(entity =>
        {
            entity.HasKey(product => new { product.Id, product.Sku });
            entity.HasPageOrder(product => product.Price);
        }).Should().Contain("primary key is composite");

    [TestMethod]
    public void NavigationColumn_FailsModelBuild()
        => ModelError(entity => entity.HasPageOrder(product => product.Location!))
            .Should().Contain("'Location' is not a mapped scalar property");

    [TestMethod]
    public void ComputedColumn_FailsModelBuild()
        => ModelError(entity => entity.HasPageOrder(product => product.Price * 2))
            .Should().Contain("is not a mapped scalar property");

    [TestMethod]
    public void IgnoredResourceId_FailsModelBuild()
        => ModelError(entity =>
        {
            entity.Ignore(product => product.ResourceId);
            entity.HasPageOrder(product => product.Price);
        }).Should().Contain("has no mapped ResourceId");

    [TestMethod]
    public void StringWithoutMaximumLength_FailsModelBuild()
        => ModelError(entity => entity.HasPageOrder(product => product.Notes))
            .Should().Contain("'Notes' is a string without a maximum length");

    [TestMethod]
    public void ViewMapping_FailsModelBuild()
        => ModelError(entity =>
        {
            entity.ToTable((string?)null);
            entity.ToView("ProductsView");
            entity.HasPageOrder(product => product.Price);
        }).Should().Contain("mapped to a view");

    [TestMethod]
    public void OwnedType_FailsModelBuild()
    {
        var act = () => BuildModel(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            PageOrderModelContext.ConfigureProduct(entity);
            entity.OwnsOne(product => product.Details, details =>
                SqlOSFgaPageOrders.Declare(details.OwnedEntityType, ["Rank"]));
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*is an owned type*");
    }

    [TestMethod]
    public void TptInheritance_FailsModelBuild()
    {
        var act = () => BuildModel(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(entity =>
            {
                PageOrderModelContext.ConfigureProduct(entity);
                entity.UseTptMappingStrategy();
                entity.HasPageOrder(product => product.Price);
            });
            modelBuilder.Entity<PagedSpecialProduct>().ToTable("SpecialProducts");
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*TPT or TPC*");
    }

    [TestMethod]
    public void DerivedTypeDeclaration_FailsModelBuild()
    {
        var act = () => BuildModel(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(PageOrderModelContext.ConfigureProduct);
            modelBuilder.Entity<PagedSpecialProduct>(entity => entity.HasPageOrder(product => product.Price));
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Declare the page order on PagedProduct*");
    }

    [TestMethod]
    public void LongName_IsTruncatedWithAHashSuffix_AtEachEnginesLimit()
    {
        var table = "Products" + new string('x', 70);
        foreach (var (postgreSql, limit) in new[] { (false, 128), (true, 63) })
        {
            using var context = PageOrderModelContext.Create(
                modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
                {
                    PageOrderModelContext.ConfigureProduct(entity);
                    entity.ToTable(table);
                    entity.HasPageOrder(product => new { product.Status, product.CreatedAt });
                }),
                postgreSql);

            var expected = new[] { $"SqlOSFgaPage_{table}_Status_CreatedAt", $"SqlOSFgaPage_{table}_Id" }
                .Select(full => full.Length <= limit
                    ? full
                    : $"{full[..(limit - 9)]}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant()}")
                .ToList();

            EntryTables(context.Model).Should().BeEquivalentTo(expected);
            expected.Should().OnlyContain(name => name.Length <= limit);
            if (postgreSql)
            {
                expected.Should().OnlyContain(name => name.Length == limit && Regex.IsMatch(name, "_[0-9a-f]{8}$"));
            }
        }
    }

    [TestMethod]
    public void HandRolledModel_HasNoEntryTableUntilSqlOSDbContextAppliesIt()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity<PagedProduct>(entity =>
        {
            PageOrderModelContext.ConfigureProduct(entity);
            entity.HasPageOrder(product => product.Price);
        });

        modelBuilder.Model.GetEntityTypes().Should().NotContain(entityType => entityType.Name.StartsWith("SqlOSFgaPage_", StringComparison.Ordinal));
        SqlOSFgaPageOrders.ApplyModel(modelBuilder.Model, providerName: null);
        modelBuilder.Model.FindEntityType("SqlOSFgaPage_Products_Price").Should().NotBeNull();
    }

    private static string ModelError(Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PagedProduct>> configure)
    {
        var act = () => BuildModel(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            PageOrderModelContext.ConfigureProduct(entity);
            configure(entity);
        }));

        return act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().StartWith("HasPageOrder on PagedProduct: ")
            .And.Subject;
    }

    /// <summary>
    /// Builds a products model the configuration makes unable to have page orders, and returns why
    /// SqlOS keeps no node index for it.
    /// </summary>
    private static string Unindexed(Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PagedProduct>> configure)
    {
        using var context = PageOrderModelContext.CreateProducts(configure);

        EntryTables(context.Model).Should().BeEmpty();
        var unindexed = SqlOSFgaPageOrders.Unindexed(context.Model).Should().ContainSingle().Subject;
        unindexed.EntityType.ClrType.Should().Be(typeof(PagedProduct));
        return unindexed.Reason;
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }

    private static List<string> EntryTables(IModel model)
        => model.GetEntityTypes()
            .Select(entityType => entityType.Name)
            .Where(name => name.StartsWith(SqlOSFgaPageOrders.EntryTablePrefix, StringComparison.Ordinal))
            .ToList();

    /// <summary>Builds the model; SqlOSDbContext builds it while the context is constructed.</summary>
    private static IModel BuildModel(Action<ModelBuilder> configure)
    {
        using var context = PageOrderModelContext.Create(configure);
        return context.Model;
    }

    private static IEnumerable<(string Column, string StoreType)> StoreTypes(IModel model, string table)
        => model.FindEntityType(table)!
            .GetTableMappings()
            .Single()
            .ColumnMappings
            .Select(mapping => (mapping.Column.Name, mapping.Column.StoreType))
            .OrderBy(column => column.Name, StringComparer.Ordinal);
}
