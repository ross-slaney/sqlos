using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Extensions;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Pagination;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// ToPageAsync returns, page by page, exactly the rows <c>CheckAccessAsync</c> allows one by one, in
/// the query's order, for every caller, order, direction, page size and application filter.
/// </summary>
[TestClass]
public class SqlOSFgaPageOrderIntegrationTests
{
    [TestInitialize]
    public Task Seed() => PageOrderFixture.EnsureSeededAsync();

    [TestMethod]
    public async Task WalkingEveryPage_EqualsThePointChecks_ForEveryCallerOrderDirectionAndPageSize()
    {
        foreach (var caller in PageOrderFixture.Callers)
        {
            await using var context = PageOrderFixture.CreateContext();
            var documents = await context.Documents.AsNoTracking().ToListAsync();
            var allowed = await PageOrderFixture.AllowedResourcesAsync(caller, documents.Select(document => document.ResourceId));
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

                        var expected = await PageOrderFixture.ExpectedAsync(query, order, descending, caller, allowed);
                        foreach (var pageSize in new[] { 1, 7, 100 })
                        {
                            var pages = await PageOrderFixture.WalkAsync(PageOrderFixture.Order(query, order, descending), caller, pageSize);

                            AssertPages(pages, expected, pageSize, $"{caller} {order} {(descending ? "desc" : "asc")} filter={withApplicationFilter} size={pageSize}");
                        }
                    }
                }
            }
        }
    }

    [TestMethod]
    public async Task GroupMember_SeesTheGroupsRows_AsThePointChecksDo()
    {
        await using var context = PageOrderFixture.CreateContext();
        var query = context.Documents.AsNoTracking();
        var expected = await PageOrderFixture.ExpectedAsync(query, "Rank", false, FgaTestDataSeeder.GroupMemberSubjectId);

        var pages = await PageOrderFixture.WalkAsync(query.OrderBy(document => document.Rank), FgaTestDataSeeder.GroupMemberSubjectId, 7);

        expected.Should().NotBeEmpty("the member's group holds grants in the seeded tree");
        AssertPages(pages, expected, 7, "group member");
    }

    [TestMethod]
    public async Task CursorFromTheMiddle_ContinuesWhereThePageEnded()
    {
        await using var context = PageOrderFixture.CreateContext();
        var caller = FgaTestDataSeeder.AgencyAdminSubjectId;
        var ordered = context.Documents.AsNoTracking().OrderByDescending(document => document.Title);
        var expected = await PageOrderFixture.ExpectedAsync(context.Documents, "Title", true, caller);

        var first = await ordered.ToPageAsync(caller, PageOrderFixture.ViewPermission, 10);
        var second = await ordered.ToPageAsync(caller, PageOrderFixture.ViewPermission, 10, first.NextCursor);
        var rest = await PageOrderFixture.WalkAsync(ordered, caller, 25, second.NextCursor);

        first.Data.Select(document => document.Id).Should().Equal(expected.Take(10));
        second.Data.Select(document => document.Id).Should().Equal(expected.Skip(10).Take(10));
        rest.SelectMany(page => page.Data).Select(document => document.Id).Should().Equal(expected.Skip(20));
    }

    [TestMethod]
    public async Task CallerWhoSeesNothing_GetsAnEmptyPageWithoutCursor()
    {
        await using var context = PageOrderFixture.CreateContext();

        var page = await context.Documents.OrderBy(document => document.Rank)
            .ToPageAsync(FgaTestDataSeeder.UnauthorizedSubjectId, PageOrderFixture.ViewPermission, PageOrderFixture.MaxPageSize);

        page.Data.Should().BeEmpty();
        page.PageSize.Should().Be(PageOrderFixture.MaxPageSize);
        page.NextCursor.Should().BeNull();
        page.HasNextPage.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("subj_page_nobody", PageOrderFixture.ViewPermission, "No subjects found for subj_page_nobody")]
    [DataRow(FgaTestDataSeeder.SystemAdminSubjectId, "NO_SUCH_PERMISSION", "Permission NO_SUCH_PERMISSION not found")]
    public async Task UnknownSubjectOrPermission_GetsAnEmptyPage_WithoutAPageStatement(string subjectId, string permissionKey, string warning)
    {
        var logs = new CapturingLoggerFactory();
        var capture = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(logs, capture);

        var page = await context.Documents.OrderBy(document => document.Rank).ToPageAsync(subjectId, permissionKey, 20);

        page.Data.Should().BeEmpty();
        page.NextCursor.Should().BeNull();
        capture.Commands.Should().NotBeEmpty("the subject and permission are looked up")
            .And.OnlyContain(command => !command.Text.Contains("PagedDocuments", StringComparison.Ordinal)
                && !command.Text.Contains("fn_", StringComparison.Ordinal)
                && !command.Text.Contains("SqlOSFgaPage_", StringComparison.Ordinal));
        logs.Entries.Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Warning)
            .Should().ContainSingle().Which.Message.Should().Be(warning);
    }

    [TestMethod]
    public async Task ExactlyAPageOfRows_HasACursor_ThenAnEmptyLastPage()
    {
        await using var context = PageOrderFixture.CreateContext();
        var ordered = context.Documents.OrderBy(document => document.Rank);

        var first = await ordered.ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 3);
        var second = await ordered.ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 3, first.NextCursor);

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
            var pages = await PageOrderFixture.WalkAsync(context.Documents.AsNoTracking().OrderBy(document => document.Rank), PageOrderFixture.DirectSubjectId, 10);

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

        var tracked = await tracking.Documents.AsTracking().Include(document => document.Category)
            .OrderBy(document => document.Title)
            .ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 10);

        tracked.Data.Should().HaveCount(3).And.OnlyContain(document => document.Category != null);
        tracking.ChangeTracker.Entries<PagedDocument>().Should().HaveCount(3);

        await using var untracked = PageOrderFixture.CreateContext();
        var page = await untracked.Documents.AsNoTracking().OrderBy(document => document.Title)
            .ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 10);

        page.Data.Should().HaveCount(3).And.OnlyContain(document => document.Category == null);
        untracked.ChangeTracker.Entries<PagedDocument>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task UnsupportedQueries_AreRejectedBeforeAnyRead()
    {
        await using var context = PageOrderFixture.CreateContext();
        var admin = FgaTestDataSeeder.SystemAdminSubjectId;

        await FluentActions.Awaiting(() => context.Documents.Select(document => document).OrderBy(document => document.Rank).ToPageAsync(admin, PageOrderFixture.ViewPermission, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Select is not supported before ToPageAsync*");
        await FluentActions.Awaiting(() => context.Documents.OrderBy(document => document.Rank).ThenByDescending(document => document.Title).ToPageAsync(admin, PageOrderFixture.ViewPermission, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*one direction*");
    }

    [TestMethod]
    public async Task QueryNotFromASqlOSDbContext_IsRejected()
    {
        const string message = "ToPageAsync needs a query that starts from a DbSet of a context deriving from SqlOSDbContext<TContext>*";
        var admin = FgaTestDataSeeder.SystemAdminSubjectId;

        await FluentActions.Awaiting(() => AspireFixture.SharedContext.Set<LifecycleProtectedEntity>().OrderBy(entity => entity.Id).ToPageAsync(admin, PageOrderFixture.ViewPermission, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage(message);
        await FluentActions.Awaiting(() => new List<PagedDocument>().AsQueryable().OrderBy(document => document.Rank).ToPageAsync(admin, PageOrderFixture.ViewPermission, 10))
            .Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [TestMethod]
    public async Task PooledContext_IsFound_AcrossLeases()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<PageOrderTestDbContext>(options => options.UseTestProvider(AspireFixture.SqlConnectionString), poolSize: 1);
        await using var provider = services.BuildServiceProvider();
        await using var reference = PageOrderFixture.CreateContext();
        var expected = await PageOrderFixture.ExpectedAsync(reference.Documents, "Rank", false, FgaTestDataSeeder.AgencyMemberSubjectId);

        var leased = new List<PageOrderTestDbContext>();
        for (var lease = 0; lease < 3; lease++)
        {
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<PageOrderTestDbContext>();
            leased.Add(context);

            var pages = await PageOrderFixture.WalkAsync(context.Documents.AsNoTracking().OrderBy(document => document.Rank), FgaTestDataSeeder.AgencyMemberSubjectId, 20);

            pages.SelectMany(page => page.Data).Select(document => document.Id).Should().Equal(expected, $"lease {lease}");
        }

        leased.Distinct().Should().ContainSingle("the pool hands out the same context on every lease");
    }

    [TestMethod]
    public async Task TwoContextsOfOneType_AndOneFromDI_EachRunsItsOwnPage()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PageOrderTestDbContext>(options => options.UseTestProvider(AspireFixture.SqlConnectionString));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var resolved = scope.ServiceProvider.GetRequiredService<PageOrderTestDbContext>();
        var firstCapture = new CommandCapture();
        var secondCapture = new CommandCapture();
        await using var first = PageOrderFixture.CreateContext(null, firstCapture);
        await using var second = PageOrderFixture.CreateContext(null, secondCapture);
        var firstQuery = first.Documents.AsTracking().OrderBy(document => document.Rank);
        var secondQuery = second.Documents.AsTracking().OrderBy(document => document.Rank);

        var secondPage = await secondQuery.ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 10);
        var resolvedPage = await resolved.Documents.AsTracking().OrderBy(document => document.Rank)
            .ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 10);

        secondPage.Data.Should().HaveCount(3);
        resolvedPage.Data.Select(document => document.Id).Should().Equal(secondPage.Data.Select(document => document.Id));
        secondCapture.Commands.Should().Contain(command => command.Text.Contains("SqlOSFgaSubjects", StringComparison.Ordinal),
            "the caller is resolved on the context the query came from");
        firstCapture.Commands.Should().BeEmpty("the other context of the same type ran nothing");
        second.ChangeTracker.Entries<PagedDocument>().Should().HaveCount(3);
        first.ChangeTracker.Entries<PagedDocument>().Should().BeEmpty();
        resolved.ChangeTracker.Entries<PagedDocument>().Should().HaveCount(3);

        (await firstQuery.ToPageAsync(PageOrderFixture.DirectSubjectId, PageOrderFixture.ViewPermission, 10)).Data.Should().HaveCount(3);
        firstCapture.Commands.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task PageOnAnotherContext_ReusesTheCompiledQueries()
    {
        // The same options, so both contexts share EF Core's internal services and its query cache.
        var logs = new CapturingLoggerFactory();
        await using (var warm = PageOrderFixture.CreateContext(logs))
        {
            await warm.Documents.AsNoTracking().OrderBy(document => document.Rank)
                .ToPageAsync(FgaTestDataSeeder.AgencyMemberSubjectId, PageOrderFixture.ViewPermission, 5);
        }

        logs.Clear();
        await using var context = PageOrderFixture.CreateContext(logs);

        await context.Documents.AsNoTracking().OrderBy(document => document.Rank)
            .ToPageAsync(FgaTestDataSeeder.AgencyMemberSubjectId, PageOrderFixture.ViewPermission, 5);

        logs.Entries.Where(entry => entry.EventId.Id == CoreEventId.QueryCompilationStarting.Id)
            .Should().BeEmpty("the page's queries are cached across context instances");
    }

    [TestMethod]
    public async Task UndeclaredOrder_ReturnsTheRightPages_AndWarnsOnce()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs);
        var ordered = context.Documents.AsNoTracking().OrderBy(document => document.CreatedAt);
        var expected = await PageOrderFixture.ExpectedAsync(context.Documents, "CreatedAt", false, PageOrderFixture.DirectSubjectId);

        var firstWalk = await PageOrderFixture.WalkAsync(ordered, PageOrderFixture.DirectSubjectId, 2);
        var secondWalk = await PageOrderFixture.WalkAsync(ordered, PageOrderFixture.DirectSubjectId, 2);

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
        await context.Documents.OrderBy(document => document.Rank)
            .ToPageAsync(FgaTestDataSeeder.AgencyMemberSubjectId, PageOrderFixture.ViewPermission, 5);

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
