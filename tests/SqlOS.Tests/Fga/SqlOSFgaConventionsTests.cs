using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Tests.Fga;

// ---------------------------------------------------------------------------
// Minimal consumer entity types used only by these tests
// ---------------------------------------------------------------------------

/// <summary>Entity that implements IHasResourceId but has no manually-defined index.</summary>
internal class EntityWithNoIndex : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
}

/// <summary>Entity that already has an explicit single-column index on ResourceId.</summary>
internal class EntityWithExplicitIndex : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
}

/// <summary>Entity that already has a composite index whose leading column is ResourceId.</summary>
internal class EntityWithCompositeIndex : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>Entity that does NOT implement IHasResourceId – should never be touched.</summary>
internal class EntityWithoutResourceId
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Test DbContext helpers
// ---------------------------------------------------------------------------

/// <summary>
/// Context used to verify that ApplySqlOSFgaConventions() adds a missing index.
/// </summary>
internal class ContextWithAutoIndex : DbContext, ISqlOSFgaDbContext
{
    public ContextWithAutoIndex(DbContextOptions<ContextWithAutoIndex> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithNoIndex>(e => e.HasKey(x => x.Id));

        // Call last so all entities are registered
        modelBuilder.ApplySqlOSFgaConventions();
    }
}

/// <summary>
/// Context used to verify that an entity with an explicit index is not given a duplicate.
/// </summary>
internal class ContextWithExplicitIndex : DbContext, ISqlOSFgaDbContext
{
    public ContextWithExplicitIndex(DbContextOptions<ContextWithExplicitIndex> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithExplicitIndex>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ResourceId); // explicit single-column index
        });

        modelBuilder.ApplySqlOSFgaConventions();
    }
}

/// <summary>
/// Context used to verify that an entity with a composite index (ResourceId first) is not modified.
/// </summary>
internal class ContextWithCompositeIndex : DbContext, ISqlOSFgaDbContext
{
    public ContextWithCompositeIndex(DbContextOptions<ContextWithCompositeIndex> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithCompositeIndex>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ResourceId, x.TenantId }); // composite index, ResourceId first
        });

        modelBuilder.ApplySqlOSFgaConventions();
    }
}

/// <summary>
/// Context used to verify that a non-IHasResourceId entity is never touched.
/// </summary>
internal class ContextWithNonResourceIdEntity : DbContext, ISqlOSFgaDbContext
{
    public ContextWithNonResourceIdEntity(DbContextOptions<ContextWithNonResourceIdEntity> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithoutResourceId>(e => e.HasKey(x => x.Id));

        modelBuilder.ApplySqlOSFgaConventions();
    }
}

/// <summary>
/// Context used to verify validation throws when AutoIndexResourceIds is disabled.
/// </summary>
internal class ContextWithValidationEnabled : DbContext, ISqlOSFgaDbContext
{
    public ContextWithValidationEnabled(DbContextOptions<ContextWithValidationEnabled> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithNoIndex>(e => e.HasKey(x => x.Id));

        // AutoIndexResourceIds disabled, but ValidateResourceIdIndexes enabled
        modelBuilder.ApplySqlOSFgaConventions(o =>
        {
            o.AutoIndexResourceIds = false;
            o.ValidateResourceIdIndexes = true;
        });
    }
}

/// <summary>
/// Context used to verify validation passes when all entities are already indexed.
/// </summary>
internal class ContextWithValidationPassingAllIndexed : DbContext, ISqlOSFgaDbContext
{
    public ContextWithValidationPassingAllIndexed(
        DbContextOptions<ContextWithValidationPassingAllIndexed> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithExplicitIndex>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ResourceId);
        });

        // Both flags true: auto-index will satisfy validation, no exception
        modelBuilder.ApplySqlOSFgaConventions(o =>
        {
            o.AutoIndexResourceIds = true;
            o.ValidateResourceIdIndexes = true;
        });
    }
}

