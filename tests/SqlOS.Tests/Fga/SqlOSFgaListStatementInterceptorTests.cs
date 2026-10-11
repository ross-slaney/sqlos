using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Extensions;
using SqlOS.Fga;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaListStatementInterceptorTests
{
    private const string ListStatement = """
        SELECT TOP(@__p_1) [d].[Id], [d].[ResourceId]
        FROM [Docs] AS [d]
        WHERE EXISTS (
            SELECT 1
            FROM [dbo].[fn_VisibleSet](@__subjectIdsJson_0, @__permissionId_1, @__typeId_2) AS [f]
            WHERE [f].[ResourceId] = [d].[ResourceId])
        ORDER BY [d].[Id]
        """;

    [TestMethod]
    public void OnSqlServer_AStatementThatReadsTheList_IsPlannedForEachCaller()
    {
        var sql = SqlOSFgaListStatementInterceptor.Apply(ListStatement, sqlServer: true);

        sql.Should().EndWith("ORDER BY [d].[Id]" + Environment.NewLine + "OPTION (RECOMPILE)");
        sql.Should().StartWith("SELECT TOP(@__p_1)");
    }

    [TestMethod]
    public void TheHintIsAddedOnce_AndAfterATrailingSemicolon()
    {
        var once = SqlOSFgaListStatementInterceptor.Apply(ListStatement, sqlServer: true);
        SqlOSFgaListStatementInterceptor.Apply(once, sqlServer: true).Should().Be(once);

        var ended = SqlOSFgaListStatementInterceptor.Apply(ListStatement + ";" + Environment.NewLine, sqlServer: true);
        ended.Should().EndWith("ORDER BY [d].[Id]" + Environment.NewLine + "OPTION (RECOMPILE)");
        ended.Should().NotContain(";");
    }

    [TestMethod]
    public void AStatementThatChecksEachRow_IsLeftAlone_OnBothEngines()
    {
        // The row check's plan does not depend on who asks; every caller shares it.
        var rowCheck = ListStatement.Replace("[dbo].[fn_VisibleSet](@__subjectIdsJson_0, @__permissionId_1, @__typeId_2) AS [f]", "[dbo].[fn_CheckRow]([d].[ResourceId], @__subjectIdsJson_0, @__permissionId_1) AS [f]", StringComparison.Ordinal);
        SqlOSFgaListStatementInterceptor.Apply(rowCheck, sqlServer: true).Should().Be(rowCheck);
        SqlOSFgaListStatementInterceptor.Apply(ListStatement, sqlServer: false).Should().Be(ListStatement, "PostgreSQL plans the list function by its declared row count");
    }

    [TestMethod]
    public void UseSqlOS_AttachesTheInterceptorToTheContextsOptions()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseSqlServer("Server=.;Database=SqlOS_Interceptor;Trusted_Connection=True;TrustServerCertificate=True")
            .UseSqlOS()
            .Options;

        var interceptors = options.FindExtension<CoreOptionsExtension>()!.Interceptors!;
        interceptors.Should().ContainSingle(i => i is SqlOSFgaListStatementInterceptor);
        interceptors.OfType<IInterceptor>().Should().Contain(SqlOSFgaListStatementInterceptor.Instance);
    }
}
