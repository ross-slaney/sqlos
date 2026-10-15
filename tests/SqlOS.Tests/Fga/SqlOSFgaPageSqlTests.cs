using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Fga.Configuration;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaPageSqlTests
{
    private static readonly SqlOSFgaPageOrderStore Order = new(
        FgaSchema: "dbo",
        ResourcesTable: "SqlOSFgaResources",
        MaxDepth: 7,
        EntrySchema: "dbo",
        EntryTable: "SqlOSFgaPage_Products_Price",
        EntryNodeIdColumn: "NodeId",
        EntrySortColumns: ["Price"],
        EntryRowKeyColumn: "RowKey",
        AppSchema: null,
        AppTable: "Products",
        AppSortColumns: ["Price"],
        AppKeyColumn: "Id",
        AppResourceIdColumn: "ResourceId",
        KeyStoreType: "uniqueidentifier");

    // fn_IsResourceAccessible must not change: these are the SHA-256 hashes of its text before
    // the page-order functions were added (default options, and a custom schema, table and depth).
    [TestMethod]
    [DataRow(false, false, "0a94ab5b96978a2d198b0a1014737ba43728fc835b53b3a2bb9c71119f18847c")]
    [DataRow(false, true, "9d0c18819926fa08945b254a4e6bccaac922641f3fa42fcb1f762ac32bf037be")]
    [DataRow(true, false, "e96d930bb67f7e6ba21086065804b37b1f8ff7006cdcb7effa27464032032dd6")]
    [DataRow(true, true, "e2c8c7f23b9e855abebc24fd9d64a0e9b04ec75a9ca40a65d16b2216f4c18ef5")]
    public void IsResourceAccessibleFunction_IsUnchanged(bool postgreSql, bool customOptions, string expectedSha256)
    {
        var sql = Provider(postgreSql).BuildIsResourceAccessibleFunctionSql(customOptions ? CustomOptions() : new SqlOSFgaOptions());

        Sha256(sql).Should().Be(expectedSha256);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResourceAncestors_UsesTheAncestorsCteOfTheAccessCheck_Verbatim(bool postgreSql)
    {
        var options = CustomOptions();
        var provider = Provider(postgreSql);
        var cte = postgreSql
            ? PostgreSqlDatabaseProvider.BuildAncestorsCte(options)
            : SqlServerDatabaseProvider.BuildAncestorsCte(options);

        var accessCheck = provider.BuildIsResourceAccessibleFunctionSql(options);
        var ancestors = provider.BuildResourceAncestorsFunctionSql(options);

        cte.Should().Contain("VisitedPath").And.Contain("CycleDetected").And.Contain("< 7");
        accessCheck.Should().Contain(cte);
        ancestors.Should().Contain(cte);
        ancestors.Should().Contain(postgreSql ? "malformed.\"CycleDetected\" = TRUE" : "malformed.CycleDetected = 1");
        ancestors.Should().Contain(postgreSql ? "truncated.\"Depth\" = 7" : "truncated.Depth = 7");
    }

    [TestMethod]
    public void ResourceAncestors_IsAnInlineTableFunction()
    {
        var sqlServer = SqlServerDatabaseProvider.Instance.BuildResourceAncestorsFunctionSql(new SqlOSFgaOptions());
        var postgreSql = PostgreSqlDatabaseProvider.Instance.BuildResourceAncestorsFunctionSql(new SqlOSFgaOptions());

        sqlServer.Should().StartWith("CREATE OR ALTER FUNCTION [dbo].fn_ResourceAncestors(")
            .And.Contain("RETURNS TABLE")
            .And.Contain("SELECT a.Id AS NodeId, a.Depth");
        postgreSql.Should().StartWith("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_ResourceAncestors\"(")
            .And.Contain("RETURNS TABLE(\"NodeId\" varchar(450), \"Depth\" int)")
            .And.Contain("LANGUAGE sql")
            .And.Contain("STABLE");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PageNodes_SharesTheSubjectActivityConditions_ByteForByte(bool postgreSql)
    {
        var options = CustomOptions();
        var provider = Provider(postgreSql);
        var conditions = postgreSql
            ? PostgreSqlDatabaseProvider.BuildSubjectActivityConditions(options)
            : SqlServerDatabaseProvider.BuildSubjectActivityConditions(options);

        var accessCheck = provider.BuildIsResourceAccessibleFunctionSql(options);
        var pageNodes = provider.BuildPageNodesFunctionSql(options);

        conditions.Should().Contain("'service_account'").And.Contain("caller");
        accessCheck.Should().Contain(conditions);
        pageNodes.Should().Contain(conditions);
    }

    [TestMethod]
    public void PageNodes_ReadsTheSubjectListLikeTheAccessCheck_AndReturnsMinimalNodes()
    {
        var sqlServer = SqlServerDatabaseProvider.Instance.BuildPageNodesFunctionSql(new SqlOSFgaOptions());
        var postgreSql = PostgreSqlDatabaseProvider.Instance.BuildPageNodesFunctionSql(new SqlOSFgaOptions());

        sqlServer.Should().Contain("CREATE OR ALTER FUNCTION [dbo].fn_PageNodes(")
            .And.Contain("FROM OPENJSON(@SubjectIds)")
            .And.Contain("JSON_VALUE(@SubjectIds, '$[0]')")
            .And.Contain("node.IsActive = 1")
            .And.Contain("g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE()")
            .And.Contain("FROM [dbo].fn_ResourceAncestors(granted.NodeId) ancestor")
            .And.Contain("WHERE ancestor.Depth >= 1");
        postgreSql.Should().Contain("CREATE OR REPLACE FUNCTION \"dbo\".\"fn_PageNodes\"(")
            .And.Contain("jsonb_array_elements_text(p_subject_ids::jsonb)")
            .And.Contain("(p_subject_ids::jsonb ->> 0)")
            .And.Contain("g.\"EffectiveFrom\" IS NULL OR g.\"EffectiveFrom\" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')")
            .And.Contain("CROSS JOIN LATERAL \"dbo\".\"fn_ResourceAncestors\"(granted.\"NodeId\") ancestor")
            .And.Contain("WHERE chain.\"Depth\" = 0\nEXCEPT\n")
            .And.Contain("WHERE chain.\"Depth\" >= 1");
    }

    // Measured on PostgreSQL: inlined into the page query, the function's cost estimate crosses
    // jit_above_cost and JIT compilation takes longer than the query.
    [TestMethod]
    public void PageNodes_OnPostgreSql_IsNotInlined_AndRunsWithoutJit()
    {
        PostgreSqlDatabaseProvider.Instance.BuildPageNodesFunctionSql(new SqlOSFgaOptions())
            .Should().Contain("LANGUAGE sql\nSTABLE\nROWS 10\nSET jit = off\nAS $sqlos$");
    }

    [TestMethod]
    public void KeyStatements_TakeTheKeysAsOneParameter()
    {
        var sqlServerDelete = SqlServerDatabaseProvider.Instance.BuildDeletePageEntriesByKeysSql(Order, "@keys");
        var sqlServerInsert = SqlServerDatabaseProvider.Instance.BuildInsertPageEntriesByKeysSql(Order, "@keys");
        var postgreSqlDelete = PostgreSqlDatabaseProvider.Instance.BuildDeletePageEntriesByKeysSql(Order, "@keys");
        var postgreSqlInsert = PostgreSqlDatabaseProvider.Instance.BuildInsertPageEntriesByKeysSql(Order, "@keys");

        sqlServerDelete.Should().Be("""
            DELETE FROM [dbo].[SqlOSFgaPage_Products_Price]
            WHERE [RowKey] IN (SELECT CAST(k.[value] AS uniqueidentifier) FROM OPENJSON(@keys) AS k);
            """);
        sqlServerInsert.Should().Be("""
            INSERT INTO [dbo].[SqlOSFgaPage_Products_Price] ([NodeId], [Price], [RowKey])
            SELECT a.NodeId, t.[Price], t.[Id]
            FROM [Products] AS t
            CROSS APPLY [dbo].[fn_ResourceAncestors](t.[ResourceId]) AS a
            WHERE t.[Id] IN (SELECT CAST(k.[value] AS uniqueidentifier) FROM OPENJSON(@keys) AS k);
            """);
        postgreSqlDelete.Should().Be("""
            DELETE FROM "dbo"."SqlOSFgaPage_Products_Price"
            WHERE "RowKey" = ANY(@keys);
            """);
        postgreSqlInsert.Should().Be("""
            INSERT INTO "dbo"."SqlOSFgaPage_Products_Price" ("NodeId", "Price", "RowKey")
            SELECT a."NodeId", t."Price", t."Id"
            FROM "Products" AS t
            CROSS JOIN LATERAL "dbo"."fn_ResourceAncestors"(t."ResourceId") AS a
            WHERE t."Id" = ANY(@keys);
            """);
    }

    [TestMethod]
    public void SubtreeStatements_WalkDescendantsOfTheMovedResources_BoundedByMaxDepth()
    {
        var both = new SqlOSFgaChangedResources("@movedResources", "@addedOrDeletedResources");
        var sqlServerDelete = SqlServerDatabaseProvider.Instance.BuildDeletePageEntriesBySubtreeSql(Order, both);
        var sqlServerInsert = SqlServerDatabaseProvider.Instance.BuildInsertPageEntriesBySubtreeSql(Order, both, exceptKeys: null);
        var postgreSqlDelete = PostgreSqlDatabaseProvider.Instance.BuildDeletePageEntriesBySubtreeSql(Order, both);
        var postgreSqlInsert = PostgreSqlDatabaseProvider.Instance.BuildInsertPageEntriesBySubtreeSql(Order, both, exceptKeys: null);

        foreach (var sql in new[] { sqlServerDelete, sqlServerInsert })
        {
            sql.Should().StartWith("WITH subtree AS (")
                .And.Contain("FROM OPENJSON(@movedResources) AS r")
                .And.Contain("FROM [dbo].[SqlOSFgaResources] AS child")
                .And.Contain("INNER JOIN subtree ON child.ParentId = subtree.Id")
                .And.Contain("WHERE subtree.Depth < 7")
                .And.NotContain("OPENJSON(@addedOrDeletedResources) AS r");
        }

        foreach (var sql in new[] { postgreSqlDelete, postgreSqlInsert })
        {
            sql.Should().StartWith("WITH RECURSIVE subtree AS (")
                .And.Contain("FROM unnest(@movedResources) AS ids(id)")
                .And.Contain("FROM \"dbo\".\"SqlOSFgaResources\" AS child")
                .And.Contain("WHERE subtree.\"Depth\" < 7");
        }

        const string sqlServerResources = "WHERE t.[ResourceId] IN (SELECT subtree.Id FROM subtree UNION ALL SELECT CAST(changed.[value] AS NVARCHAR(450)) FROM OPENJSON(@addedOrDeletedResources) AS changed)";
        const string postgreSqlResources = "WHERE t.\"ResourceId\" IN (SELECT subtree.\"Id\" FROM subtree UNION ALL SELECT changed.id::varchar(450) FROM unnest(@addedOrDeletedResources) AS changed(id))";
        sqlServerDelete.Should().Contain("DELETE FROM [dbo].[SqlOSFgaPage_Products_Price]").And.Contain(sqlServerResources);
        sqlServerInsert.Should().Contain("CROSS APPLY [dbo].[fn_ResourceAncestors](t.[ResourceId]) AS a").And.EndWith($"{sqlServerResources};");
        postgreSqlDelete.Should().Contain(postgreSqlResources);
        postgreSqlInsert.Should().Contain("CROSS JOIN LATERAL \"dbo\".\"fn_ResourceAncestors\"(t.\"ResourceId\") AS a").And.EndWith($"{postgreSqlResources};");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubtreeStatements_WithOnlyAddedOrDeletedResources_DoNotWalk(bool postgreSql)
    {
        // ParentId is a foreign key: a resource under an added or deleted one is added or deleted in the same save.
        var resources = new SqlOSFgaChangedResources(null, "@addedOrDeletedResources");
        var provider = Provider(postgreSql);

        foreach (var sql in new[]
                 {
                     provider.BuildDeletePageEntriesBySubtreeSql(Order, resources),
                     provider.BuildInsertPageEntriesBySubtreeSql(Order, resources, exceptKeys: null),
                 })
        {
            sql.Should().NotContain("subtree").And.Contain("@addedOrDeletedResources");
        }

        provider.BuildDeletePageEntriesBySubtreeSql(Order, resources).Should().StartWith("DELETE FROM ");
        provider.BuildInsertPageEntriesBySubtreeSql(Order, resources, exceptKeys: null).Should().StartWith("INSERT INTO ");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubtreeStatements_WithOnlyMovedResources_ReadTheWalk(bool postgreSql)
    {
        var resources = new SqlOSFgaChangedResources("@movedResources", null);
        var sql = Provider(postgreSql).BuildDeletePageEntriesBySubtreeSql(Order, resources);

        sql.Should().Contain(postgreSql ? "IN (SELECT subtree.\"Id\" FROM subtree)" : "IN (SELECT subtree.Id FROM subtree)")
            .And.NotContain("UNION ALL SELECT");
    }

    [TestMethod]
    public void SubtreeInsert_SkipsTheRowsTheKeyInsertWrites()
    {
        var moved = new SqlOSFgaChangedResources("@movedResources", null);
        SqlServerDatabaseProvider.Instance.BuildInsertPageEntriesBySubtreeSql(Order, moved, "@keys0")
            .Should().EndWith("WHERE t.[ResourceId] IN (SELECT subtree.Id FROM subtree) AND t.[Id] NOT IN (SELECT CAST(k.[value] AS uniqueidentifier) FROM OPENJSON(@keys0) AS k);");
        PostgreSqlDatabaseProvider.Instance.BuildInsertPageEntriesBySubtreeSql(Order, moved, "@keys0")
            .Should().EndWith("WHERE t.\"ResourceId\" IN (SELECT subtree.\"Id\" FROM subtree) AND t.\"Id\" <> ALL(@keys0);");
    }

    [TestMethod]
    public void SaveStatements_RunWithoutJit_OnPostgreSql_AndRestoreItAfter()
    {
        var statements = Provider(postgreSql: false).BuildDeletePageEntriesByKeysSql(Order, "@keys0");

        Provider(postgreSql: false).WrapPageMaintenanceSql(statements).Should().Be(statements);
        var wrapped = Provider(postgreSql: true).WrapPageMaintenanceSql(statements);
        wrapped.Should().StartWith("SELECT set_config('sqlos.jit', current_setting('jit'), true), set_config('jit', 'off', true);")
            .And.Contain(statements)
            .And.EndWith("SELECT set_config('jit', current_setting('sqlos.jit'), true);");
    }

    [TestMethod]
    public void RebuildStatements_DeleteEverythingThenInsertEveryRow()
    {
        SqlServerDatabaseProvider.Instance.BuildDeleteAllPageEntriesSql(Order)
            .Should().Be("DELETE FROM [dbo].[SqlOSFgaPage_Products_Price];");
        SqlServerDatabaseProvider.Instance.BuildInsertAllPageEntriesSql(Order)
            .Should().EndWith("CROSS APPLY [dbo].[fn_ResourceAncestors](t.[ResourceId]) AS a;");
        PostgreSqlDatabaseProvider.Instance.BuildInsertAllPageEntriesSql(Order)
            .Should().EndWith("CROSS JOIN LATERAL \"dbo\".\"fn_ResourceAncestors\"(t.\"ResourceId\") AS a;");
    }

    [TestMethod]
    public void KeyParameters_AreJsonOnSqlServer_AndTypedArraysOnPostgreSql()
    {
        var keys = new object[] { Guid.Parse("a2b0e9f4-5d1c-4a52-9bd4-9d0b1c6d1c11"), Guid.Parse("4e7a3a2e-8c3f-4b84-9a52-2f4f7f2b0f10") };

        var sqlServer = SqlServerDatabaseProvider.Instance.CreatePageKeysParameter("@keys", keys);
        var postgreSql = PostgreSqlDatabaseProvider.Instance.CreatePageKeysParameter("@keys", keys);

        sqlServer.Value.Should().Be("[\"a2b0e9f4-5d1c-4a52-9bd4-9d0b1c6d1c11\",\"4e7a3a2e-8c3f-4b84-9a52-2f4f7f2b0f10\"]");
        sqlServer.Size.Should().Be(-1);
        postgreSql.Value.Should().BeOfType<Guid[]>().Which.Should().Equal(keys.Cast<Guid>());
        PostgreSqlDatabaseProvider.Instance.CreatePageResourceIdsParameter("@resourceIds", ["a", "b"])
            .Value.Should().BeOfType<string[]>();
    }

    [TestMethod]
    public void TableChecks_AreScalarQueriesNamedValue()
    {
        SqlServerDatabaseProvider.Instance.BuildTableExistsSql("dbo", "O'Brien")
            .Should().Be("SELECT CASE WHEN OBJECT_ID(N'[dbo].[O''Brien]', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]");
        PostgreSqlDatabaseProvider.Instance.BuildTableExistsSql(null, "Products")
            .Should().Be("SELECT CASE WHEN to_regclass('\"Products\"') IS NULL THEN 0 ELSE 1 END AS \"Value\"");
        SqlServerDatabaseProvider.Instance.BuildTableHasRowsSql(null, "Products")
            .Should().Be("SELECT CASE WHEN EXISTS (SELECT 1 FROM [Products]) THEN 1 ELSE 0 END AS [Value]");
    }

    private static ISqlOSDatabaseProvider Provider(bool postgreSql)
        => postgreSql ? PostgreSqlDatabaseProvider.Instance : SqlServerDatabaseProvider.Instance;

    private static SqlOSFgaOptions CustomOptions()
    {
        var options = new SqlOSFgaOptions { Schema = "tenant]one", MaxResourceHierarchyDepth = 7 };
        options.TableNames.Resources = "resources]current";
        return options;
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)))).ToLowerInvariant();
}
