using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSResourceEntitySyncTests
{
    [TestMethod]
    public void SaveChanges_ReturnsWhenNoResourceEntityChanged()
    {
        using var context = CreateContext();

        context.SaveChanges().Should().Be(0);
    }

    [TestMethod]
    public void SaveChanges_CreatesUpdatesAndDeletesBackingResourceAndGrants()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
        {
            Id = "usr_1",
            SubjectTypeId = "user",
            DisplayName = "User One"
        });
        var entity = new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            Description = "Initial workspace"
        };
        context.Resources.Add(entity);

        context.SaveChanges();

        var resource = context.Set<SqlOSFgaResource>().Single(x => x.Id == "workspace_1");
        resource.Name.Should().Be("Workspace 1");
        resource.Description.Should().Be("Initial workspace");

        entity.Name = "Workspace One";
        entity.Description = "Updated workspace";
        entity.IsActive = false;
        context.SaveChanges();

        resource = context.Set<SqlOSFgaResource>().Single(x => x.Id == "workspace_1");
        resource.Name.Should().Be("Workspace One");
        resource.Description.Should().Be("Updated workspace");
        resource.IsActive.Should().BeFalse();

        context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
        {
            Id = "grant_1",
            SubjectId = "usr_1",
            ResourceId = "workspace_1",
            RoleId = "role_owner"
        });
        context.SaveChanges();

        context.Resources.Remove(entity);
        context.SaveChanges();

        context.Set<SqlOSFgaResource>().Any(x => x.Id == "workspace_1").Should().BeFalse();
        context.Set<SqlOSFgaGrant>().Any(x => x.ResourceId == "workspace_1").Should().BeFalse();
    }

    [TestMethod]
    public void SaveChanges_AllowsParentAndChildAddedTogetherOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.AddRange(
            new ResourceBackedEntity
            {
                Id = "workspace_parent",
                Name = "Workspace parent"
            },
            new ResourceBackedEntity
            {
                Id = "workspace_child",
                Name = "Workspace child",
                ParentId = "workspace_parent"
            });

        context.SaveChanges();

        context.Set<SqlOSFgaResource>()
            .Single(x => x.Id == "workspace_child")
            .ParentId
            .Should()
            .Be("workspace_parent");
    }

    [TestMethod]
    public void SaveChanges_RejectsDuplicateResourceIdsOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.AddRange(
            new ResourceBackedEntity
            {
                Id = "entity_1",
                ResourceKey = "workspace_1",
                Name = "Workspace 1"
            },
            new ResourceBackedEntity
            {
                Id = "entity_2",
                ResourceKey = "workspace_1",
                Name = "Workspace duplicate"
            });

        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Multiple tracked SqlOS resource entities use resource id 'workspace_1'*");
    }

    [TestMethod]
    public void SaveChanges_RejectsMissingResourceTypeOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            TypeId = "workpsace"
        });

        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*resource type 'workpsace' was not found*");
    }

    [TestMethod]
    public void SaveChanges_RejectsMissingParentOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            ParentId = "missing_parent"
        });

        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*resource 'missing_parent' was not found*");
    }

    [TestMethod]
    public void SaveChanges_RejectsExistingBackingResourceForAddedEntityOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "workspace_1",
            Name = "Existing workspace",
            ResourceTypeId = "workspace",
            ParentId = "root"
        });
        context.SaveChanges();
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1"
        });

        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already exists*new resource-backed entity*");
    }

    [TestMethod]
    public void SaveChanges_RejectsMissingBackingResourceForModifiedEntityOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);
        context.SaveChanges();

        var resource = context.Set<SqlOSFgaResource>().Single(x => x.Id == "workspace_1");
        context.Set<SqlOSFgaResource>().Remove(resource);
        context.SaveChanges();

        entity.Name = "Workspace One";
        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*was not found for a modified resource-backed entity*");
    }

    [TestMethod]
    public void SaveChanges_DeleteFailsForLocalChildOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);
        context.SaveChanges();

        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "workspace_child",
            ParentId = "workspace_1",
            Name = "Workspace child",
            ResourceTypeId = "workspace"
        });
        context.Resources.Remove(entity);

        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*has child resources*Delete or reparent child resources*");
    }

    [TestMethod]
    public void SaveChanges_RejectsCycleOnSyncPath()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.AddRange(
            new ResourceBackedEntity
            {
                Id = "workspace_a",
                Name = "Workspace A",
                ParentId = "workspace_b"
            },
            new ResourceBackedEntity
            {
                Id = "workspace_b",
                Name = "Workspace B",
                ParentId = "workspace_a"
            });

        Action act = () => context.SaveChanges();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*hierarchy contains a cycle*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_CreatesBackingResourceForAddedEntity()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            Description = "Initial workspace"
        });

        await context.SaveChangesAsync();

        var resource = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_1");
        resource.Name.Should().Be("Workspace 1");
        resource.Description.Should().Be("Initial workspace");
        resource.ResourceTypeId.Should().Be("workspace");
        resource.ParentId.Should().Be("root");
        resource.IsActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task SaveChangesAsync_UpdatesBackingResourceForModifiedEntity()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);
        await context.SaveChangesAsync();

        entity.Name = "Workspace One";
        entity.Description = "Updated";
        entity.IsActive = false;
        await context.SaveChangesAsync();

        var resource = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_1");
        resource.Name.Should().Be("Workspace One");
        resource.Description.Should().Be("Updated");
        resource.IsActive.Should().BeFalse();
    }

    [TestMethod]
    public async Task SaveChangesAsync_DeletesBackingResourceAndGrantsForDeletedEntity()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.EnsureSqlOSUserSubjectAsync("usr_1", "User One");
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);
        await context.SaveChangesAsync();

        await context.GrantRoleAsync("usr_1", entity, "owner");
        await context.SaveChangesAsync();

        context.Resources.Remove(entity);
        await context.SaveChangesAsync();

        (await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == "workspace_1")).Should().BeFalse();
        (await context.Set<SqlOSFgaGrant>().AnyAsync(x => x.ResourceId == "workspace_1")).Should().BeFalse();
    }

    [TestMethod]
    public async Task SaveChangesAsync_DeleteFailsWhenChildResourcesStillExist()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);
        await context.SaveChangesAsync();
        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "workspace_child",
            ParentId = "workspace_1",
            Name = "Workspace child",
            ResourceTypeId = "workspace"
        });
        await context.SaveChangesAsync();

        context.Resources.Remove(entity);
        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has child resources*Delete or reparent child resources*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_RequiresExistingResourceType()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            TypeId = "workpsace"
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource type 'workpsace' was not found*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_RequiresExistingParent()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            ParentId = "missing_parent"
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource 'missing_parent' was not found*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_AllowsParentAndChildAddedTogether()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.AddRange(
            new ResourceBackedEntity
            {
                Id = "workspace_1",
                Name = "Workspace 1"
            },
            new ResourceBackedEntity
            {
                Id = "workspace_child",
                Name = "Workspace child",
                ParentId = "workspace_1"
            });

        await context.SaveChangesAsync();

        var child = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_child");
        child.ParentId.Should().Be("workspace_1");
    }

    [TestMethod]
    public async Task SaveChangesAsync_RejectsSelfParent()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1",
            ParentId = "workspace_1"
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*parent cannot be the resource itself*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_RejectsPendingCycle()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Resources.AddRange(
            new ResourceBackedEntity
            {
                Id = "workspace_a",
                Name = "Workspace A",
                ParentId = "workspace_b"
            },
            new ResourceBackedEntity
            {
                Id = "workspace_b",
                Name = "Workspace B",
                ParentId = "workspace_a"
            });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*hierarchy contains a cycle*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_RejectsAddedEntityWhenBackingResourceAlreadyExists()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "workspace_1",
            Name = "Existing workspace",
            ResourceTypeId = "workspace",
            ParentId = "root"
        });
        await context.SaveChangesAsync();
        context.Resources.Add(new ResourceBackedEntity
        {
            Id = "workspace_1",
            Name = "Workspace 1"
        });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*new resource-backed entity*");
    }

    [TestMethod]
    public async Task SaveChangesAsync_RejectsModifiedEntityWhenBackingResourceIsMissing()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);
        await context.SaveChangesAsync();

        var resource = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_1");
        context.Set<SqlOSFgaResource>().Remove(resource);
        await context.SaveChangesAsync();

        entity.Name = "Workspace One";
        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found for a modified resource-backed entity*");
    }

    [TestMethod]
    public async Task GrantRoleAsync_AllowsPendingResourceEntity()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.EnsureSqlOSUserSubjectAsync("usr_1", "User One");
        var entity = new ResourceBackedEntity { Id = "workspace_1", Name = "Workspace 1" };
        context.Resources.Add(entity);

        var grant = await context.GrantRoleAsync("usr_1", entity, "owner");
        await context.SaveChangesAsync();

        grant.ResourceId.Should().Be("workspace_1");
        (await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == "workspace_1")).Should().BeTrue();
        (await context.Set<SqlOSFgaGrant>().AnyAsync(x => x.ResourceId == "workspace_1")).Should().BeTrue();
    }

    private static ResourceEntityTestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ResourceEntityTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ResourceEntityTestDbContext(options);
    }

    private static void SeedFgaCore(ResourceEntityTestDbContext context)
    {
        context.Set<SqlOSFgaSubjectType>().Add(new SqlOSFgaSubjectType { Id = "user", Name = "User" });
        context.Set<SqlOSFgaResourceType>().AddRange(
            new SqlOSFgaResourceType { Id = "root", Name = "Root" },
            new SqlOSFgaResourceType { Id = "workspace", Name = "Workspace" });
        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "root",
            Name = "Root",
            ResourceTypeId = "root"
        });
        context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole
        {
            Id = "role_owner",
            Key = "owner",
            Name = "Owner"
        });
    }

    private sealed class ResourceEntityTestDbContext(DbContextOptions<ResourceEntityTestDbContext> options)
        : SqlOSDbContext<ResourceEntityTestDbContext>(options)
    {
        public DbSet<ResourceBackedEntity> Resources => Set<ResourceBackedEntity>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ResourceBackedEntity>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).HasMaxLength(128);
                entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
                entity.Property(x => x.TypeId).HasMaxLength(128).IsRequired();
                entity.Property(x => x.ParentId).HasMaxLength(128);
                entity.Ignore(x => x.ResourceId);
                entity.Ignore(x => x.ResourceTypeId);
                entity.Ignore(x => x.ResourceName);
                entity.Ignore(x => x.ParentResourceId);
                entity.Ignore(x => x.ResourceDescription);
                entity.Ignore(x => x.ResourceIsActive);
            });
        }
    }

    private sealed class ResourceBackedEntity : ISqlOSResourceEntity
    {
        public string Id { get; set; } = string.Empty;
        public string? ResourceKey { get; set; }
        public string TypeId { get; set; } = "workspace";
        public string Name { get; set; } = string.Empty;
        public string? ParentId { get; set; } = "root";
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;

        public string ResourceId => ResourceKey ?? Id;
        public string ResourceTypeId => TypeId;
        public string ResourceName => Name;
        public string? ParentResourceId => ParentId;
        public string? ResourceDescription => Description;
        public bool ResourceIsActive => IsActive;
    }
}
