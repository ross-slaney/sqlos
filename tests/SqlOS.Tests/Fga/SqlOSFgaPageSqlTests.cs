using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The SQL behind a page call, on both engines: the grant counts and their routines, the direct index of an
/// application table with its rebuild and the grants triggers that keep both exact, the prelude, and the two
/// statements of a round (nodes opened, then every stream's rows merged in the page's order).
/// </summary>
[TestClass]
public class SqlOSFgaPageSqlTests
{
    private static readonly SqlOSFgaScopeTable Items = new(
        "app",
        "Items",
        "ResourceId",
        ["Id"],
        [new SqlOSFgaScopeOrder("Price", ["Price", "Id"])],
        [new SqlOSFgaScopeColumn("Id", "int", false), new SqlOSFgaScopeColumn("Price", "decimal(10,2)", false), new SqlOSFgaScopeColumn("Status", "int", false)]);

    [TestMethod]
    public void Names_AreOneDefinitionForBothEngines()
    {
        SqlOSFgaPageIndex.CountsTable.Should().Be("SqlOSFgaGrantCounts");
        SqlOSFgaPageIndex.DirectTable(Items).Should().Be("SqlOSFgaDirect_app_Items");
        SqlOSFgaPageIndex.DirectTable(Items with { Schema = null }).Should().Be("SqlOSFgaDirect_Items");
        SqlOSFgaPageIndex.DirectRebuildRoutine(Items).Should().Be("SqlOSFgaDirect_app_Items_Rebuild");
        SqlOSFgaPageIndex.DirectIndexNames(Items).Should().Equal("IX_SqlOSFgaDirect_app_Items_Price");
        SqlOSFgaPageIndex.GrantTriggerNames("SqlOSFgaGrants").Should().Equal("TR_SqlOSFgaGrants_SqlOSFgaPage_Insert", "TR_SqlOSFgaGrants_SqlOSFgaPage_Update", "TR_SqlOSFgaGrants_SqlOSFgaPage_Delete");

        // The direct index carries the key and every declared order's columns, each once, never a filter column.
        SqlOSFgaPageIndex.DirectColumns(Items).Select(c => c.Column).Should().Equal("Id", "Price");
        var untyped = () => SqlOSFgaPageIndex.DirectColumns(Items with { Columns = [] });
        untyped.Should().Throw<InvalidOperationException>().WithMessage("*Id*Items*");
    }

