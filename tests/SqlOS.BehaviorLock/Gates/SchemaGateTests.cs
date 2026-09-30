using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure;
using SqlOS.BehaviorLock.Infrastructure.Database;
using SqlOS.BehaviorLock.Infrastructure.Hosting;
using SqlOS.BehaviorLock.Infrastructure.Schema;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// Approves, per provider, the schema SqlOS bootstrap creates on an empty database and the EF Core
/// mapping of the SqlOS model that hosts query through. Both change only with a behavior-ledger entry.
/// </summary>
[TestClass]
[TestCategory("gate")]
public sealed class SchemaGateTests
{
    public static string ApprovedDirectory => RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "Schema");

    [TestMethod]
    public async Task Bootstrapped_schema_matches_the_approval()
    {
        await using var host = await ScenarioHost.StartAsync(HostProfiles.DashboardCallback);
        var schema = await SchemaDump.RenderAsync(host.ConnectionString);

        await Approvals.VerifyAsync(ApprovedDirectory, BehaviorLockDatabase.ProviderName, schema);
    }

    [TestMethod]
    public async Task Ef_model_mapping_matches_the_approval()
    {
        var builder = new DbContextOptionsBuilder<SqlOSModelContext>();
        if (BehaviorLockDatabase.Provider == DatabaseProvider.PostgreSql)
        {
            builder.UseNpgsql("Host=ef-model;Database=ef-model");
        }
        else
        {
            builder.UseSqlServer("Server=ef-model;Database=ef-model;Trusted_Connection=True");
        }

        await using var context = new SqlOSModelContext(builder.Options);

        await Approvals.VerifyAsync(ApprovedDirectory, $"EfModel.{BehaviorLockDatabase.ProviderName}", EfModelDump.Render(context));
    }
}
