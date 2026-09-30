using Microsoft.VisualStudio.TestTools.UnitTesting;
using PublicApiGenerator;
using SqlOS.BehaviorLock.Infrastructure;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// Approves the public API of the SqlOS assembly under test. In source mode it locks the
/// refactor's surface; the baseline job (package mode) compares the released 7.2.1 assembly to
/// the same approval, proving it describes what shipped. Assembly-level attributes are left out
/// because they carry build metadata rather than API.
/// </summary>
[TestClass]
[TestCategory("gate")]
public sealed class PublicApiGateTests
{
    public static string ApprovedDirectory => RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "PublicApi");

    [TestMethod]
    public async Task Public_api_matches_the_approval()
    {
        var api = SqlOSUnderTest.Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            IncludeAssemblyAttributes = false
        });

        await Approvals.VerifyAsync(ApprovedDirectory, "SqlOS", api.ReplaceLineEndings("\n").TrimEnd('\n') + "\n");
    }
}
