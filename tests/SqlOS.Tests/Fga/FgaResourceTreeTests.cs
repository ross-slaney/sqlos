using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Fga;

/// <summary>The resource tree: placement under a parent, moves, activity and deletion.</summary>
[TestClass]
public sealed class FgaResourceTreeTests
{
    private static readonly DateTime Now = FgaTestModel.Now;

    [TestMethod]
    public void A_child_is_placed_under_its_parent_and_its_creation_is_recorded()
    {
        var resource = SqlOSFgaResource.Create("doc", "Doc", "document", null, Ancestry("doc", "folder", "root"), Now);

        resource.ParentId.Should().Be("folder");
        resource.IsActive.Should().BeTrue();
        resource.CreatedAt.Should().Be(Now);
        Events(resource).Should().Equal(new FgaResourceCreated("doc", "folder", "document"));
    }

    [TestMethod]
    public void A_resource_cannot_be_its_own_parent()
    {
        var create = () => SqlOSFgaResource.Create("doc", "Doc", "document", null, Ancestry("doc", "doc"), Now);

        create.Should().Throw<InvalidOperationException>().WithMessage("FGA resource parent cannot be the resource itself.");
    }

    [TestMethod]
    public void A_resource_cannot_be_placed_under_its_own_descendant()
    {
        var folder = FgaTestModel.Resource("folder", "Folder", "folder", parentId: "root");

        var move = () => folder.MoveTo(Ancestry("folder", "doc", "folder", "root"), FgaActor.Host, Now);

        move.Should().Throw<InvalidOperationException>().WithMessage("FGA resource hierarchy contains a cycle.");
        folder.ParentId.Should().Be("root");
        Events(folder).Should().BeEmpty();
    }

    [TestMethod]
    public void A_place_deeper_than_the_maximum_is_refused_and_the_deepest_allowed_one_is_not()
    {
        var deepest = Ancestry("leaf", Enumerable.Range(1, 10).Select(level => $"level-{level}").ToArray());
        var tooDeep = Ancestry("leaf", Enumerable.Range(1, 11).Select(level => $"level-{level}").ToArray());

        SqlOSFgaResource.Create("leaf", "Leaf", "document", null, deepest, Now).ParentId.Should().Be("level-1");
        var create = () => SqlOSFgaResource.Create("leaf", "Leaf", "document", null, tooDeep, Now);

        create.Should().Throw<InvalidOperationException>().WithMessage("FGA resource hierarchy exceeds the configured maximum depth of 10.");
    }

    [TestMethod]
    public void Moving_records_both_parents_and_moving_to_the_same_parent_records_nothing()
    {
        var doc = FgaTestModel.Resource("doc", "Doc", "document", parentId: "alpha");

        doc.MoveTo(Ancestry("doc", "alpha"), FgaActor.Host, Now);
        doc.MoveTo(Ancestry("doc", "beta"), FgaActor.Host, Now);

        doc.ParentId.Should().Be("beta");
        Events(doc).Should().Equal(new FgaResourceMoved("doc", "alpha", "beta", FgaActor.Host));
    }

    [TestMethod]
    public void Deactivating_changes_only_the_resource_and_is_recorded_once()
    {
        var doc = FgaTestModel.Resource("doc", "Doc", "document");

        doc.ChangeActivity(false, FgaActor.Host, Now);
        doc.ChangeActivity(false, FgaActor.Host, Now);
        doc.ChangeActivity(true, FgaActor.Operator, Now);

        Events(doc).Should().Equal(
            new FgaResourceActivationChanged("doc", false, FgaActor.Host),
            new FgaResourceActivationChanged("doc", true, FgaActor.Operator));
    }

    [TestMethod]
    public async Task The_walk_stops_at_a_cycle_above_the_parent_so_a_malformed_tree_still_answers()
    {
        await using var context = CreateContext();
        context.Set<SqlOSFgaResource>().AddRange(
            FgaTestModel.Resource("a", "A", "folder", parentId: "b"),
            FgaTestModel.Resource("b", "B", "folder", parentId: "a"));
        await context.SaveChangesAsync();

        var ancestry = await new SqlOSFgaResourceTree(context).AncestryAsync("new", "a", CancellationToken.None);

        ancestry.Chain.Should().Equal("a", "b", "a");
        var place = () => ancestry.EnsureCanHold("new");
        place.Should().Throw<InvalidOperationException>().WithMessage("FGA resource hierarchy contains a cycle.");
    }

