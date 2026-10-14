using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// What pages cost, from the engines' actual plans. A declared order: a caller who can see 3 rows of
/// a table whose other 5,000 rows sit first in the page's order gets their page from about a page of
/// work. A plain order: every caller reads at most a small multiple of the smaller of "rows read 1
/// needs" and "rows the caller can see", on both engines.
/// </summary>
[TestClass]
public class SqlOSFgaPageWorkIntegrationTests
{
    private const int PageSize = 20;

    // PagedEvents, by Sequence (no page order declares it): 100,000 rows, written with SQL and indexed
    // by the startup fill. The other agency's rows, with every 400th the mid caller's and 50,001 to
    // 50,003 the agency member's; then, at the far end of the order, the big far caller's 2,200 rows
    // (more keys than the 2,100 parameters SQL Server allows) and the far caller's last 300.
    private const int Events = 100_000;
    private const int FarRows = 300;
    private const int BigFarRows = 2_200;
    private const int FarStart = Events - FarRows;
    private const int BigFarStart = FarStart - BigFarRows;
    private const int MidEvery = 400;
    private const string MidResource = "page_work_mid";
    private const string FarResource = "page_work_far";
    private const string BigFarResource = "page_work_big_far";
    private const string MidSubject = "subj_page_work_mid";
    private const string FarSubject = "subj_page_work_far";
    private const string BigFarSubject = "subj_page_work_big_far";
    private const string MidAndFarSubject = "subj_page_work_mid_far";
    private static readonly int[] MemberSequences = [50_001, 50_002, 50_003];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;

