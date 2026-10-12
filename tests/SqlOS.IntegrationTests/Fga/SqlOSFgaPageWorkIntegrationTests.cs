using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Extensions;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// The point of page orders: a caller who can see 3 rows of a table whose other 5,000 rows sit
/// first in the page's order gets their page from about a page of work, on both engines.
/// </summary>
[TestClass]
public class SqlOSFgaPageWorkIntegrationTests
{
    private const int PageSize = 20;
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

            _seeded = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    [TestMethod]
    public async Task SparseCaller_ReadsAboutAPageOfEntries()
    {
        var capture = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(null, capture);
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedWorkItem>(
            FgaTestDataSeeder.AgencyMemberSubjectId,
            PageOrderFixture.ViewPermission);
        capture.Clear();

        var page = await context.WorkItems.AsNoTracking().OrderBy(item => item.Rank).ToPageAsync(filter, PageSize);

        page.Data.Select(item => item.Rank).Should().Equal(5_000, 5_001, 5_002);
        page.NextCursor.Should().BeNull();
        var commands = Classify(capture.Commands);
        commands.Read1.Should().ContainSingle();
        TakeOf(commands.Read1[0]).Should().Be(PageSize);
        commands.Read2.Should().ContainSingle();
        commands.Load.Should().ContainSingle();
        capture.Commands.Should().HaveCount(3);

        (await EntryRowsReadAsync(commands.Read2[0], PageOrderTestDbContext.WorkItemEntries))
            .Should().BeLessThanOrEqualTo(PageSize);
    }

    [TestMethod]
    public async Task CallerWhoSeesEverything_IsServedByRead1Alone()
    {
        var capture = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(null, capture);
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedWorkItem>(
            FgaTestDataSeeder.SystemAdminSubjectId,
            PageOrderFixture.ViewPermission);
        capture.Clear();

        var page = await context.WorkItems.AsNoTracking().OrderBy(item => item.Rank).ToPageAsync(filter, PageSize);

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
        var filter = await PageOrderFixture.AuthService(context).BuildFilterAsync<PagedWorkItem>(
            FgaTestDataSeeder.SystemAdminSubjectId,
            PageOrderFixture.ViewPermission);
        capture.Clear();
        logs.Clear();

        var page = await context.WorkItems.AsNoTracking()
            .Where(item => item.FolderResourceId == FgaTestDataSeeder.TestProjectResourceId)
            .OrderBy(item => item.Rank)
            .ToPageAsync(filter, 3);

        page.Data.Select(item => item.Rank).Should().Equal(5_000, 5_001, 5_002);
        var commands = Classify(capture.Commands);
        commands.Read1.Should().ContainSingle();
        TakeOf(commands.Read1[0]).Should().Be(3);
        commands.Read2.Should().BeEmpty();
        logs.Entries.Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
            .Should().ContainSingle()
            .Which.Message.Should().Contain("1 round(s); read 1 rows per round [3];");
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
    private static async Task<long> EntryRowsReadAsync(CapturedCommand captured, string table)
    {
        await using var connection = TestDatabase.CreateConnection(AspireFixture.SqlConnectionString);
        await connection.OpenAsync();
        if (TestDatabase.IsPostgreSql)
        {
            await using var explain = connection.CreateCommand();
            explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + captured.Text;
            AddParameters(explain, captured);
            var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
            return SumPostgreSqlRows(plan.RootElement[0].GetProperty("Plan"), table);
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
        return SumSqlServerRows(XDocument.Parse(showplan!), table);
    }

    private static void AddParameters(DbCommand command, CapturedCommand captured)
    {
        foreach (var parameter in captured.Parameters)
        {
            command.Parameters.Add((DbParameter)((ICloneable)parameter.Original).Clone());
        }
    }

    private static long SumSqlServerRows(XDocument plan, string table)
    {
        XNamespace showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var quoted = $"[{table}]";
        return plan.Descendants(showplan + "RelOp")
            .Where(relOp => relOp.Elements()
                .Any(@operator => @operator.Elements(showplan + "Object")
                    .Any(target => string.Equals((string?)target.Attribute("Table"), quoted, StringComparison.Ordinal))))
            .SelectMany(relOp => relOp.Elements(showplan + "RunTimeInformation").Elements(showplan + "RunTimeCountersPerThread"))
            .Sum(counter => (long?)counter.Attribute("ActualRows") ?? 0);
    }

    private static long SumPostgreSqlRows(JsonElement node, string table)
    {
        var rows = 0L;
        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == table)
        {
            rows += (long)(node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble());
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                rows += SumPostgreSqlRows(child, table);
            }
        }

        return rows;
    }
}
