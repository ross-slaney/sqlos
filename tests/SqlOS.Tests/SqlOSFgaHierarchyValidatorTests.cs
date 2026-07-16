using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSFgaHierarchyValidatorTests
{
    [TestMethod]
    public async Task ValidateExistingDataAsync_ReportsPersistedTreeBeyondConfiguredDepth()
    {
        await using var context = CreateContext();
        SeedResources(context,
            ("root", null),
            ("level_1", "root"),
            ("level_2", "level_1"),
            ("level_3", "level_2"));
        await context.SaveChangesAsync();
        var validator = new SqlOSFgaHierarchyValidator(
            context,
            Options.Create(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 }));

        var act = async () => await validator.ValidateExistingDataAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'level_3'*configured maximum hierarchy depth of 2*repair*");
    }

    [TestMethod]
    public async Task ValidateExistingDataAsync_AcceptsTreeAtConfiguredDepth()
    {
        await using var context = CreateContext();
        SeedResources(context,
            ("root", null),
            ("level_1", "root"),
            ("level_2", "level_1"));
        await context.SaveChangesAsync();
        var validator = new SqlOSFgaHierarchyValidator(
            context,
            Options.Create(new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 }));

        var act = async () => await validator.ValidateExistingDataAsync();

        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateExistingDataAsync_ReportsPersistedCycle()
    {
        await using var context = CreateContext();
        SeedResources(context,
            ("resource_a", "resource_b"),
            ("resource_b", "resource_a"));
        await context.SaveChangesAsync();
        var validator = new SqlOSFgaHierarchyValidator(
            context,
            Options.Create(new SqlOSFgaOptions()));

        var act = async () => await validator.ValidateExistingDataAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*contains a cycle*");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static void SeedResources(
        TestSqlOSInMemoryDbContext context,
        params (string Id, string? ParentId)[] resources)
    {
        context.Set<SqlOSFgaResource>().AddRange(resources.Select(resource => new SqlOSFgaResource
        {
            Id = resource.Id,
            ParentId = resource.ParentId,
            Name = resource.Id,
            ResourceTypeId = "workspace"
        }));
    }
}
