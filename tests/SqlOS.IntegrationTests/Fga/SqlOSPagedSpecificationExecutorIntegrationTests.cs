using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;
using SqlOS.Fga.Specifications;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public sealed class SqlOSPagedSpecificationExecutorIntegrationTests : FgaIntegrationTestBase
{
    [TestMethod]
    public async Task ExecuteAsync_DateTimePageLoop_DoesNotSkipOrDuplicateRows()
    {
        var prefix = $"page_{Guid.NewGuid():N}"[..16];
        var stampA = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var stampB = new DateTime(2024, 6, 15, 13, 0, 0, DateTimeKind.Utc);
        var stampC = new DateTime(2024, 6, 16, 9, 0, 0, DateTimeKind.Utc);

        Context.Set<CursorPagedTestEntity>().AddRange(
            new CursorPagedTestEntity { Id = $"{prefix}-a", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampA },
            new CursorPagedTestEntity { Id = $"{prefix}-b", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampA },
            new CursorPagedTestEntity { Id = $"{prefix}-c", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampB },
            new CursorPagedTestEntity { Id = $"{prefix}-d", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampB },
            new CursorPagedTestEntity { Id = $"{prefix}-e", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampC },
            new CursorPagedTestEntity { Id = $"{prefix}-f", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampC },
            new CursorPagedTestEntity { Id = $"{prefix}-g", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampC.AddHours(1) },
            new CursorPagedTestEntity { Id = $"{prefix}-hidden", ResourceId = FgaTestDataSeeder.OtherAgencyResourceId, CreatedAt = stampA });
        await Context.SaveChangesAsync();

        var executor = CreateExecutor();
        var collected = await CollectPagesAsync(
            executor,
            FgaTestDataSeeder.AgencyMemberSubjectId,
            prefix,
            pageSize: 2,
            descending: false);

        var expected = new[]
        {
            $"{prefix}-a", $"{prefix}-b", $"{prefix}-c", $"{prefix}-d",
            $"{prefix}-e", $"{prefix}-f", $"{prefix}-g"
        };
        collected.Should().Equal(expected);
        collected.Should().NotContain($"{prefix}-hidden");

        var unauthorized = await CollectPagesAsync(
            executor,
            FgaTestDataSeeder.UnauthorizedSubjectId,
            prefix,
            pageSize: 2,
            descending: false);
        unauthorized.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ExecuteAsync_DateTimePageLoop_Descending_DoesNotSkipOrDuplicateRows()
    {
        var prefix = $"desc_{Guid.NewGuid():N}"[..16];
        var stampA = new DateTime(2024, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var stampB = new DateTime(2024, 7, 1, 9, 0, 0, DateTimeKind.Utc);

        Context.Set<CursorPagedTestEntity>().AddRange(
            new CursorPagedTestEntity { Id = $"{prefix}-a", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampA },
            new CursorPagedTestEntity { Id = $"{prefix}-b", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampA },
            new CursorPagedTestEntity { Id = $"{prefix}-c", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampB },
            new CursorPagedTestEntity { Id = $"{prefix}-d", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampB },
            new CursorPagedTestEntity { Id = $"{prefix}-e", ResourceId = FgaTestDataSeeder.TestTeamResourceId, CreatedAt = stampB.AddHours(1) });
        await Context.SaveChangesAsync();

        var collected = await CollectPagesAsync(
            CreateExecutor(),
            FgaTestDataSeeder.AgencyMemberSubjectId,
            prefix,
            pageSize: 2,
            descending: true);

        collected.Should().Equal(
            $"{prefix}-e", $"{prefix}-d", $"{prefix}-c", $"{prefix}-b", $"{prefix}-a");
        collected.Should().OnlyHaveUniqueItems();
    }

    private SpecificationExecutor CreateExecutor()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var authService = new SqlOSFgaAuthService(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaAuthService>());
        return new SpecificationExecutor(
            authService,
            loggerFactory.CreateLogger<SpecificationExecutor>());
    }

    private async Task<List<string>> CollectPagesAsync(
        SpecificationExecutor executor,
        string subjectId,
        string prefix,
        int pageSize,
        bool descending)
    {
        var ids = new List<string>();
        string? cursor = null;
        for (var i = 0; i < 10; i++)
        {
            var spec = PagedSpec.For<CursorPagedTestEntity>(row => row.Id)
                .RequirePermission("TEST_VIEW")
                .SortBy("createdAt", row => row.CreatedAt, isDefault: true)
                .Where(row => row.Id.StartsWith(prefix))
                .Build(pageSize, cursor, descending: descending);

            var page = await executor.ExecuteAsync(
                Context.Set<CursorPagedTestEntity>(),
                spec,
                subjectId,
                row => row.Id);

            ids.AddRange(page.Data);
            if (!page.HasNextPage)
                break;
            cursor = page.NextCursor;
        }

        return ids;
    }
}