/// <summary>
/// Context used to verify that when both flags are false, no indexes are added.
/// </summary>
internal class ContextWithConventionsDisabled : DbContext, ISqlOSFgaDbContext
{
    public ContextWithConventionsDisabled(DbContextOptions<ContextWithConventionsDisabled> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();

        modelBuilder.Entity<EntityWithNoIndex>(e => e.HasKey(x => x.Id));

        modelBuilder.ApplySqlOSFgaConventions(o =>
        {
            o.AutoIndexResourceIds = false;
            o.ValidateResourceIdIndexes = false;
        });
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[TestClass]
public class SqlOSFgaConventionsTests
{
    private static DbContextOptions<T> InMemoryOptions<T>(string dbName) where T : DbContext
        => new DbContextOptionsBuilder<T>()
            .UseInMemoryDatabase(dbName)
            .Options;

    private static bool HasResourceIdIndex(IReadOnlyEntityType entityType)
        => entityType.GetIndexes()
            .Any(idx => idx.Properties.Count > 0 &&
                        idx.Properties[0].Name == nameof(IHasResourceId.ResourceId));

    // ------------------------------------------------------------------
    // Auto-indexing
    // ------------------------------------------------------------------

    [TestMethod]
    public void ApplySqlOSFgaConventions_AutoIndex_AddsIndexForEntityWithoutOne()
    {
        using var ctx = new ContextWithAutoIndex(
            InMemoryOptions<ContextWithAutoIndex>("AutoIndex_AddsIndex"));

        var entityType = ctx.Model.FindEntityType(typeof(EntityWithNoIndex));
        entityType.Should().NotBeNull();
        HasResourceIdIndex(entityType!).Should().BeTrue(
            "ApplySqlOSFgaConventions with AutoIndexResourceIds=true should add a ResourceId index");
    }

    [TestMethod]
    public void ApplySqlOSFgaConventions_AutoIndex_DoesNotAddIndexToNonIHasResourceIdEntity()
    {
        using var ctx = new ContextWithNonResourceIdEntity(
            InMemoryOptions<ContextWithNonResourceIdEntity>("AutoIndex_SkipsNonResourceId"));

        var entityType = ctx.Model.FindEntityType(typeof(EntityWithoutResourceId));
        entityType.Should().NotBeNull();
        entityType!.GetIndexes().Should().BeEmpty(
            "entities that do not implement IHasResourceId should not get an auto-added index");
    }

    // ------------------------------------------------------------------
    // Duplicate-index avoidance
    // ------------------------------------------------------------------

    [TestMethod]
    public void ApplySqlOSFgaConventions_AutoIndex_DoesNotDuplicateExplicitResourceIdIndex()
    {
        using var ctx = new ContextWithExplicitIndex(
            InMemoryOptions<ContextWithExplicitIndex>("AutoIndex_NoDuplicate"));

        var entityType = ctx.Model.FindEntityType(typeof(EntityWithExplicitIndex));
        entityType.Should().NotBeNull();

        var resourceIdIndexes = entityType!
            .GetIndexes()
            .Where(idx => idx.Properties.Count > 0 &&
                          idx.Properties[0].Name == nameof(IHasResourceId.ResourceId))
            .ToList();

        resourceIdIndexes.Should().HaveCount(1,
            "an existing explicit ResourceId index should not be duplicated by conventions");
    }

    [TestMethod]
    public void ApplySqlOSFgaConventions_AutoIndex_DoesNotDuplicateCompositeIndexWithResourceIdFirst()
    {
        using var ctx = new ContextWithCompositeIndex(
            InMemoryOptions<ContextWithCompositeIndex>("AutoIndex_NoCompositeIndexDuplicate"));

        var entityType = ctx.Model.FindEntityType(typeof(EntityWithCompositeIndex));
        entityType.Should().NotBeNull();

        var resourceIdIndexes = entityType!
            .GetIndexes()
            .Where(idx => idx.Properties.Count > 0 &&
                          idx.Properties[0].Name == nameof(IHasResourceId.ResourceId))
            .ToList();

        resourceIdIndexes.Should().HaveCount(1,
            "an existing composite index whose leading column is ResourceId should not be duplicated");
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    [TestMethod]
    public void ApplySqlOSFgaConventions_ValidateOnly_ThrowsWhenEntityHasNoIndex()
    {
        var act = () => new ContextWithValidationEnabled(
            InMemoryOptions<ContextWithValidationEnabled>("Validation_Throws"));

        // EF builds the model on first access; we trigger that by accessing the Model property.
        act.Invoking(create =>
        {
            using var ctx = create();
            _ = ctx.Model; // forces OnModelCreating
        }).Should().Throw<InvalidOperationException>()
          .WithMessage($"*{nameof(IHasResourceId)}*")
          .And.Message.Should().Contain(nameof(EntityWithNoIndex));
    }

    [TestMethod]
    public void ApplySqlOSFgaConventions_BothFlagsTrue_DoesNotThrowBecauseAutoIndexSatisfiesValidation()
    {
        var act = () =>
        {
            using var ctx = new ContextWithValidationPassingAllIndexed(
                InMemoryOptions<ContextWithValidationPassingAllIndexed>("Validation_PassesWhenAlreadyIndexed"));
            _ = ctx.Model;
        };

        act.Should().NotThrow(
            "auto-indexing runs before validation, so the validation step should always find indexes");
    }

    [TestMethod]
    public void ApplySqlOSFgaConventions_BothFlagsTrue_AddsIndexAndPassesValidationForEntityWithNoManualIndex()
    {
        // EntityWithNoIndex has no manual index; with AutoIndex=true+Validate=true we expect:
        // 1) an index is added automatically
        // 2) validation passes (no throw)
        using var ctx = new ContextAutoIndexAndValidate(
            InMemoryOptions<ContextAutoIndexAndValidate>("AutoIndex_And_Validate_Passes"));

        var entityType = ctx.Model.FindEntityType(typeof(EntityWithNoIndex));
        entityType.Should().NotBeNull();
        HasResourceIdIndex(entityType!).Should().BeTrue();
    }

    [TestMethod]
    public void ApplySqlOSFgaConventions_BothFlagsDisabled_DoesNotAddIndex()
    {
        using var ctx = new ContextWithConventionsDisabled(
            InMemoryOptions<ContextWithConventionsDisabled>("Conventions_Disabled"));

        var entityType = ctx.Model.FindEntityType(typeof(EntityWithNoIndex));
        entityType.Should().NotBeNull();
        HasResourceIdIndex(entityType!).Should().BeFalse(
            "when AutoIndexResourceIds=false no index should be added by conventions");
    }
}

// Extra context needed for the combined auto-index + validate test
internal class ContextAutoIndexAndValidate : DbContext, ISqlOSFgaDbContext
{
    public ContextAutoIndexAndValidate(DbContextOptions<ContextAutoIndexAndValidate> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();
        modelBuilder.Entity<EntityWithNoIndex>(e => e.HasKey(x => x.Id));

        modelBuilder.ApplySqlOSFgaConventions(o =>
        {
            o.AutoIndexResourceIds = true;
            o.ValidateResourceIdIndexes = true;
        });
    }
}