    [TestMethod]
    public void SqlServer_PageIndexSql_CreatesTheCountsRoutinesTheDirectIndexAndTheGrantTriggers()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 };
        var all = string.Join("\n", SqlServerDatabaseProvider.Instance.BuildPageIndexSql(options, [Items]));

        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaGrantCounts_Rebuild]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaGrantCounts_Adjust]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaGrantCounts_Refresh]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaPageIndex_Rebuild]");
        all.Should().Contain("[dbo].[SqlOSFgaDirect_app_Items]");
        all.Should().Contain("CREATE OR ALTER PROCEDURE [dbo].[sp_SqlOSFgaDirect_app_Items_Rebuild]");
        all.Should().Contain("[IX_SqlOSFgaDirect_app_Items_Price]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaGrants_SqlOSFgaPage_Insert] ON [dbo].[SqlOSFgaGrants]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaGrants_SqlOSFgaPage_Update] ON [dbo].[SqlOSFgaGrants]");
        all.Should().Contain("CREATE OR ALTER TRIGGER [dbo].[TR_SqlOSFgaGrants_SqlOSFgaPage_Delete] ON [dbo].[SqlOSFgaGrants]");
        all.Should().Contain("AND EXISTS (SELECT 1 FROM [dbo].[SqlOSFgaResources] ch WHERE ch.ParentId = r.Id)", "only grants on resources with children live in the counts; a childless resource's grants are the direct index's");
        all.Should().NotContain("[Status]", "a filter column is not part of the direct index");

        SqlServerDatabaseProvider.Instance.BuildPageIndexRebuildSql(options).Should().Contain("[dbo].[sp_SqlOSFgaPageIndex_Rebuild]");
        var refresh = SqlServerDatabaseProvider.Instance.BuildCountsRefreshSql(options);
        refresh.Should().Contain("[dbo].[sp_SqlOSFgaGrantCounts_Refresh]").And.Contain("@From").And.Contain("@To");
        var boundary = SqlServerDatabaseProvider.Instance.BuildNextValidityBoundarySql(options);
        boundary.Should().Contain("@Now").And.Contain("EffectiveFrom").And.Contain("EffectiveTo");
    }

    [TestMethod]
    public void PostgreSql_PageIndexSql_CreatesTheCountsRoutinesTheDirectIndexAndTheGrantTriggers()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 };
        var all = string.Join("\n", PostgreSqlDatabaseProvider.Instance.BuildPageIndexSql(options, [Items]));

        all.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_SqlOSFgaGrantCounts_Rebuild\"");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_SqlOSFgaGrantCounts_Adjust\"");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_SqlOSFgaGrantCounts_Refresh\"");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_SqlOSFgaPageIndex_Rebuild\"");
        all.Should().Contain("\"dbo\".\"SqlOSFgaDirect_app_Items\"");
        all.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_SqlOSFgaDirect_app_Items_Rebuild\"");
        all.Should().Contain("\"IX_SqlOSFgaDirect_app_Items_Price\"");

        // The row index answers the triggers' equality lookups only: a B-tree on the key alone would also
        // answer a page's ORDER BY past its position, with the principal as a filter over the whole index.
        all.Should().Contain("CREATE INDEX IF NOT EXISTS \"IX_SqlOSFgaDirect_app_Items_Row\" ON \"dbo\".\"SqlOSFgaDirect_app_Items\" USING hash (\"Id\")");
        all.Should().Contain("am.amname <> 'hash'", "an existing B-tree row index is replaced on the next start");
        all.Should().Contain("\"fn_SqlOSFgaGrants_PageOnInsert\"");
        all.Should().Contain("\"TR_SqlOSFgaGrants_SqlOSFgaPage_Insert\"");
        all.Should().Contain("\"TR_SqlOSFgaGrants_SqlOSFgaPage_Delete\"");
        all.Should().Contain("REFERENCING NEW TABLE AS new_rows", "the grants triggers are statement triggers over transition tables");
        all.Should().Contain("AND EXISTS (SELECT 1 FROM \"dbo\".\"SqlOSFgaResources\" ch WHERE ch.\"ParentId\" = r.\"Id\")", "only grants on resources with children live in the counts; a childless resource's grants are the direct index's");
        all.Should().NotContain("TEMP TABLE", "a statement trigger may run these thousands of times in one transaction");
        all.Should().NotContain("\"Status\"", "a filter column is not part of the direct index");

        PostgreSqlDatabaseProvider.Instance.BuildPageIndexRebuildSql(options).Should().Contain("fn_SqlOSFgaPageIndex_Rebuild");
        var refresh = PostgreSqlDatabaseProvider.Instance.BuildCountsRefreshSql(options);
        refresh.Should().Contain("fn_SqlOSFgaGrantCounts_Refresh").And.Contain("@From").And.Contain("@To");
        var boundary = PostgreSqlDatabaseProvider.Instance.BuildNextValidityBoundarySql(options);
        boundary.Should().Contain("@Now").And.Contain("EffectiveFrom").And.Contain("EffectiveTo");
    }

    [TestMethod]
    public void SqlServer_RoundSql_OpensNodesThenMergesEveryStreamInPageOrder()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 };
        var spec = new SqlOSFgaPageSpec(Items, "i", [Items.Columns[1], Items.Columns[0]], "Price", "[i].[Status] = @__status_0", Typed: true);

        var prelude = SqlServerDatabaseProvider.Instance.BuildPagePreludeSql(options);
        prelude.Should().Contain("fn_ActiveSubjects").And.Contain("@SubjectIds").And.Contain("@PermissionId").And.Contain("@RootId");
        prelude.Should().Contain("c.ValidUntil <= GETUTCDATE()", "the prelude names the live principals whose counts fell behind the clock");
        SqlServerDatabaseProvider.Instance.BuildCountsRebuildSql(options).Should().Be("EXEC [dbo].[sp_SqlOSFgaGrantCounts_Rebuild] @Subjects = @Subjects;");
        var routines = string.Join("\n", SqlServerDatabaseProvider.Instance.BuildPageIndexSql(options, [Items]));
        routines.Should().Contain("ValidUntil = NULL WHERE ResourceSeq = @Root", "a rebuild recomputes each principal's next window boundary")
            .And.Contain("AND c.ValidUntil IS NULL", "a root row holding a pending boundary survives the cleanup");

        var round = SqlServerDatabaseProvider.Instance.BuildPageRoundSql(options, spec);
        round.Should().Contain("OPENJSON(@Opens)").And.Contain("OPENJSON(@Streams)");
        round.Should().Contain("[dbo].[SqlOSFgaGrantCounts]");
        round.Should().Contain("fetch_n");
        round.Should().Contain("[i].[Status] = @__status_0", "the application's filter runs inside every seek");
        round.Should().Contain("[dbo].[SqlOSFgaDirect_app_Items]");
        round.Should().Contain("TOP (s.f)");
        for (var level = 0; level <= 2; level++)
        {
            round.Should().Contain($"s.[level] = {level}");

            // Every stream seeks the level's mirror of the page's order, by hint: the optimizer must not read
            // the table in key order and filter the level, which costs rows in proportion to the table.
            round.Should().Contain($"[app].[Items] AS i WITH (FORCESEEK ([IX_Items_FgaScope{level}_Price] ([FgaScope{level}])))");
        }

        round.Should().Contain("WITH (FORCESEEK ([IX_SqlOSFgaDirect_app_Items_Price] ([SubjectId], [RoleId])))");
        round.Should().NotContain("s.[level] = 3");
        round.Should().Contain("ORDER BY c0, c1");

        // The rows of earlier rounds the executor holds come back typed by the order columns and are merged by
        // the same ORDER BY as the round's own rows: the database orders every row of the page, never .NET.
        round.Should().Contain("FROM OPENJSON(@Retained) WITH (kind INT '$.kind', [level] INT '$.level', seq BIGINT '$.seq', principal NVARCHAR(450) '$.principal', role NVARCHAR(450) '$.role', rn BIGINT '$.rn', cnt INT '$.cnt', granted BIT '$.granted', c0 decimal(10,2) '$.c0', c1 int '$.c1') r");
        round.IndexOf("OPENJSON(@Retained)", StringComparison.Ordinal).Should().BeLessThan(round.LastIndexOf("ORDER BY c0, c1", StringComparison.Ordinal), "the held rows are part of the merge");

        // A positioned stream's keyset is a range the index serves, never behind an OR on the position's presence.
        round.Should().Contain("AND s.has_after = 1").And.Contain("AND s.has_after = 0").And.NotContain("has_after = 0 OR");
        round.Should().Contain("(i.[Price] >= s.a0 AND (i.[Price] > s.a0 OR i.[Id] > s.a1))");
    }

    [TestMethod]
    public void PostgreSql_RoundSql_OpensNodesThenMergesEveryStreamInPageOrder()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 };
        var spec = new SqlOSFgaPageSpec(Items, "i", [Items.Columns[1], Items.Columns[0]], "Price", "i.\"Status\" = @__status_0", Typed: true);

        var prelude = PostgreSqlDatabaseProvider.Instance.BuildPagePreludeSql(options);
        prelude.Should().Contain("\"fn_ActiveSubjects\"(@SubjectIds)").And.Contain("@PermissionId").And.Contain("@RootId");
        prelude.Should().Contain("c.\"ValidUntil\" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')", "the prelude names the live principals whose counts fell behind the clock");
        PostgreSqlDatabaseProvider.Instance.BuildCountsRebuildSql(options).Should().Contain("\"fn_SqlOSFgaGrantCounts_Rebuild\"(ARRAY(").And.Contain("@Subjects::jsonb");
        var routines = string.Join("\n", PostgreSqlDatabaseProvider.Instance.BuildPageIndexSql(options, [Items]));
        routines.Should().Contain("\"ValidUntil\" = LEAST(", "an arriving grant brings its principal's next boundary forward")
            .And.Contain("AND c.\"ValidUntil\" IS NULL", "a root row holding a pending boundary survives the cleanup");

        var round = PostgreSqlDatabaseProvider.Instance.BuildPageRoundSql(options, spec);
        round.Should().Contain("jsonb_to_recordset(@Opens::jsonb)").And.Contain("jsonb_to_recordset(@Streams::jsonb)");
        round.Should().Contain("\"dbo\".\"SqlOSFgaGrantCounts\"");
        round.Should().Contain("fetch_n");
        round.Should().Contain("i.\"Status\" = @__status_0", "the application's filter runs inside every seek");
        round.Should().Contain("\"dbo\".\"SqlOSFgaDirect_app_Items\"");
        round.Should().Contain("LIMIT s.f");
        for (var level = 0; level <= 2; level++)
        {
            round.Should().Contain($"SUBSTRING(i.\"FgaScope\", {SqlOSFgaPageIndex.Offset(level)}, 8) = int8send(s.seq)");
            round.Should().Contain($"s.level = {level}");
        }

        round.Should().NotContain("s.level = 3");
        round.Should().Contain("ORDER BY c0, c1");
        round.Should().Contain("FROM jsonb_to_recordset(@Retained::jsonb) AS r(kind int, level int, seq bigint, principal varchar(450), role varchar(450), rn bigint, cnt bigint, granted boolean, c0 decimal(10,2), c1 int)");
        round.IndexOf("@Retained::jsonb", StringComparison.Ordinal).Should().BeLessThan(round.LastIndexOf("ORDER BY c0, c1", StringComparison.Ordinal), "the held rows are part of the merge");

        // A positioned stream's keyset is a range the index serves, never behind an OR on the position's presence.
        round.Should().Contain("AND s.has_after").And.Contain("AND NOT s.has_after").And.NotContain("NOT s.has_after OR");
        round.Should().Contain("(i.\"Price\" >= s.a0 AND (i.\"Price\" > s.a0 OR i.\"Id\" > s.a1))");

        round.Should().Contain("ORDER BY i.\"Price\", i.\"Id\"").And.Contain("ORDER BY d.\"Price\", d.\"Id\"");
    }

    [TestMethod]
    public void RoundSql_Descending_ReadsTheSameIndexesBackwards()
    {
        var options = new SqlOSFgaOptions { MaxResourceHierarchyDepth = 2 };
        var spec = new SqlOSFgaPageSpec(Items, "i", [Items.Columns[1], Items.Columns[0]], "Price", null, Typed: false, Descending: true);

        var sqlServer = SqlServerDatabaseProvider.Instance.BuildPageRoundSql(options, spec);
        sqlServer.Should().Contain("ORDER BY c0 DESC, c1 DESC", "the merge runs backwards");
        sqlServer.Should().Contain("ORDER BY i.[Price] DESC, i.[Id] DESC", "every stream reads its index backwards");
        sqlServer.Should().Contain("ORDER BY d.[Price] DESC, d.[Id] DESC", "the direct index too");
        sqlServer.Should().Contain("(i.[Price] <= s.a0 AND (i.[Price] < s.a0 OR i.[Id] < s.a1))", "the keyset seeks before the position");
        sqlServer.Should().NotContain(" > s.a").And.NotContain(" >= s.a");

        var postgres = PostgreSqlDatabaseProvider.Instance.BuildPageRoundSql(options, spec);
        postgres.Should().Contain("ORDER BY c0 DESC, c1 DESC");
        postgres.Should().Contain("ORDER BY i.\"Price\" DESC, i.\"Id\" DESC");
        postgres.Should().Contain("ORDER BY d.\"Price\" DESC, d.\"Id\" DESC");
        postgres.Should().Contain("(i.\"Price\" <= s.a0 AND (i.\"Price\" < s.a0 OR i.\"Id\" < s.a1))", "the keyset seeks before the position");
        postgres.Should().NotContain(" > s.a").And.NotContain(" >= s.a");

        // Ascending is the default, unchanged.
        var ascending = SqlServerDatabaseProvider.Instance.BuildPageRoundSql(options, spec with { Descending = false });
        ascending.Should().Contain("ORDER BY c0, c1").And.Contain("(i.[Price] >= s.a0 AND (i.[Price] > s.a0 OR i.[Id] > s.a1))").And.NotContain("DESC");
    }
}
