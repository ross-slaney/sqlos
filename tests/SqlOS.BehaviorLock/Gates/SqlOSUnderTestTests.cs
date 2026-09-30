using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure;
using SqlOS.BehaviorLock.Infrastructure.Database;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// Records which SqlOS the run tested, as a result in the test report. The assembly setup
/// already fails the run when the loaded assembly does not match the build mode.
/// </summary>
[TestClass]
public sealed class SqlOSUnderTestTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void The_run_loads_the_sqlos_build_it_was_built_for()
    {
        TestContext.WriteLine(SqlOSUnderTest.VerifyLoadedAssembly());
        TestContext.WriteLine($"Database provider: {BehaviorLockDatabase.ProviderName}");
        if (SqlOSUnderTest.IsPackage)
        {
            StringAssert.StartsWith(SqlOSUnderTest.InformationalVersion, SqlOSUnderTest.BaselineVersion);
        }
    }
}