    [TestMethod]
    public async Task CreateResource_refuses_an_unknown_parent_instead_of_failing_the_save()
    {
        await using var context = CreateContext();
        await SeedModelAsync(context);

        var create = () => context.CreateResource("workspace::missing", "Stray", "workspace", "workspace::stray");

        create.Should().Throw<InvalidOperationException>().WithMessage("FGA resource 'workspace::missing' was not found.");
        context.ChangeTracker.Entries<SqlOSFgaResource>().Should().NotContain(entry => entry.Entity.Id == "workspace::stray");
    }

    [TestMethod]
    public async Task CreateResource_refuses_an_unknown_resource_type()
    {
        await using var context = CreateContext();
        await SeedModelAsync(context);

        var create = () => context.CreateResource("root", "Folder", "folder", "folder::one");

        create.Should().Throw<InvalidOperationException>()
            .WithMessage("FGA resource type 'folder' was not found. Seed or create the resource type before provisioning resources.");
    }

    [TestMethod]
    public async Task CreateResource_places_a_resource_under_one_the_unit_already_tracks()
    {
        await using var context = CreateContext();
        await SeedModelAsync(context);

        var parent = context.CreateResource("root", "Parent", "workspace", "workspace::parent");
        context.CreateResource(parent, "Child", "workspace", "workspace::child");
        await context.SaveChangesAsync();

        (await context.Set<SqlOSFgaResource>().SingleAsync(resource => resource.Id == "workspace::child")).ParentId.Should().Be(parent);
    }

    [TestMethod]
    public async Task CreateResource_without_a_parent_creates_a_root_as_7_x_did()
    {
        await using var context = CreateContext();
        await SeedModelAsync(context);

        context.CreateResource(null!, "Second root", "workspace", "workspace::second-root");
        await context.SaveChangesAsync();

        (await context.Set<SqlOSFgaResource>().SingleAsync(resource => resource.Id == "workspace::second-root")).ParentId.Should().BeNull();
    }

    [TestMethod]
    public async Task Host_moves_deactivations_and_deletions_are_audited_and_creations_are_not()
    {
        await using var context = CreateContext();
        await SeedModelAsync(context);
        await context.CreateResourceWithIdAsync("workspace::alpha", "workspace", "Alpha", "root");
        await context.CreateResourceWithIdAsync("workspace::beta", "workspace", "Beta", "root");
        await context.SaveChangesAsync();
        (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(0);

        await context.ProvisionResourceWithIdAsync("workspace::beta", "workspace", "Beta", parentResourceId: "workspace::alpha", isActive: false);
        await context.SaveChangesAsync();
        await context.DeleteResourceAsync("workspace::beta");
        await context.SaveChangesAsync();

        var rows = await context.Set<SqlOSAuditEvent>().OrderBy(row => row.IngestedAt).ToListAsync();
        rows.Select(row => row.EventType).Should().Equal("fga.resource.moved", "fga.resource.deactivated", "fga.resource.deleted");
        rows.Should().OnlyContain(row => row.Source == "fga" && row.ActorType == "application");
        rows[0].MetadataJson.Should().Contain("\"fromParentId\":\"root\"").And.Contain("\"toParentId\":\"workspace::alpha\"");
    }

    private static SqlOSFgaAncestry Ancestry(string resourceId, params string[] chain)
    {
        var parents = chain.Zip(chain.Skip(1).Cast<string?>().Append(null)).ToDictionary(pair => pair.First, pair => pair.Second);
        return SqlOSFgaAncestry.WalkAsync(
                resourceId,
                chain[0],
                10,
                id => Task.FromResult(parents.TryGetValue(id, out var parent) ? (true, parent) : (false, (string?)null)))
            .GetAwaiter()
            .GetResult();
    }

    private static IReadOnlyList<ISqlOSDomainEvent> Events(ISqlOSAggregate aggregate) => aggregate.Events.Pending;

    private static async Task SeedModelAsync(TestSqlOSInMemoryDbContext context)
    {
        context.Set<SqlOSFgaResourceType>().AddRange(FgaTestModel.ResourceType("root"), FgaTestModel.ResourceType("workspace"));
        context.Set<SqlOSFgaResource>().Add(FgaTestModel.Resource("root", "Root", "root"));
        await context.SaveChangesAsync();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"fga-tree-{Guid.NewGuid():N}")
            .Options);
}
