using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Extensions;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Pagination;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// ToPageAsync returns, page by page, exactly what <c>.Where(filter)</c> returns in the same
/// order, for every caller, order, direction, page size and application filter.
/// </summary>
[TestClass]
public class SqlOSFgaPageOrderIntegrationTests
{
    [TestInitialize]
    public Task Seed() => PageOrderFixture.EnsureSeededAsync();

    [TestMethod]
    public async Task WalkingEveryPage_EqualsTheFilteredQuery_ForEveryCallerOrderDirectionAndPageSize()
    {
        foreach (var caller in PageOrderFixture.Callers)
        {
            await using var context = PageOrderFixture.CreateContext();
            var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(caller, PageOrderFixture.ViewPermission);
            foreach (var order in new[] { "Rank", "Title" })
            {
                foreach (var descending in new[] { false, true })
                {
                    foreach (var withApplicationFilter in new[] { false, true })
                    {
                        IQueryable<PagedDocument> query = context.Documents.AsNoTracking();
                        if (withApplicationFilter)
                        {
                            query = query.Where(document => document.Title.Contains(PageOrderFixture.FilterTerm));
                        }

                        var expected = await PageOrderFixture.OrderWithKey(query.Where(filter), order, descending)
                            .Select(document => document.Id)
                            .ToListAsync();
                        foreach (var pageSize in new[] { 1, 7, 100 })
                        {
                            var pages = await PageOrderFixture.WalkAsync(PageOrderFixture.Order(query, order, descending), filter, pageSize);

                            AssertPages(pages, expected, pageSize, $"{caller} {order} {(descending ? "desc" : "asc")} filter={withApplicationFilter} size={pageSize}");
                        }
                    }
                }
            }
        }
    }

    [TestMethod]
    public async Task CursorFromTheMiddle_ContinuesWhereThePageEnded()
    {
        await using var context = PageOrderFixture.CreateContext();
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(
            FgaTestDataSeeder.AgencyAdminSubjectId,
            PageOrderFixture.ViewPermission);
        var ordered = context.Documents.AsNoTracking().OrderByDescending(document => document.Title);
        var expected = await ordered.ThenByDescending(document => document.Id).Where(filter).Select(document => document.Id).ToListAsync();

        var first = await ordered.ToPageAsync(filter, 10);
        var second = await ordered.ToPageAsync(filter, 10, first.NextCursor);
        var rest = await PageOrderFixture.WalkAsync(ordered, filter, 25, second.NextCursor);

        first.Data.Select(document => document.Id).Should().Equal(expected.Take(10));
        second.Data.Select(document => document.Id).Should().Equal(expected.Skip(10).Take(10));
        rest.SelectMany(page => page.Data).Select(document => document.Id).Should().Equal(expected.Skip(20));
    }

    [TestMethod]
    public async Task CallerWhoSeesNothing_GetsAnEmptyPageWithoutCursor()
    {
        await using var context = PageOrderFixture.CreateContext();
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(
            FgaTestDataSeeder.UnauthorizedSubjectId,
            PageOrderFixture.ViewPermission);

        var page = await context.Documents.OrderBy(document => document.Rank).ToPageAsync(filter, 20);

        page.Data.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
        page.HasNextPage.Should().BeFalse();
    }

    [TestMethod]
    public async Task ExactlyAPageOfRows_HasACursor_ThenAnEmptyLastPage()
    {
        await using var context = PageOrderFixture.CreateContext();
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(
            PageOrderFixture.DirectSubjectId,
            PageOrderFixture.ViewPermission);
        var ordered = context.Documents.OrderBy(document => document.Rank);

        var first = await ordered.ToPageAsync(filter, 3);
        var second = await ordered.ToPageAsync(filter, 3, first.NextCursor);

        first.Data.Should().HaveCount(3);
        first.NextCursor.Should().NotBeNull();
        second.Data.Should().BeEmpty();
        second.NextCursor.Should().BeNull();
    }

