using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// Every root entity that implements IHasResourceId gets a page order for its primary key and one
/// for each of its indexes, minus lookups; nothing about an automatic order fails the model build.
/// </summary>
[TestClass]
public class SqlOSFgaPageOrderModelTests
{
    [TestMethod]
    public void KeyOrder_ExistsForEveryProtectedEntity()
    {
        using var context = PageOrderModelContext.CreateProducts(_ => { });

        var entry = context.Model.FindEntityType("SqlOSFgaPage_Products_Id");

        entry.Should().NotBeNull();
        entry!.ClrType.Should().Be(typeof(Dictionary<string, object>));
        entry.GetTableName().Should().Be("SqlOSFgaPage_Products_Id");
        entry.GetSchema().Should().Be("dbo");
        entry.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal("NodeId", "RowKey");
        entry.GetIndexes().Should().ContainSingle().Which.Properties.Select(property => property.Name).Should().Equal("RowKey");
        SqlOSFgaPageOrders.For(context.Model.FindEntityType(typeof(PagedProduct))!).Select(order => order.Description)
            .Should().Equal("PagedProduct(Id)");
        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        SqlOSFgaPageOrders.Skips(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void EntryTable_IsNamedAfterTableAndColumns_InTheFgaSchema()
    {
        using var context = PageOrderModelContext.CreateProducts(entity => entity.HasIndex(product => product.Price));

        var entry = context.Model.FindEntityType("SqlOSFgaPage_Products_Price");

        entry.Should().NotBeNull();
        entry!.ClrType.Should().Be(typeof(Dictionary<string, object>));
        entry.GetTableName().Should().Be("SqlOSFgaPage_Products_Price");
        entry.GetSchema().Should().Be("dbo");
    }

    [TestMethod]
    public void EntryTable_HasCompositeKeyOverNodeOrderAndRowKey_AndRowKeyIndex()
    {
        using var context = PageOrderModelContext.CreateProducts(entity => entity.HasIndex(product => new { product.Status, product.CreatedAt }));

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
            entity.HasIndex(product => new { product.Sku, product.Price });
            entity.HasIndex(product => product.Status);
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
            entity => entity.HasIndex(product => new { product.Sku, product.Price }),
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
            entity.HasIndex(product => new { product.Notes, product.Status });
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
            using var context = PageOrderModelContext.CreateProducts(entity => entity.HasIndex(product => product.Price), postgreSql);
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
    public void EveryIndex_IsAnOrder_WithTheKeyAppended_AndIsRecordedOnTheEntity()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasIndex(product => product.Price);
            entity.HasIndex(product => new { product.Status, product.CreatedAt, product.Id }).IsUnique().IsDescending();
        });
        var products = context.Model.FindEntityType(typeof(PagedProduct))!;

        var recorded = JsonDocument.Parse((string)products.FindAnnotation(SqlOSFgaPageOrders.AnnotationName)!.Value!).RootElement;

        recorded.EnumerateArray().Select(order => (
                order.GetProperty("table").GetString(),
                string.Join(",", order.GetProperty("columns").EnumerateArray().Select(column => column.GetString()))))
            .Should().Equal(
                ("SqlOSFgaPage_Products_Id", "Id"),
                ("SqlOSFgaPage_Products_Price", "Price,Id"),
                ("SqlOSFgaPage_Products_Status_CreatedAt", "Status,CreatedAt,Id"));
        SqlOSFgaPageOrders.For(products).Select(order => order.Description)
            .Should().Equal("PagedProduct(Id)", "PagedProduct(Price, Id)", "PagedProduct(Status, CreatedAt, Id)");
        products.FindAnnotation(SqlOSFgaPageOrders.SkipsAnnotationName).Should().BeNull();
    }

