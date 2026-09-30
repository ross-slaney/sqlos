using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure;
using SqlOS.BehaviorLock.Infrastructure.Database;

// Scenarios are isolated (own host, own database), so they run in parallel. Override the
// worker count with: dotnet test ... -- MSTest.Parallelize.Workers=8
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace SqlOS.BehaviorLock;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        // Fails fast when the build mixes source and package SqlOS assemblies.
        context.WriteLine(SqlOSUnderTest.VerifyLoadedAssembly());
        context.WriteLine($"Database provider: {BehaviorLockDatabase.ProviderName}");
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync() => await BehaviorLockDatabase.StopAsync();
}