    [TestMethod]
    public async Task StaleEntryListingARowUnderTwoOfTheCallersNodes_ShowsTheRowOnce()
    {
        await using var context = PageOrderFixture.CreateContext();
        var stale = await context.Documents.AsNoTracking().SingleAsync(document => document.ResourceId == "page_doc_001");
        var entries = TestDatabase.Qualify(PageOrderTestDbContext.RankEntries);
        string Column(string name) => TestDatabase.Quote(name);

        // As a write that bypassed EF could leave it: page_doc_001 also under page_doc_000, another of the caller's nodes.
        await PageOrderFixture.ExecuteAsync(
            $"INSERT INTO {entries} ({Column("NodeId")}, {Column("Rank")}, {Column("RowKey")}) VALUES ('page_doc_000', {stale.Rank}, '{stale.Id}')");
        try
        {
            var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission);

            var pages = await PageOrderFixture.WalkAsync(context.Documents.AsNoTracking().OrderBy(document => document.Rank), filter, 10);

            pages.SelectMany(page => page.Data).Select(document => document.ResourceId)
                .Should().Equal("page_doc_000", "page_doc_001", "page_doc_002");
        }
        finally
        {
            await PageOrderFixture.ExecuteAsync(
                $"DELETE FROM {entries} WHERE {Column("NodeId")} = 'page_doc_000' AND {Column("RowKey")} = '{stale.Id}'");
        }
    }

    [TestMethod]
    public async Task IncludeAndTracking_AreHonored()
    {
        await using var tracking = PageOrderFixture.CreateContext();
        var filter = await PageOrderFixture.AuthService(tracking).BuildFilterAsync<PagedDocument>(
            PageOrderFixture.DirectSubjectId,
            PageOrderFixture.ViewPermission);

        var tracked = await tracking.Documents.AsTracking().Include(document => document.Category)
            .OrderBy(document => document.Title)
            .ToPageAsync(filter, 10);

        tracked.Data.Should().HaveCount(3).And.OnlyContain(document => document.Category != null);
        tracking.ChangeTracker.Entries<PagedDocument>().Should().HaveCount(3);

        await using var untracked = PageOrderFixture.CreateContext();
        var untrackedFilter = await PageOrderFixture.AuthService(untracked).BuildFilterAsync<PagedDocument>(
            PageOrderFixture.DirectSubjectId,
            PageOrderFixture.ViewPermission);
        var page = await untracked.Documents.AsNoTracking().OrderBy(document => document.Title).ToPageAsync(untrackedFilter, 10);

        page.Data.Should().HaveCount(3).And.OnlyContain(document => document.Category == null);
        untracked.ChangeTracker.Entries<PagedDocument>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task UnsupportedQueries_AreRejectedBeforeAnyRead()
    {
        await using var context = PageOrderFixture.CreateContext();
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(
            FgaTestDataSeeder.SystemAdminSubjectId,
            PageOrderFixture.ViewPermission);

        await FluentActions.Awaiting(() => context.Documents.Select(document => document).OrderBy(document => document.Rank).ToPageAsync(filter, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Select is not supported before ToPageAsync*");
        await FluentActions.Awaiting(() => context.Documents.OrderBy(document => document.Rank).ThenByDescending(document => document.Title).ToPageAsync(filter, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*one direction*");

        await using var other = PageOrderFixture.CreateContext();
        await FluentActions.Awaiting(() => other.Documents.OrderBy(document => document.Rank).ToPageAsync(filter, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Build the filter and run the page on the same DbContext instance.*");
    }

    [TestMethod]
    public async Task HandRolledContext_IsRejected()
    {
        var authService = new SqlOS.Fga.Services.SqlOSFgaAuthService(
            AspireFixture.SharedContext,
            Microsoft.Extensions.Options.Options.Create(new SqlOS.Fga.Configuration.SqlOSFgaOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlOS.Fga.Services.SqlOSFgaAuthService>.Instance);
        var filter = await authService.BuildFilterAsync<LifecycleProtectedEntity>(
            FgaTestDataSeeder.SystemAdminSubjectId,
            PageOrderFixture.ViewPermission);

        var act = () => AspireFixture.SharedContext.Set<LifecycleProtectedEntity>().OrderBy(entity => entity.Id).ToPageAsync(filter, 10);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("ToPageAsync needs a context that derives from SqlOSDbContext<TContext>*");
    }

    [TestMethod]
    public async Task UndeclaredOrder_ReturnsTheRightPages_AndWarnsOnce()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs);
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(
            PageOrderFixture.DirectSubjectId,
            PageOrderFixture.ViewPermission);
        var ordered = context.Documents.AsNoTracking().OrderBy(document => document.CreatedAt);
        var expected = await ordered.ThenBy(document => document.Id).Where(filter).Select(document => document.Id).ToListAsync();

        var firstWalk = await PageOrderFixture.WalkAsync(ordered, filter, 2);
        var secondWalk = await PageOrderFixture.WalkAsync(ordered, filter, 2);

        AssertPages(firstWalk, expected, 2, "undeclared CreatedAt");
        AssertPages(secondWalk, expected, 2, "undeclared CreatedAt, again");
        logs.Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Category == "SqlOS.Fga")
            .Should().ContainSingle()
            .Which.Message.Should().StartWith("ToPageAsync scanned ")
            .And.Contain("of PagedDocument ordered by CreatedAt, Id asc to fill a page of 2")
            .And.Contain("Declare modelBuilder.Entity<PagedDocument>().HasPageOrder(e => e.CreatedAt) and add a migration.");
    }

    [TestMethod]
    public async Task EveryPage_LogsOneDebugLine()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs);
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedDocument>(
            FgaTestDataSeeder.AgencyMemberSubjectId,
            PageOrderFixture.ViewPermission);

        await context.Documents.OrderBy(document => document.Rank).ToPageAsync(filter, 5);

        logs.Entries.Where(entry => entry.Level == LogLevel.Debug && entry.Category == "SqlOS.Fga")
            .Should().ContainSingle()
            .Which.Message.Should().StartWith("ToPageAsync PagedDocument by Rank, Id asc (page order declared): ");
    }

    private static void AssertPages(
        IReadOnlyList<SqlOSCursorPage<PagedDocument>> pages,
        IReadOnlyList<Guid> expected,
        int pageSize,
        string because)
    {
        pages.SelectMany(page => page.Data).Select(document => document.Id)
            .Should().Equal(expected, because);
        for (var index = 0; index < pages.Count - 1; index++)
        {
            pages[index].Data.Should().HaveCount(pageSize, $"page {index} of {because} is not the last");
            pages[index].NextCursor.Should().NotBeNull(because);
        }

        pages[^1].NextCursor.Should().BeNull(because);
        pages[^1].Data.Count.Should().BeLessThanOrEqualTo(pageSize, because);
    }
}
