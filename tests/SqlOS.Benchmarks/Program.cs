using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlOS.Benchmarks;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Infrastructure;
using SqlOS.Benchmarks.Reporting;
using SqlOS.Benchmarks.Scenarios;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;

// SqlOS SHRBAC benchmarks. See README.md for what is measured and why.

BenchmarkOptions? options;
try
{
    options = BenchmarkOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(BenchmarkOptions.Usage);
    return 2;
}

if (options is null)
{
    Console.WriteLine(BenchmarkOptions.Usage);
    return 0;
}

var log = new Log();
var cancellation = CancellationToken.None;
var provider = options.Provider == DatabaseProvider.PostgreSql ? "postgresql" : "sqlserver";
var outputDirectory = Path.GetFullPath(Path.Combine(options.OutputDirectory, provider));
log.Info($"SqlOS SHRBAC benchmarks · {provider} · scales {string.Join(" → ", options.Scales.Select(RetailTree.Count))}");

if (options.Provider == DatabaseProvider.PostgreSql)
{
    // SqlOS maps DateTime to PostgreSQL timestamp without time zone (the same switch SqlOS sets itself).
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
}

await using var server = await DatabaseServer.StartAsync(options, log, cancellation);
await server.RecreateDatabaseAsync(cancellation);

var fga = new SqlOSFgaOptions { RootResourceId = BenchmarkModel.RootResourceId, RootResourceName = "Retail" };
var contextOptions = new DbContextOptionsBuilder<BenchDbContext>();
if (options.Provider == DatabaseProvider.PostgreSql)
{
    contextOptions.UseNpgsql(server.DatabaseConnectionString);
}
else
{
    contextOptions.UseSqlServer(server.DatabaseConnectionString);
}

contextOptions.AddInterceptors(new PlanCapture(options.Provider));
var builtContextOptions = contextOptions.Options;
BenchDbContext CreateContext() => new(builtContextOptions);

// The schema, indexes, function, and core seed exactly as SqlOS creates them for an application.
log.Info("Creating the SqlOS FGA schema, fn_IsResourceAccessible, and the authorization model...");
await using (var db = CreateContext())
{
    await db.Database.EnsureCreatedAsync(cancellation);
    await new SqlOSFgaSchemaInitializer(db, Options.Create(fga), NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync(cancellation);
    await new SqlOSFgaFunctionInitializer(db, Options.Create(fga), NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync(cancellation);
    var seed = new SqlOSFgaSeedService(db, Options.Create(fga), NullLogger<SqlOSFgaSeedService>.Instance);
    await seed.SeedCoreAsync(cancellation);
    await seed.SeedAuthorizationDataAsync(BenchmarkModel.Seed, cancellation);
}

var tree = RetailTree.Build(fga.RootResourceId, options.Seed);
var chains = tree.Nodes.Count(n => n.TypeId == "chain");
var managedScopes = tree.Nodes.Count(n => n.TypeId is "store" or "region" or "chain");
var dataset = new DatasetShape(
    chains,
    tree.Stores.Count,
    tree.Leaves.Count,
    tree.Nodes.Count + 1,
    MaxDepth: 10,
    options.Seed,
    string.Create(CultureInfo.InvariantCulture,
        $"The shipped schema, indexes, and `fn_IsResourceAccessible`, queried through `BuildFilterAsync`. The tree: {chains} retail chains ({chains - 1} at D = 5, one at D = 10) with {tree.Stores.Count:N0} stores and {tree.Nodes.Count + 1:N0} organizational nodes. Store sizes are log-normal, and products are spread through the id range the way rows arrive over time. {managedScopes:N0} managers hold grants on their store, region, or chain. The people measured each resolve to 3 subjects (M = 3)."));

IDatasetLoader loader = options.Provider == DatabaseProvider.PostgreSql
    ? new PostgreSqlDatasetLoader(server.DatabaseConnectionString, fga, log)
    : new SqlServerDatasetLoader(server.DatabaseConnectionString, fga, log);

await loader.ConfigureDatabaseAsync(cancellation);
log.Info($"Loading the hierarchy: {tree.Nodes.Count:N0} organizational nodes, {tree.Stores.Count:N0} stores, {tree.Leaves.Count:N0} leaves...");
await loader.LoadHierarchyAsync(tree, cancellation);

Principals people;
int staffGrants;
await using (var db = CreateContext())
{
    people = await BenchmarkModel.CreatePrincipalsAsync(db, tree, cancellation);
}

await using (var db = CreateContext())
{
    staffGrants = await BenchmarkModel.PopulateOrganizationAsync(db, tree, cancellation);
}

log.Info($"Created the organization: {staffGrants:N0} managers with grants on their store, region, or chain.");

var report = new BenchmarkReport
{
    Provider = provider,
    Engine = await loader.EngineVersionAsync(cancellation),
    Server = server.Description,
    Environment = new RunEnvironment(
        RuntimeInformation.OSDescription,
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        RuntimeInformation.FrameworkDescription,
        Environment.GetEnvironmentVariable("GITHUB_SHA"),
        Environment.GetEnvironmentVariable("GITHUB_REF")),
    Dataset = dataset,
};
log.Info($"Engine: {report.Engine}");

var runner = new ScenarioRunner(CreateContext, fga, tree, Path.Combine(outputDirectory, "plans"), log);
long loaded = 0;
foreach (var target in options.Scales)
{
    log.Info($"Growing the catalog to {RetailTree.Count(target)} products ({target - loaded:N0} new rows in each of two tables)...");
    var timing = await loader.GrowProductsAsync(tree, loaded, target, cancellation);
    loaded = target;

    var size = await loader.DatabaseSizeBytesAsync(cancellation);
    log.Info($"Measuring at {RetailTree.Count(target)} products ({tree.TotalResources(target):N0} resources, {size / 1e9:F1} GB)...");
    var scenarios = ScenarioCatalog.Build(tree, people, target);
    var results = await runner.RunAsync(scenarios, target, cancellation);

    // Grant density does not depend on N, so it is measured once, at the first scale.
    IReadOnlyList<ScenarioResult> density = [];
    if (report.Steps.Count == 0)
    {
        log.Info($"Grant-density pass: {BenchmarkModel.RootCrowdGrants} other people's grants on the root...");
        await using (var db = CreateContext())
        {
            await BenchmarkModel.AddRootCrowdAsync(db, tree, cancellation);
        }

        density = await runner.RunAsync(ScenarioCatalog.Density(scenarios), target, cancellation);
        await using (var db = CreateContext())
        {
            await BenchmarkModel.RemoveRootCrowdAsync(db, cancellation);
        }
    }

    report.Steps.Add(new ScaleStep(
        target,
        tree.TotalResources(target),
        timing.Rows.TotalSeconds,
        timing.Indexes.TotalSeconds,
        timing.Maintenance.TotalSeconds,
        size,
        results)
    {
        Density = density,
    });
}

report.Gates = GateEvaluator.Evaluate(report, GateConfig.Load(options.GatesPath));
report.DurationSeconds = log.Elapsed.TotalSeconds;
var summary = await ReportWriter.WriteAsync(report, outputDirectory, options.SummaryPath, cancellation);
Console.WriteLine();
Console.WriteLine(summary);
log.Info($"Results: {outputDirectory}");

var failed = report.Gates.Where(g => !g.Passed).ToList();
if (failed.Count == 0)
{
    return 0;
}

foreach (var gate in failed)
{
    Console.Error.WriteLine($"GATE FAILED [{gate.Gate}] {gate.Subject}: {gate.Detail}");
}

return options.EnforceGates ? 1 : 0;
