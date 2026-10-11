using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSFgaFunctionInitializerTests
{
    [TestMethod]
    public void BuildFunctionSql_UsesAtomicDefinitionUpdateAndCycleGuard()
    {
        var sql = SqlOSFgaFunctionInitializer.BuildIsResourceAccessibleFunctionSql(
            new SqlOSFgaOptions { MaxResourceHierarchyDepth = 7 });

        sql.Should().Contain("CREATE OR ALTER FUNCTION");
        sql.Should().NotContain("DROP FUNCTION");
        sql.Should().Contain("CycleDetected");
        sql.Should().Contain("CHARINDEX");
        sql.Should().Contain("malformed.CycleDetected = 1");
        sql.Should().Contain("truncated.Depth = 7");
        sql.Should().Contain("a.Depth < 7");
    }

    [TestMethod]
    public void BuildFunctionSql_EscapesConfiguredIdentifiers()
    {
        var options = new SqlOSFgaOptions { Schema = "tenant]one" };
        options.TableNames.Resources = "resources]current";

        var sql = SqlOSFgaFunctionInitializer.BuildIsResourceAccessibleFunctionSql(options);

        sql.Should().Contain("[tenant]]one].fn_IsResourceAccessible");
        sql.Should().Contain("[tenant]]one].[resources]]current]");
    }
}
