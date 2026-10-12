using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Indexing;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaNativePageSqlTests
{
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TypedPermissionCannotBecomeUntypedWhenItsCompactKeyIsMissing(bool postgreSql)
    {
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), Spec(postgreSql));
        string Q(string column) => Quote(postgreSql, column);

        // NULL in the permission's declared type means untyped. A missing compact key for a declared type
        // must instead make the equality fail; treating that missing key as untyped would widen access.
        sql.Should().Contain($"{Q("__sqlos_grant")}.{Q("PermissionTypeId")} IS NULL");
        sql.Should().Contain($"{Q("__sqlos_grant")}.{Q("PermissionTypeId")} IS NOT NULL");
        sql.Should().Contain($"s.{Q("_TypeKey")} = {Q("__sqlos_grant")}.{Q("PermissionTypeKey")}");
        sql.Should().NotContain($"{Q("__sqlos_grant")}.{Q("PermissionTypeKey")} IS NULL");
        sql.Should().Contain($"s.{Q("_AncestorKey")} = {Q("__sqlos_grant")}.{Q("AncestorKey")}");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OverlappingGrantsAreDeduplicatedBeforeTheGlobalPageLimit(bool postgreSql)
    {
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), Spec(postgreSql));
        string Q(string column) => Quote(postgreSql, column);
        var globalSelect = postgreSql ? "SELECT DISTINCT candidate." : "SELECT DISTINCT TOP (@__sqlos_take) candidate.";
        sql.Should().Contain(globalSelect + Q("__SqlOS_RowKey"));
        sql.Should().Contain($"ORDER BY candidate.{Q("__SqlOS_Order0")} DESC, candidate.{Q("__SqlOS_Order1")} ASC");
        sql.Should().Contain($"page ON app.{Q("Id")} = page.{Q("__SqlOS_RowKey")}");

        // Typed and untyped local streams are mutually exclusive, so UNION ALL is correct there. DISTINCT
        // belongs to their global output, before TOP/LIMIT, rather than to the already-limited final page.
        Regex.Matches(sql, "UNION ALL").Count.Should().Be(1);
        if (postgreSql)
        {
            sql.Should().Contain($"ORDER BY candidate.{Q("__SqlOS_Order0")} DESC, candidate.{Q("__SqlOS_Order1")} ASC\nLIMIT @__sqlos_take");
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MixedDirectionsAndNullableSortValuesKeepNativeColumnComparisons(bool postgreSql)
    {
        // The SQL generator does not convert a nullable/date/text order to an application-comparable key.
        // NULL positioning, collations, and native temporal values remain the engine's column semantics.
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), Spec(postgreSql));
        string Q(string column) => Quote(postgreSql, column);
        var streamOrder = $"ORDER BY s.{Q("DueAt")} DESC, s.{Q("Id")} ASC";
        Regex.Matches(sql, Regex.Escape(streamOrder)).Count.Should().Be(2);
        sql.Should().Contain($"ORDER BY page.{Q("__SqlOS_Order0")} DESC, page.{Q("__SqlOS_Order1")} ASC");
        sql.Should().NotContain("COALESCE(").And.NotContain("ISNULL(").And.NotContain("@Retained");
        sql.Should().NotContain("CAST(s.");
        if (postgreSql)
        {
            sql.Should().NotContain("CONVERT(");
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BothLocalStreamsApplyCopiedAndResidualFiltersBeforeTheirLimit(bool postgreSql)
    {
        string Q(string column) => Quote(postgreSql, column);
        var predicate = $"s.{Q("DueAt")} >= @from AND original.{Q("Description")} LIKE @text";
        var spec = Spec(postgreSql) with
        {
            ApplicationAliasSql = "original",
            NeedsApplicationJoin = true,
            PredicateSql = predicate,
        };
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), spec);

        var join = $"INNER JOIN {spec.ApplicationTableSql} AS original ON original.{Q("Id")} = s.{Q("Id")}";
        Regex.Matches(sql, Regex.Escape(join)).Count.Should().Be(2);
        Regex.Matches(sql, Regex.Escape("AND (" + predicate + ")")).Count.Should().Be(2);
        Regex.Matches(sql, Regex.Escape("AND (" + predicate + ")\nORDER BY s.")).Count.Should().Be(2);
        sql.Should().NotContain("WHERE app.", "the final payload join must not introduce a filter after selecting the page");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LiveGrantEligibilityRetainsCallerAndEveryPrincipalTypeCheck(bool postgreSql)
    {
        var sql = SqlOSFgaNativePageSql.EligibleGrants(postgreSql, new SqlOSFgaOptions());
        string Q(string column) => Quote(postgreSql, column);
        var now = postgreSql ? "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')" : "GETUTCDATE()";
        foreach (var principal in new[] { "subject", "caller" })
        {
            foreach (var type in new[] { "user", "service_account", "group", "agent" })
            {
                sql.Should().Contain($"{principal}.{Q("SubjectTypeId")} <> '{type}'");
            }

            sql.Should().Contain($"{principal}_service.{Q("ExpiresAt")} > {now}");
            sql.Should().Contain($"{principal}_agent.{Q("SubjectId")} IS NOT NULL");
        }

        sql.Should().Contain($"grant_row.{Q("EffectiveFrom")} <= {now}");
        sql.Should().Contain($"grant_row.{Q("EffectiveTo")} >= {now}");
        sql.Should().Contain(postgreSql ? "(@__sqlos_subjects::jsonb ->> 0)" : "JSON_VALUE(@__sqlos_subjects, '$[0]')");
        sql.Should().Contain($"role_permission.{Q("PermissionId")} = @__sqlos_permission");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheStatementIsComposableAndDoesNotJoinPayloadInsideCoveredStreams(bool postgreSql)
    {
        var spec = Spec(postgreSql);
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), spec);
        sql.Should().StartWith(postgreSql ? "SELECT app.*" : "SELECT TOP (@__sqlos_take) app.*");
        sql.Should().NotContain("DECLARE ").And.NotContain("CREATE TABLE ").And.NotEndWith(";");
        Regex.Matches(sql, Regex.Escape(spec.ApplicationTableSql)).Count.Should().Be(1);
        sql.Should().Contain(postgreSql ? "CROSS JOIN LATERAL" : "CROSS APPLY");
    }

    [TestMethod]
    public void TheOrderMustContainItsActualUniqueKeyAtTheEnd()
    {
        var spec = Spec(true) with { Order = [new("Id", false), new("DueAt", false)] };
        var build = () => SqlOSFgaNativePageSql.Build(true, new SqlOSFgaOptions(), spec);
        build.Should().Throw<ArgumentException>().WithMessage("*unique application key*");
    }

    [DataTestMethod]
    [DataRow(true, "g")]
    [DataRow(false, "g")]
    [DataRow(true, "__sqlos_grant")]
    [DataRow(false, "__sqlos_grant")]
    public void ResidualApplicationAliasCannotShadowTheCorrelatedGrant(bool postgreSql, string applicationAlias)
    {
        string Q(string name) => Quote(postgreSql, name);
        var grantAlias = applicationAlias == "__sqlos_grant" ? "__sqlos_grant_" : "__sqlos_grant";
        var spec = Spec(postgreSql) with
        {
            ApplicationAliasSql = Q(applicationAlias),
            NeedsApplicationJoin = true,
            PredicateSql = $"{Q(applicationAlias)}.{Q("Description")} = @text",
        };
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), spec);
        sql.Should().Contain($"AS {Q(grantAlias)}\n");
        sql.Should().Contain($"s.{Q("_AncestorKey")} = {Q(grantAlias)}.{Q("AncestorKey")}");
        sql.Should().Contain($"AS {Q(applicationAlias)} ON {Q(applicationAlias)}.{Q("Id")}");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PayloadJoinRetainsTheResourceIdentityThatAuthorizedTheCandidate(bool postgreSql)
    {
        string Q(string name) => Quote(postgreSql, name);
        var sql = SqlOSFgaNativePageSql.Build(postgreSql, new SqlOSFgaOptions(), Spec(postgreSql));
        sql.Should().Contain($"s.{Q("ResourceId")} AS {Q("__SqlOS_ResourceId")}");
        sql.Should().Contain($"candidate.{Q("__SqlOS_ResourceId")}");
        sql.Should().Contain($"AND app.{Q("ResourceId")} = page.{Q("__SqlOS_ResourceId")}");
    }

    private static SqlOSFgaNativePageSqlSpec Spec(bool postgreSql) => new()
    {
        ProjectionTableSql = Quote(postgreSql, "auth") + "." + Quote(postgreSql, "ScopedItems"),
        ApplicationTableSql = Quote(postgreSql, "app") + "." + Quote(postgreSql, "Items"),
        KeyColumn = "Id",
        ResourceIdColumn = "ResourceId",
        Order = [new("DueAt", true), new("Id", false)],
    };

    private static string Quote(bool postgreSql, string identifier) => postgreSql ? "\"" + identifier + "\"" : "[" + identifier + "]";
}
