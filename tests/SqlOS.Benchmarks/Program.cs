using System.Diagnostics;
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
using SqlOS.Database;
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

// The schema, indexes, functions, closure triggers, and core seed exactly as SqlOS creates them for an
// application; then the previous release's function beside them, for the regression comparison.
log.Info("Creating the SqlOS FGA schema, fn_AccessRoots, fn_IsResourceAccessible, the closure triggers, and the authorization model...");
await using (var db = CreateContext())
{
    await db.Database.EnsureCreatedAsync(cancellation);
    await new SqlOSFgaSchemaInitializer(db, Options.Create(fga), NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync(cancellation);
    await new SqlOSFgaFunctionInitializer(db, Options.Create(fga), NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync(cancellation);
    var seed = new SqlOSFgaSeedService(db, Options.Create(fga), NullLogger<SqlOSFgaSeedService>.Instance);
    await seed.SeedCoreAsync(cancellation);
    await seed.SeedAuthorizationDataAsync(BenchmarkModel.Seed, cancellation);
}

await ReferenceFunction.CreateAsync(options.Provider, server.DatabaseConnectionString, cancellation);

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
        $"The shipped schema, indexes, `fn_AccessRoots`, `fn_IsResourceAccessible`, and the ancestor closure, queried through `BuildFilterAsync` and `ListVisibleAsync`; the previous release's function beside them on the same data. The tree: {chains} retail chains ({chains - 1} at D = 5, one at D = 10) with {tree.Stores.Count:N0} stores and {tree.Nodes.Count + 1:N0} organizational nodes. Store sizes are log-normal, and products are spread through the id range the way rows arrive over time. {managedScopes:N0} managers hold grants on their store, region, or chain. Two more people hold 10,000 and 100,000 grants on single products, spread through the catalog. The people measured each resolve to 3 subjects (M = 3)."));

// Leave room for the CI runner's own logs and the uploaded results.
long? FreeBytes() => options.DataDirectory is { } directory ? new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace : null;
var diskBudget = FreeBytes() is { } free ? free - 8_000_000_000L : (long?)null;

IDatasetLoader loader = options.Provider == DatabaseProvider.PostgreSql
    ? new PostgreSqlDatasetLoader(server.DatabaseConnectionString, fga, log)
    : new SqlServerDatasetLoader(server.DatabaseConnectionString, fga, diskBudget, log);

await loader.ConfigureDatabaseAsync(cancellation);
var typeSeq = await loader.ReadTypeSeqsAsync(cancellation);
tree.AssignSeqs(await loader.ReadRootSeqAsync(fga.RootResourceId, cancellation));
log.Info($"Loading the hierarchy: {tree.Nodes.Count:N0} organizational nodes, {tree.Stores.Count:N0} stores, {tree.Leaves.Count:N0} leaves, and their closure...");
await loader.LoadHierarchyAsync(tree, typeSeq, cancellation);

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
        RunEnvironment.DetectCpu(),
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        RuntimeInformation.FrameworkDescription,
        Environment.GetEnvironmentVariable("GITHUB_SHA"),
        Environment.GetEnvironmentVariable("GITHUB_REF")),
    Dataset = dataset,
};
log.Info($"Engine: {report.Engine}");

var runner = new ScenarioRunner(CreateContext, options.Provider, fga, tree, Path.Combine(outputDirectory, "plans"), log);
long loaded = 0;
foreach (var target in options.Scales)
{
    log.Info($"Growing the catalog to {RetailTree.Count(target)} products ({target - loaded:N0} new rows in each of two tables, plus their closure)...");
    var timing = await loader.GrowProductsAsync(tree, typeSeq, loaded, target, cancellation);
    if (loaded == 0)
    {
        await using var db = CreateContext();
        var granted = await BenchmarkModel.GrantProductsAsync(db, people, target, cancellation);
        log.Info($"Granted {granted:N0} single products to the many-grants people ({string.Join(", ", people.ManyGrants.Select(p => $"{p.Key}: {p.GrantedProducts:N0}"))}).");
    }

    loaded = target;

    var size = await loader.DatabaseSizeBytesAsync(cancellation);
    var closureRows = tree.ClosureRows(target);
    log.Info(
        $"Measuring at {RetailTree.Count(target)} products ({tree.TotalResources(target):N0} resources, {closureRows:N0} closure rows, {size / 1e9:F1} GB)" +
        (FreeBytes() is { } left ? $", {left / 1e9:F0} GB free on the data disk..." : "..."));
    // The sparse row-filter scans are independent of N and cost minutes, so they run at the first and last
    // scales only: the two the scale gate compares.
    var intermediate = target != options.Scales[0] && target != options.Scales[^1];
    var scenarios = ScenarioCatalog.Build(tree, people, target)
        .Where(s => !options.Exclude.Contains(s.Id) && !(intermediate && ScenarioRunner.IsSparseScan(s)))
        .ToList();
    var results = await runner.RunAsync(scenarios, target, cancellation);

    // Grant density and closure maintenance do not depend on N, so they are measured once, at the first scale.
    IReadOnlyList<ScenarioResult> density = [];
    IReadOnlyList<MaintenanceResult> maintenance = [];
    ClosureCheck? closureCheck = null;
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

        log.Info("Closure maintenance pass: single-row and multi-row inserts, a multi-row delete, and subtree updates...");
        var loadedClosure = await loader.ClosureChecksumAsync(cancellation);
        if (loadedClosure.Rows != closureRows)
        {
            throw new InvalidOperationException($"The loaded closure has {loadedClosure.Rows:N0} rows; the dataset implies {closureRows:N0}.");
        }

        maintenance = await new MaintenancePass(CreateContext, options.Provider, fga, tree, loader, log).RunAsync(people, target, cancellation);
        var restoredClosure = await loader.ClosureChecksumAsync(cancellation);

        log.Info("Rebuilding the closure from scratch with SqlOS's own procedure...");
        var rebuild = Stopwatch.StartNew();
        await using (var db = CreateContext())
        {
            db.Database.SetCommandTimeout(0);
            await db.Database.ExecuteSqlRawAsync(SqlOSDatabase.Resolve(db.Database).BuildResourceClosureRebuildSql(fga), cancellation);
        }

        rebuild.Stop();
        var rebuiltClosure = await loader.ClosureChecksumAsync(cancellation);
        closureCheck = new ClosureCheck(
            loadedClosure.Rows, loadedClosure.Hash.ToString(CultureInfo.InvariantCulture),
            restoredClosure.Rows, restoredClosure.Hash.ToString(CultureInfo.InvariantCulture),
            rebuiltClosure.Rows, rebuiltClosure.Hash.ToString(CultureInfo.InvariantCulture),
            rebuild.Elapsed.TotalSeconds);
        log.Info(
            $"  closure: loaded {loadedClosure.Rows:N0} rows, after maintenance {restoredClosure.Rows:N0}, rebuilt {rebuiltClosure.Rows:N0} in {rebuild.Elapsed.TotalSeconds:F1}s; " +
            (closureCheck.Agrees ? "identical." : "DIFFERENT."));
    }

    report.Steps.Add(new ScaleStep(
        target,
        tree.TotalResources(target),
        closureRows,
        timing.Rows.TotalSeconds,
        timing.Closure.TotalSeconds,
        timing.Indexes.TotalSeconds,
        timing.Maintenance.TotalSeconds,
        size,
        results)
    {
        Density = density,
        Maintenance = maintenance,
        ClosureCheck = closureCheck,
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