    [TestMethod]
    public void IndexWithTheKey_AndIndexWithoutIt_AreOneOrder()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasIndex(product => product.Price);
            entity.HasIndex(product => new { product.Price, product.Id });
            entity.HasIndex(product => product.Id);
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id", "SqlOSFgaPage_Products_Price");
        SqlOSFgaPageOrders.For(context.Model.FindEntityType(typeof(PagedProduct))!).Should().HaveCount(2);
    }

    [TestMethod]
    public void UniqueFilteredAndDescendingIndexes_MakeNoDifference()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasIndex(product => product.Sku).IsUnique().IsDescending().HasFilter("[Sku] <> ''");
            SqlServerIndexBuilderExtensions.IncludeProperties(entity.HasIndex(product => product.Price), product => product.Name);
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id", "SqlOSFgaPage_Products_Price", "SqlOSFgaPage_Products_Sku");
        context.Model.FindEntityType("SqlOSFgaPage_Products_Sku")!.GetIndexes().Should().ContainSingle()
            .Which.IsUnique.Should().BeFalse();
    }

    [TestMethod]
    public void LookupIndexes_GetNoOrder_AndNoWarning()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            // ResourceId is indexed by ConfigureProduct; a composite index containing it is a lookup too.
            entity.HasIndex(product => new { product.ResourceId, product.Name });
            // Exactly the foreign key's columns, written by hand as the examples do.
            entity.HasIndex(product => product.LocationId);
        });

        var products = context.Model.FindEntityType(typeof(PagedProduct))!;
        products.GetIndexes().Select(index => string.Join(",", index.Properties.Select(property => property.Name)))
            .Should().BeEquivalentTo("ResourceId", "ResourceId,Name", "LocationId");
        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        SqlOSFgaPageOrders.Skips(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void AttributeIndexes_AndConventionForeignKeyIndexes_AreVisibleWhenOrdersAreMade()
    {
        // The moment SqlOSDbContext applies the orders: the end of OnModelCreating, before the model is finalized.
        var modelBuilder = SqlServerConventionSetBuilder.CreateModelBuilder();
        modelBuilder.Entity<PagedIndexedProduct>(PageOrderModelContext.ConfigureIndexedProduct);
        var products = modelBuilder.Model.FindEntityType(typeof(PagedIndexedProduct))!;

        products.GetIndexes().Select(index => string.Join(",", index.Properties.Select(property => property.Name)))
            .Should().BeEquivalentTo(new[] { "Rank", "LocationId" }, "the [Index] attribute and the foreign key's convention index exist before the model is finalized");
        products.GetForeignKeys().Should().ContainSingle().Which.Properties.Select(property => property.Name).Should().Equal("LocationId");

        SqlOSFgaPageOrders.ApplyModel(modelBuilder.Model, providerName: null);

        EntryTables(modelBuilder.Model).Should().Equal("SqlOSFgaPage_IndexedProducts_Id", "SqlOSFgaPage_IndexedProducts_Rank");

        using var context = PageOrderModelContext.Create(builder =>
        {
            builder.Ignore<PagedProduct>();
            builder.Entity<PagedIndexedProduct>(PageOrderModelContext.ConfigureIndexedProduct);
        });
        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_IndexedProducts_Id", "SqlOSFgaPage_IndexedProducts_Rank");
        SqlOSFgaPageOrders.Skips(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void NullableColumn_SkipsTheIndex_WithAReason()
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasIndex(product => product.DeletedAt);
            entity.HasIndex(product => new { product.Price, product.DeletedAt });
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        var skips = SqlOSFgaPageOrders.Skips(context.Model);
        skips.Should().HaveCount(2).And.OnlyContain(skip => skip.EntityType.ClrType == typeof(PagedProduct));
        skips.Select(skip => string.Join(",", skip.Columns!)).Should().Equal("DeletedAt", "Price,DeletedAt");
        skips.Should().OnlyContain(skip => skip.Reason.Contains("'DeletedAt' is nullable") && skip.Reason.Contains("make 'DeletedAt' required"));
    }

    [TestMethod]
    public void StringWithoutMaximumLength_SkipsTheIndex()
        => Skip(entity => entity.HasIndex(product => product.Notes))
            .Should().Contain("'Notes' is a string without a maximum length");

    [TestMethod]
    public void UnsupportedColumnType_AndEntryColumnName_SkipTheirIndexes()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Ignore<PagedProduct>();
            modelBuilder.Entity<PagedOddRow>(entity =>
            {
                PageOrderModelContext.ConfigureOddRow(entity);
                entity.HasIndex(row => row.Blob);
                entity.HasIndex(row => row.NodeId);
            });
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_OddRows_Id");
        var skips = SqlOSFgaPageOrders.Skips(context.Model);
        skips.Select(skip => (skip.Columns![0], skip.Reason)).Should().SatisfyRespectively(
            blob =>
            {
                blob.Item1.Should().Be("Blob");
                blob.Reason.Should().Contain("'Blob' is a Byte[]").And.Contain("Page orders support strings, numbers, Guid, bool, enums and date/time values.");
            },
            nodeId =>
            {
                nodeId.Item1.Should().Be("NodeId");
                nodeId.Reason.Should().Contain("'NodeId' is a name the entry table uses for itself");
            });
    }

    [TestMethod]
    public void StringKeyWithoutMaximumLength_GetsItsOrders()
    {
        // The key is bounded by the engine's key convention (nvarchar(450) on SQL Server), as it was in 10.0.0.
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Ignore<PagedProduct>();
            modelBuilder.Entity<PagedStringKeyRow>(entity =>
            {
                entity.ToTable("StringKeyRows");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.ResourceId).HasMaxLength(450);
                entity.Property(row => row.Name).HasMaxLength(200);
                entity.HasIndex(row => new { row.Name, row.Id });
            });
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_StringKeyRows_Id", "SqlOSFgaPage_StringKeyRows_Name");
        SqlOSFgaPageOrders.Skips(context.Model).Should().BeEmpty();
        StoreTypes(context.Model, "SqlOSFgaPage_StringKeyRows_Name").Should().Equal(
            ("Name", "nvarchar(200)"),
            ("NodeId", "nvarchar(450)"),
            ("RowKey", "nvarchar(450)"));
    }

    [TestMethod]
    public void CompositeKey_SkipsTheEntity()
        => EntitySkip(entity => entity.HasKey(product => new { product.Id, product.Sku }))
            .Should().Contain("primary key is composite");

    [TestMethod]
    public void IgnoredResourceId_SkipsTheEntity()
        => EntitySkip(entity => entity.Ignore(product => product.ResourceId))
            .Should().Contain("has no mapped ResourceId");

    [TestMethod]
    public void ViewMapping_SkipsTheEntity()
        => EntitySkip(entity =>
        {
            entity.ToTable((string?)null);
            entity.ToView("ProductsView");
        }).Should().Contain("mapped to a view");

    [TestMethod]
    public void OwnedType_SkipsTheEntity()
    {
        using var context = PageOrderModelContext.Create(modelBuilder => modelBuilder.Entity<PagedProduct>(entity =>
        {
            PageOrderModelContext.ConfigureProduct(entity);
            entity.OwnsOne(product => product.Details);
        }));

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        var skip = SqlOSFgaPageOrders.Skips(context.Model).Should().ContainSingle().Subject;
        skip.EntityType.ClrType.Should().Be(typeof(PagedDetails));
        skip.Columns.Should().BeNull();
        skip.Reason.Should().Contain("is an owned type");
    }

    [TestMethod]
    public void TptInheritance_SkipsTheRoot_AndNothingElse()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(entity =>
            {
                PageOrderModelContext.ConfigureProduct(entity);
                entity.UseTptMappingStrategy();
                entity.HasIndex(product => product.Price);
            });
            modelBuilder.Entity<PagedSpecialProduct>().ToTable("SpecialProducts");
        });

        EntryTables(context.Model).Should().BeEmpty();
        var skip = SqlOSFgaPageOrders.Skips(context.Model).Should().ContainSingle().Subject;
        skip.EntityType.ClrType.Should().Be(typeof(PagedProduct));
        skip.Reason.Should().Contain("TPT or TPC");
    }

    [TestMethod]
    public void TphOrders_LiveOnTheRootOnly()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(entity =>
            {
                PageOrderModelContext.ConfigureProduct(entity);
                entity.HasIndex(product => product.Price);
            });
            modelBuilder.Entity<PagedSpecialProduct>();
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id", "SqlOSFgaPage_Products_Price");
        var root = context.Model.FindEntityType(typeof(PagedProduct))!;
        var derived = context.Model.FindEntityType(typeof(PagedSpecialProduct))!;
        derived.FindAnnotation(SqlOSFgaPageOrders.AnnotationName).Should().BeNull();
        SqlOSFgaPageOrders.For(derived).Should().BeSameAs(SqlOSFgaPageOrders.For(root)).And.HaveCount(2);
        SqlOSFgaPageOrders.Skips(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void ProtectedTypeUnderAnUnprotectedRoot_SkipsWithAReason()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Ignore<PagedProduct>();
            modelBuilder.Entity<PagedEntry>(entity =>
            {
                entity.ToTable("Entries");
                entity.HasKey(entry => entry.Id);
            });
            modelBuilder.Entity<PagedProtectedEntry>(entity => entity.Property(entry => entry.ResourceId).HasMaxLength(450));
        });

        EntryTables(context.Model).Should().BeEmpty();
        var skip = SqlOSFgaPageOrders.Skips(context.Model).Should().ContainSingle().Subject;
        skip.EntityType.ClrType.Should().Be(typeof(PagedProtectedEntry));
        skip.Columns.Should().BeNull();
        skip.Reason.Should().Be("it derives from PagedEntry, which does not implement IHasResourceId. Page orders live on the root of a hierarchy: implement IHasResourceId on PagedEntry.");
    }

    [TestMethod]
    public void TwoProtectedEntitiesSplitFromOneTable_TheSecondSkipsItsColliding()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(PageOrderModelContext.ConfigureProduct);
            modelBuilder.Entity<PagedProductSummary>(entity =>
            {
                entity.ToTable("Products");
                entity.HasKey(summary => summary.Id);
                entity.Property(summary => summary.ResourceId).HasMaxLength(450);
                entity.HasOne<PagedProduct>().WithOne().HasForeignKey<PagedProductSummary>(summary => summary.Id);
            });
        });

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        var skip = SqlOSFgaPageOrders.Skips(context.Model).Should().ContainSingle().Subject;
        skip.Columns.Should().Equal("Id");
        skip.Reason.Should().Contain("entry table name 'SqlOSFgaPage_Products_Id' is already used by another entity type");
    }

    [TestMethod]
    public void SqlOSOwnEntities_GetNoOrders()
    {
        using var context = PageOrderModelContext.Create(modelBuilder => modelBuilder.Ignore<PagedProduct>());

        context.Model.GetEntityTypes().Should().NotContain(entityType => typeof(IHasResourceId).IsAssignableFrom(entityType.ClrType));
        EntryTables(context.Model).Should().BeEmpty();
        SqlOSFgaPageOrders.All(context.Model).Should().BeEmpty();
        SqlOSFgaPageOrders.Skips(context.Model).Should().BeEmpty();
    }

    [TestMethod]
    public void Startup_LogsOneWarningPerSkip()
    {
        using var context = PageOrderModelContext.Create(modelBuilder =>
        {
            modelBuilder.Entity<PagedProduct>(entity =>
            {
                PageOrderModelContext.ConfigureProduct(entity);
                entity.HasIndex(product => product.DeletedAt);
                entity.HasIndex(product => new { product.Notes, product.Price });
            });
            modelBuilder.Entity<PagedEntry>(entity =>
            {
                entity.ToTable("Entries");
                entity.HasKey(entry => entry.Id);
            });
            modelBuilder.Entity<PagedProtectedEntry>(entity => entity.Property(entry => entry.ResourceId).HasMaxLength(450));
        });
        var logger = new ListLogger();

        SqlOSFgaPageOrderMaintenance.LogSkips(context.Model, logger);

        logger.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Warning);
        logger.Entries.Select(entry => entry.Message).Should().BeEquivalentTo(
            "PagedProduct: index (DeletedAt) has no page order: 'DeletedAt' is nullable. Page orders need non-nullable columns: make 'DeletedAt' required, or order by a non-nullable column. ToPageAsync pages PagedProduct by that order by reading the table in order and checking each row.",
            "PagedProduct: index (Notes, Price) has no page order: 'Notes' is a string without a maximum length. Page orders copy it into an index key: set HasMaxLength on 'Notes', or an explicit column type. ToPageAsync pages PagedProduct by that order by reading the table in order and checking each row.",
            "PagedProtectedEntry implements IHasResourceId but has no page orders: it derives from PagedEntry, which does not implement IHasResourceId. Page orders live on the root of a hierarchy: implement IHasResourceId on PagedEntry. ToPageAsync pages PagedProtectedEntry by reading the table in the page's order and checking each row.");
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
                    entity.HasIndex(product => new { product.Status, product.CreatedAt });
                }),
                postgreSql);

            var expected = new[] { $"SqlOSFgaPage_{table}_Id", $"SqlOSFgaPage_{table}_Status_CreatedAt" }
                .Select(full => full.Length <= limit
                    ? full
                    : $"{full[..(limit - 9)]}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant()}")
                .ToList();

            EntryTables(context.Model).Should().BeEquivalentTo(expected);
            expected.Should().OnlyContain(name => name.Length <= limit);
            if (postgreSql)
            {
                expected.Should().OnlyContain(name => name.Length == limit);
                expected.Should().OnlyContain(name => System.Text.RegularExpressions.Regex.IsMatch(name, "_[0-9a-f]{8}$"));
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
            entity.HasIndex(product => product.Price);
        });

        EntryTables(modelBuilder.Model).Should().BeEmpty();
        SqlOSFgaPageOrders.ApplyModel(modelBuilder.Model, providerName: null);
        EntryTables(modelBuilder.Model).Should().Equal("SqlOSFgaPage_Products_Id", "SqlOSFgaPage_Products_Price");
    }

    /// <summary>The reason an index on products was skipped; the model builds and keeps the key order.</summary>
    private static string Skip(Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PagedProduct>> configure)
    {
        using var context = PageOrderModelContext.CreateProducts(configure);

        EntryTables(context.Model).Should().Equal("SqlOSFgaPage_Products_Id");
        var skip = SqlOSFgaPageOrders.Skips(context.Model).Should().ContainSingle().Subject;
        skip.EntityType.ClrType.Should().Be(typeof(PagedProduct));
        skip.Columns.Should().NotBeNull();
        return skip.Reason;
    }

    /// <summary>The reason products got no orders at all; the model builds.</summary>
    private static string EntitySkip(Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PagedProduct>> configure)
    {
        using var context = PageOrderModelContext.CreateProducts(entity =>
        {
            entity.HasIndex(product => product.Price);
            configure(entity);
        });

        EntryTables(context.Model).Should().BeEmpty();
        SqlOSFgaPageOrders.For(context.Model.FindEntityType(typeof(PagedProduct))!).Should().BeEmpty();
        var skip = SqlOSFgaPageOrders.Skips(context.Model).Should().ContainSingle().Subject;
        skip.EntityType.ClrType.Should().Be(typeof(PagedProduct));
        skip.Columns.Should().BeNull();
        return skip.Reason;
    }

    private static List<string> EntryTables(IReadOnlyModel model)
        => model.GetEntityTypes()
            .Select(entityType => entityType.Name)
            .Where(name => name.StartsWith("SqlOSFgaPage_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<(string Column, string StoreType)> StoreTypes(IModel model, string table)
        => model.FindEntityType(table)!
            .GetTableMappings()
            .Single()
            .ColumnMappings
            .Select(mapping => (mapping.Column.Name, mapping.Column.StoreType))
            .OrderBy(column => column.Name, StringComparer.Ordinal);

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