    [TestInitialize]
    public async Task Seed()
    {
        await PageOrderFixture.EnsureSeededAsync();
        await Gate.WaitAsync();
        try
        {
            if (_seeded)
            {
                return;
            }

            await using var context = PageOrderFixture.CreateContext();
            if (!await context.WorkItems.AnyAsync())
            {
                // 5,000 rows the member cannot see come first in the order; the member's 3 rows come last.
                context.WorkItems.AddRange(Enumerable.Range(0, 5_000).Select(rank => new PagedWorkItem
                {
                    Id = Guid.NewGuid(),
                    ResourceId = FgaTestDataSeeder.OtherAgencyResourceId,
                    FolderResourceId = FgaTestDataSeeder.OtherAgencyResourceId,
                    Rank = rank
                }));
                context.WorkItems.AddRange(Enumerable.Range(5_000, 3).Select(rank => new PagedWorkItem
                {
                    Id = Guid.NewGuid(),
                    ResourceId = FgaTestDataSeeder.TestProjectResourceId,
                    FolderResourceId = FgaTestDataSeeder.TestProjectResourceId,
                    Rank = rank
                }));
                await context.SaveChangesAsync();
            }

            if (!await context.Events.AnyAsync())
            {
                await SeedEventsAsync(context);
            }

            _seeded = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task SeedEventsAsync(PageOrderTestDbContext context)
    {
        await context.CreateResourceWithIdAsync(MidResource, "team", "Mid-density events", "root");
        await context.CreateResourceWithIdAsync(FarResource, "team", "Far-end events", "root");
        await context.CreateResourceWithIdAsync(BigFarResource, "team", "Big far-end events", "root");
        foreach (var subject in new[] { MidSubject, FarSubject, BigFarSubject, MidAndFarSubject })
        {
            context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = subject, SubjectTypeId = "user", DisplayName = subject });
            context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = $"usr_{subject}", SubjectId = subject, IsActive = true });
        }

        await context.SaveChangesAsync();
        context.Set<SqlOSFgaGrant>().AddRange(
            Grant(MidSubject, MidResource),
            Grant(FarSubject, FarResource),
            Grant(BigFarSubject, BigFarResource),
            Grant(MidAndFarSubject, MidResource),
            Grant(MidAndFarSubject, FarResource));
        await context.SaveChangesAsync();

        var resourceOf = $"""
            CASE WHEN s >= {FarStart} THEN '{FarResource}'
                 WHEN s >= {BigFarStart} THEN '{BigFarResource}'
                 WHEN s % {MidEvery} = 0 THEN '{MidResource}'
                 WHEN s IN ({string.Join(", ", MemberSequences)}) THEN '{FgaTestDataSeeder.TestProjectResourceId}'
                 ELSE '{FgaTestDataSeeder.OtherAgencyResourceId}' END
            """;
        await PageOrderFixture.ExecuteAsync(TestDatabase.IsPostgreSql
            ? $"""INSERT INTO "PagedEvents" ("Id", "ResourceId", "Sequence") SELECT gen_random_uuid(), {resourceOf}, s FROM generate_series(0, {Events - 1}) AS s"""
            : $"""
              WITH n AS (SELECT TOP ({Events}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS s FROM sys.all_columns a CROSS JOIN sys.all_columns b)
              INSERT INTO [PagedEvents] ([Id], [ResourceId], [Sequence]) SELECT NEWID(), {resourceOf}, s FROM n
              """);

        // Rows written outside EF: the startup fill writes their node index, as after a bulk load.
        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, NullLogger.Instance, CancellationToken.None);
        var (actual, expected) = await PageOrderFixture.EntriesAndRebuildAsync(PageOrderTestDbContext.EventNodeIndex, "PagedEvents", sortColumn: null);
        actual.Should().Equal(expected);

        // The plans below are measured with statistics, as a database that has been running has them.
        var nodeIndex = TestDatabase.Qualify(PageOrderTestDbContext.EventNodeIndex);
        await PageOrderFixture.ExecuteAsync(TestDatabase.IsPostgreSql
            ? $"ANALYZE \"PagedEvents\"; ANALYZE {nodeIndex};"
            : $"UPDATE STATISTICS [PagedEvents]; UPDATE STATISTICS {nodeIndex};");

        static SqlOSFgaGrant Grant(string subject, string resource)
            => new() { Id = $"grant_{subject}_{resource}", SubjectId = subject, ResourceId = resource, RoleId = FgaTestDataSeeder.AgencyMemberRoleId };
    }

    [TestMethod]
    public async Task SparseCaller_ReadsAboutAPageOfEntries()
    {
        var capture = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(null, capture);

        var page = await context.WorkItems.AsNoTracking().OrderBy(item => item.Rank)
            .ToPageAsync(FgaTestDataSeeder.AgencyMemberSubjectId, PageOrderFixture.ViewPermission, PageSize);

        page.Data.Select(item => item.Rank).Should().Equal(5_000, 5_001, 5_002);
        page.NextCursor.Should().BeNull();
        var commands = Classify(capture.Commands);
        commands.Read1.Should().ContainSingle();
        TakeOf(commands.Read1[0]).Should().Be(PageSize);
        commands.Read2.Should().ContainSingle();
        commands.Load.Should().ContainSingle();
        capture.Commands.Should().HaveCount(5, "two statements resolve the caller and the permission, then the page runs three");

        (await EntryRowsReadAsync(commands.Read2[0], PageOrderTestDbContext.WorkItemEntries))
            .Should().BeLessThanOrEqualTo(PageSize);
    }

    [TestMethod]
    public async Task CallerWhoSeesEverything_IsServedByRead1Alone()
    {
        var capture = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(null, capture);

        var page = await context.WorkItems.AsNoTracking().OrderBy(item => item.Rank)
            .ToPageAsync(FgaTestDataSeeder.SystemAdminSubjectId, PageOrderFixture.ViewPermission, PageSize);

        page.Data.Select(item => item.Rank).Should().Equal(Enumerable.Range(0, PageSize));
        var commands = Classify(capture.Commands);
        commands.Read1.Should().ContainSingle();
        TakeOf(commands.Read1[0]).Should().Be(PageSize);
        commands.Read2.Should().BeEmpty();
        commands.Load.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ScopedQuery_IsServedByRead1Alone_ReadingAPage()
    {
        var capture = new CommandCapture();
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs, capture);

        var page = await context.WorkItems.AsNoTracking()
            .Where(item => item.FolderResourceId == FgaTestDataSeeder.TestProjectResourceId)
            .OrderBy(item => item.Rank)
            .ToPageAsync(FgaTestDataSeeder.SystemAdminSubjectId, PageOrderFixture.ViewPermission, 3);

        page.Data.Select(item => item.Rank).Should().Equal(5_000, 5_001, 5_002);
        var commands = Classify(capture.Commands);
        commands.Read1.Should().ContainSingle();
        TakeOf(commands.Read1[0]).Should().Be(3);
        commands.Read2.Should().BeEmpty();
        logs.Entries.Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
            .Should().ContainSingle()
            .Which.Message.Should().Contain("1 round(s); read 1 rows per round [3];");
    }

    [TestMethod]
    public async Task PlainOrder_SparseCaller_ReadsAboutItsOwnRowsPlusAPage()
    {
        var work = await PlainPageAsync(FgaTestDataSeeder.AgencyMemberSubjectId);

        work.Page.Data.Select(item => item.Sequence).Should().Equal(MemberSequences);
        work.Page.NextCursor.Should().BeNull();
        work.Source.Should().Be("read 3");
        work.Read1.Should().ContainSingle();
        work.Read3a.Should().ContainSingle();
        work.Read3b.Should().ContainSingle();
        work.Load.Should().ContainSingle();
        work.Rows.Should().BeLessThanOrEqualTo(2 * (PageSize + MemberSequences.Length));
    }

    [TestMethod]
    public async Task PlainOrder_CallerWhoSeesEverything_ReadsAboutAPage_WithoutRead3()
    {
        var work = await PlainPageAsync(FgaTestDataSeeder.SystemAdminSubjectId);

        work.Page.Data.Select(item => item.Sequence).Should().Equal(Enumerable.Range(0, PageSize));
        work.Source.Should().Be("read 1");
        work.Read1.Should().ContainSingle();
        work.Read3a.Should().BeEmpty();
        work.Read3b.Should().BeEmpty();
        work.Rows.Should().BeLessThanOrEqualTo(3 * PageSize);
    }

    [TestMethod]
    public async Task PlainOrder_MidDensityCaller_StaysWithinTheBound()
    {
        var (bound, smaller) = await BoundAsync(MidSubject);
        var work = await PlainPageAsync(MidSubject);

        work.Page.Data.Select(item => item.Sequence).Should().Equal(Enumerable.Range(0, PageSize).Select(index => index * MidEvery));
        work.Source.Should().Be("read 3");
        work.Read3a.Should().HaveCountGreaterThan(1, "the caller has more rows than the first budgets");
        work.Rows.Should().BeLessThanOrEqualTo(bound, $"min(rows read 1 needs, rows the caller can see) is {smaller}");
    }

    [TestMethod]
    public async Task PlainOrder_FarEndCaller_IsBoundedByItsOwnRows()
    {
        var (bound, smaller) = await BoundAsync(FarSubject);
        var work = await PlainPageAsync(FarSubject);

        smaller.Should().Be(FarRows, "the caller's rows sit after everything else in the order");
        work.Page.Data.Select(item => item.Sequence).Should().Equal(Enumerable.Range(FarStart, PageSize));
        work.Page.NextCursor.Should().NotBeNull();
        work.Source.Should().Be("read 3");
        work.Rows.Should().BeLessThanOrEqualTo(bound);
    }

    [TestMethod]
    public async Task PlainOrder_CallerWithMoreRowsThanSqlServerHasParameters_SendsTheKeysAsOneParameter()
    {
        var work = await PlainPageAsync(BigFarSubject);

        work.Page.Data.Select(item => item.Sequence).Should().Equal(Enumerable.Range(BigFarStart, PageSize));
        work.Source.Should().Be("read 3");
        var callerRows = work.Read3b.Should().ContainSingle().Subject;
        callerRows.Parameters.Should().HaveCountLessThan(10, $"the caller's {BigFarRows} keys are one parameter");
        callerRows.Text.Should().Contain(TestDatabase.IsPostgreSql ? "= ANY (" : "OPENJSON(");
    }

    [TestMethod]
    [DataRow(FarSubject, 3)]
    [DataRow(MidAndFarSubject, 4)]
    public async Task PlainOrder_EachRoundOfRead3_ReadsAtMostItsCapOfTheNodeIndex(string subjectId, int rounds)
    {
        // The far caller: budgets 20 and 80 are under its 300 rows; 320 holds them. Mid and far, two
        // nodes of 244 and 300 rows: at budget 320 neither node reaches the cap but both together
        // pass it, so a plan that read every node's entries before stopping would read past it.
        var work = await PlainPageAsync(subjectId);

        work.Read3a.Should().HaveCount(rounds);
        foreach (var keys in work.Read3a)
        {
            keys.Text.Should().Contain("ORDER BY (SELECT 1)").And.Contain("fn_PageNodes");
            (await RowsReadAsync(keys, PageOrderTestDbContext.EventNodeIndex)).Should().BeLessThanOrEqualTo(TakeOf(keys));
        }
    }

    [TestMethod]
    public async Task PlainOrder_LogsNoWarning()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs);

        await context.Events.AsNoTracking().OrderBy(item => item.Sequence)
            .ToPageAsync(BigFarSubject, PageOrderFixture.ViewPermission, PageSize);

        logs.Entries.Where(entry => entry.Level >= LogLevel.Warning && entry.EventId.Id != CoreEventId.SensitiveDataLoggingEnabledWarning.Id)
            .Should().BeEmpty("EF Core compiles the page's reads without warnings, so an application that throws on warnings can page");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlainOrder_CursorContinues_WhenConsecutivePagesAreServedByDifferentReads(bool descending)
    {
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs);
        var allowed = await PageOrderFixture.AllowedResourcesAsync(
            FarSubject,
            [FarResource, BigFarResource, MidResource, FgaTestDataSeeder.OtherAgencyResourceId, FgaTestDataSeeder.TestProjectResourceId]);
        allowed.Should().BeEquivalentTo([FarResource], "the oracle: the far caller sees only its resource's rows");
        var expected = Enumerable.Range(FarStart, FarRows).ToList();
        if (descending)
        {
            expected.Reverse();
        }

        var query = context.Events.AsNoTracking();
        var ordered = descending ? query.OrderByDescending(item => item.Sequence) : query.OrderBy(item => item.Sequence);
        var paged = new List<int>();
        string? cursor = null;
        do
        {
            var page = await ordered.ToPageAsync(FarSubject, PageOrderFixture.ViewPermission, PageSize, cursor);
            paged.AddRange(page.Data.Select(item => item.Sequence));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        paged.Should().Equal(expected);
        var sources = logs.Entries
            .Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
            .Select(entry => Regex.Match(entry.Message, "page from (read \\d);").Groups[1].Value)
            .ToList();
        if (descending)
        {
            // Dense from the far end; the last page looks past the caller's rows, which read 3 settles.
            sources[0].Should().Be("read 1");
            sources[^1].Should().Be("read 3");
        }
        else
        {
            // The first page is past 99,700 rows the caller cannot see; after it, its rows are dense.
            sources[0].Should().Be("read 3");
            sources[1].Should().Be("read 1");
        }
    }

    /// <summary>
    /// The stated bound for a caller of PagedEvents by Sequence: 9 times the smaller of "rows read 1
    /// needs" and "rows the caller can see", plus a few pages, from the CheckAccessAsync oracle.
    /// </summary>
    private static async Task<(long Bound, long Smaller)> BoundAsync(string subjectId)
    {
        await using var context = PageOrderFixture.CreateContext();
        var rows = await context.Events.AsNoTracking().OrderBy(item => item.Sequence).Select(item => item.ResourceId).ToListAsync();
        var allowed = await PageOrderFixture.AllowedResourcesAsync(subjectId, rows);
        var visible = rows.Count(allowed.Contains);
        var read1Needs = 0;
        var found = 0;
        while (read1Needs < rows.Count && found < PageSize)
        {
            found += allowed.Contains(rows[read1Needs++]) ? 1 : 0;
        }

        var smaller = Math.Min(read1Needs, visible);
        return (9L * smaller + 4L * PageSize, smaller);
    }

    private sealed record PlainWork(
        SqlOS.Pagination.SqlOSCursorPage<PagedEvent> Page,
        string Source,
        long Rows,
        IReadOnlyList<CapturedCommand> Read1,
        IReadOnlyList<CapturedCommand> Read3a,
        IReadOnlyList<CapturedCommand> Read3b,
        IReadOnlyList<CapturedCommand> Load);

    /// <summary>
    /// The first page of PagedEvents by Sequence for a caller, its statements by read, which read
    /// served it, and the rows of PagedEvents and of its node index the statements read.
    /// </summary>
    private static async Task<PlainWork> PlainPageAsync(string subjectId)
    {
        var capture = new CommandCapture();
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs, capture);

        var page = await context.Events.AsNoTracking().OrderBy(item => item.Sequence)
            .ToPageAsync(subjectId, PageOrderFixture.ViewPermission, PageSize);

        var events = TestDatabase.Quote("PagedEvents");
        var statements = capture.Commands
            .Where(command => command.Text.Contains(events, StringComparison.Ordinal)
                || command.Text.Contains(PageOrderTestDbContext.EventNodeIndex, StringComparison.Ordinal))
            .ToList();
        var read3a = statements.Where(command => command.Text.Contains(PageOrderTestDbContext.EventNodeIndex, StringComparison.Ordinal)).ToList();
        var checkedStatements = statements.Where(command => command.Text.Contains("fn_IsResourceAccessible", StringComparison.Ordinal)).ToList();
        var read1 = checkedStatements.Where(command => Regex.IsMatch(command.Text, @"TOP\(@\w+\)|LIMIT @\w+")).ToList();
        var load = checkedStatements.Except(read1).ToList();
        var read3b = statements.Except(read3a).Except(checkedStatements).ToList();

        var rows = 0L;
        foreach (var statement in statements)
        {
            rows += await RowsReadAsync(statement, "PagedEvents", PageOrderTestDbContext.EventNodeIndex);
        }

        var source = logs.Entries
            .Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
            .Select(entry => Regex.Match(entry.Message, "page from (read \\d);").Groups[1].Value)
            .Should().ContainSingle().Subject;
        return new PlainWork(page, source, rows, read1, read3a, read3b, load);
    }

    private static (List<CapturedCommand> Read1, List<CapturedCommand> Read2, List<CapturedCommand> Load) Classify(IEnumerable<CapturedCommand> commands)
    {
        var list = commands.ToList();
        var read2 = list.Where(command => command.Text.Contains("fn_PageNodes", StringComparison.Ordinal)).ToList();
        var read1 = list.Except(read2)
            .Where(command => command.Text.Contains("fn_IsResourceAccessible", StringComparison.Ordinal)
                && Regex.IsMatch(command.Text, @"TOP\(@\w+\)|LIMIT @\w+"))
            .ToList();
        var load = list.Except(read2).Except(read1)
            .Where(command => command.Text.Contains("fn_IsResourceAccessible", StringComparison.Ordinal))
            .ToList();
        return (read1, read2, load);
    }

    private static int TakeOf(CapturedCommand command)
    {
        var name = Regex.Match(command.Text, @"(?:TOP\((@\w+)\)|LIMIT (@\w+))").Groups.Values.Skip(1).First(group => group.Success).Value;
        var parameter = command.Parameters.Single(candidate => string.Equals(
            candidate.Name.TrimStart('@'), name.TrimStart('@'), StringComparison.Ordinal));
        return Convert.ToInt32(parameter.Value);
    }

    /// <summary>Re-runs a captured statement with the engine's actual plan and sums the rows read from a table.</summary>
    private static Task<long> EntryRowsReadAsync(CapturedCommand captured, string table)
        => RowsReadAsync(captured, table);

    /// <summary>Re-runs a captured statement with the engine's actual plan and sums the rows read from the tables.</summary>
    private static async Task<long> RowsReadAsync(CapturedCommand captured, params string[] tables)
    {
        await using var connection = TestDatabase.CreateConnection(AspireFixture.SqlConnectionString);
        await connection.OpenAsync();
        if (TestDatabase.IsPostgreSql)
        {
            await using var explain = connection.CreateCommand();
            explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + captured.Text;
            AddParameters(explain, captured);
            var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
            return SumPostgreSqlRows(plan.RootElement[0].GetProperty("Plan"), tables);
        }

        await using (var statistics = connection.CreateCommand())
        {
            statistics.CommandText = "SET STATISTICS XML ON";
            await statistics.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = captured.Text;
        AddParameters(command, captured);
        string? showplan = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
                while (await reader.ReadAsync())
                {
                    if (isPlan)
                    {
                        showplan = reader.GetString(0);
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        showplan.Should().NotBeNull("SET STATISTICS XML ON returns the actual plan");
        return SumSqlServerRows(XDocument.Parse(showplan!), tables);
    }

    private static void AddParameters(DbCommand command, CapturedCommand captured)
    {
        foreach (var parameter in captured.Parameters)
        {
            command.Parameters.Add((DbParameter)((ICloneable)parameter.Original).Clone());
        }
    }

    private static long SumSqlServerRows(XDocument plan, string[] tables)
    {
        XNamespace showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var quoted = tables.Select(table => $"[{table}]").ToHashSet(StringComparer.Ordinal);
        return plan.Descendants(showplan + "RelOp")
            .Where(relOp => relOp.Elements()
                .Any(@operator => @operator.Elements(showplan + "Object")
                    .Any(target => quoted.Contains((string?)target.Attribute("Table") ?? string.Empty))))
            .SelectMany(relOp => relOp.Elements(showplan + "RunTimeInformation").Elements(showplan + "RunTimeCountersPerThread"))
            .Sum(counter => (long?)counter.Attribute("ActualRows") ?? 0);
    }

    private static long SumPostgreSqlRows(JsonElement node, string[] tables)
    {
        var rows = 0L;
        if (node.TryGetProperty("Relation Name", out var relation) && tables.Contains(relation.GetString()))
        {
            // A scan's "Actual Rows" are the rows that passed its filter; the rows it removed were read too.
            var read = node.GetProperty("Actual Rows").GetDouble()
                + (node.TryGetProperty("Rows Removed by Filter", out var filtered) ? filtered.GetDouble() : 0)
                + (node.TryGetProperty("Rows Removed by Index Recheck", out var rechecked) ? rechecked.GetDouble() : 0);
            rows += (long)Math.Round(read * node.GetProperty("Actual Loops").GetDouble());
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                rows += SumPostgreSqlRows(child, tables);
            }
        }

        return rows;
    }
}
