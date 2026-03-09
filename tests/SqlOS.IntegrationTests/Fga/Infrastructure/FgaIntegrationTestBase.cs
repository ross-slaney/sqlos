using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga.Infrastructure;

public abstract class FgaIntegrationTestBase
{
    /// <summary>
    /// Shared context initialized once per assembly by AspireFixture.
    /// All test classes share the same database for performance and reliability.
    /// </summary>
    protected static TestSqlOSDbContext Context => AspireFixture.SharedContext
        ?? throw new InvalidOperationException("Test database not initialized.");
}
