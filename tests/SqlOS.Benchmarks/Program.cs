using System.Text.Json;
using System.Text.Json.Serialization;
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
using SqlOS.Fga;
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

if (options.EvaluatePath is { } resultsPath)
{
    // The gates alone, on a run's results: the same evaluation CI ran, for recalibrating a gate.
    var saved = JsonSerializer.Deserialize<BenchmarkReport>(
        await File.ReadAllTextAsync(resultsPath),
        new JsonSerializerOptions { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate })
        ?? throw new InvalidOperationException($"Could not read {resultsPath}.");
    var evaluated = GateEvaluator.Evaluate(saved, GateConfig.Load(options.GatesPath));
    saved.Gates = evaluated;
    if (options.SummaryPath is { } summaryFile)
    {
        await File.WriteAllTextAsync(summaryFile, ReportWriter.Markdown(saved));
    }

    foreach (var group in evaluated.GroupBy(g => g.Gate))
    {
        Console.WriteLine($"{(group.All(g => g.Passed) ? "PASS" : "FAIL")} {group.Key} {group.Count(g => g.Passed)}/{group.Count()}");
    }

    foreach (var gate in evaluated.Where(g => !g.Passed))
    {
        Console.WriteLine($"  FAILED [{gate.Gate}] {gate.Subject}: {gate.Detail}");
    }

    return evaluated.All(g => g.Passed) || !options.EnforceGates ? 0 : 1;
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

// The FGA options an application would register.
var fga = new SqlOSFgaOptions { RootResourceId = BenchmarkModel.RootResourceId, RootResourceName = "Retail" };
var plainOptions = new DbContextOptionsBuilder<BenchDbContext>();
if (options.Provider == DatabaseProvider.PostgreSql)
{
    plainOptions.UseNpgsql(server.DatabaseConnectionString);
}
else
{
    plainOptions.UseSqlServer(server.DatabaseConnectionString);
}

var planCapture = new PlanCapture(options.Provider);
plainOptions.AddInterceptors(planCapture);

var builtOptions = plainOptions.Options;
BenchDbContext CreateContext() => new(builtOptions);

// The schema, indexes, functions, lineage triggers, and core seed exactly as SqlOS creates them for an
// application.
log.Info("Creating the SqlOS FGA schema, the lineage with its per-level indexes, fn_AccessRoots, fn_ListVisible, fn_VisibleSet, fn_ListFirst, fn_IsResourceAccessible, fn_CheckRow, the triggers, and the authorization model...");
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
        $"The shipped schema, the resource lineage with its per-level indexes, and SqlOS's functions, queried on a product table through the filter `BuildFilterAsync` returns (`list.*`): one EXISTS in the application's LINQ query, over `fn_VisibleSet` when `fn_ListFirst` finds the caller sees fewer rows than the table's cap (8·√rows), else over `fn_CheckRow` (the point check) for each row the query reads. The tree: {chains} retail chains ({chains - 1} at D = 5, one at D = 10) with {tree.Stores.Count:N0} stores and {tree.Nodes.Count + 1:N0} organizational nodes. Store sizes are log-normal, and products are spread through the id range the way rows arrive over time. {managedScopes:N0} managers hold grants on their store, region, or chain. One person holds {BenchmarkModel.HundredStoreGrants} store grants across the chains; two more hold 10,000 and 100,000 grants on single products, spread through the catalog. The people measured each resolve to 3 subjects (M = 3)."));

// Leave room for the CI runner's own logs and the uploaded results.
long? FreeBytes() => options.DataDirectory is { } directory ? new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace : null;
var diskBudget = FreeBytes() is { } free ? free - 8_000_000_000L : (long?)null;

IDatasetLoader loader = options.Provider == DatabaseProvider.PostgreSql
    ? new PostgreSqlDatasetLoader(server.DatabaseConnectionString, fga, log)
    : new SqlServerDatasetLoader(server.DatabaseConnectionString, fga, diskBudget, log);

await loader.ConfigureDatabaseAsync(cancellation);
tree.AssignSeqs(await loader.ReadRootSeqAsync(fga.RootResourceId, cancellation));
log.Info($"Loading the hierarchy: {tree.Nodes.Count:N0} organizational nodes with their lineage, {tree.Stores.Count:N0} stores, {tree.Leaves.Count:N0} leaves...");
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
        RunEnvironment.DetectCpu(),
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        RuntimeInformation.FrameworkDescription,
        Environment.GetEnvironmentVariable("GITHUB_SHA"),
        Environment.GetEnvironmentVariable("GITHUB_REF")),
    Dataset = dataset,
};
log.Info($"Engine: {report.Engine}");

var runner = new ScenarioRunner(CreateContext, fga, tree, Path.Combine(outputDirectory, "plans"), options.ScenarioBudgetSeconds, log);
var extraGates = new List<GateResult>();
long loaded = 0;
long lastSize = 0;
foreach (var target in options.Scales)
{
    // The database grows with the product count. A scale that would not fit the disk is reported, not
    // attempted: running out mid-load loses the run.
    if (loaded > 0 && FreeBytes() is { } available)
    {
        var projected = (long)(lastSize * ((double)target / loaded) * 1.1);
        var fits = projected - lastSize <= available - 4_000_000_000L;
        if (!fits)
        {
            var detail = string.Create(
                CultureInfo.InvariantCulture,
                $"about {projected / 1e9:F0} GB at {RetailTree.Count(target)} products ({lastSize / 1e9:F1} GB at {RetailTree.Count(loaded)}), {available / 1e9:F0} GB free");
            log.Info($"Not growing to {RetailTree.Count(target)} products: {detail}.");
            extraGates.Add(new GateResult("disk", RetailTree.Count(target), false, detail));
            break;
        }
    }

    log.Info($"Growing the catalog to {RetailTree.Count(target)} products ({target - loaded:N0} new rows in each of two tables, lineage included)...");
    var timing = await loader.GrowProductsAsync(tree, loaded, target, cancellation);
    if (loaded == 0)
    {
        await using var db = CreateContext();
        var granted = await BenchmarkModel.GrantProductsAsync(db, people, target, cancellation);
        log.Info($"Granted {granted:N0} single products to the many-grants people ({string.Join(", ", people.ManyGrants.Select(p => $"{p.Key}: {p.GrantedProducts:N0}"))}).");
    }

    loaded = target;

    var size = await loader.DatabaseSizeBytesAsync(cancellation);
    lastSize = size;
    log.Info(
        $"Measuring at {RetailTree.Count(target)} products ({tree.TotalResources(target):N0} resources, {size / 1e9:F1} GB)" +
        (FreeBytes() is { } left ? $", {left / 1e9:F0} GB free on the data disk..." : "..."));
    var scenarios = ScenarioCatalog.Build(tree, people, target)
        .Where(s => !options.Exclude.Contains(s.Id))
        .ToList();
    var results = await runner.RunAsync(scenarios, target, cancellation);

    // Grant density and lineage maintenance do not depend on N, so they are measured once, at the first scale.
    IReadOnlyList<ScenarioResult> density = [];
    IReadOnlyList<MaintenanceResult> maintenance = [];
    LineageCheck? lineageCheck = null;
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

        log.Info("Lineage maintenance pass: single-row and multi-row inserts, a multi-row delete, and subtree updates...");
        var loadedLineage = await loader.LineageChecksumAsync(cancellation);
        if (loadedLineage.Rows != tree.TotalResources(target))
        {
            throw new InvalidOperationException($"The loaded lineage covers {loadedLineage}; the dataset has {tree.TotalResources(target):N0} resources.");
        }

        maintenance = await new MaintenancePass(CreateContext, options.Provider, fga, tree, log).RunAsync(people, target, cancellation);
        var restoredLineage = await loader.LineageChecksumAsync(cancellation);

        log.Info("Rebuilding the lineage from scratch with SqlOS's own procedure...");
        var rebuild = Stopwatch.StartNew();
        await using (var db = CreateContext())
        {
            db.Database.SetCommandTimeout(0);
            await db.Database.ExecuteSqlRawAsync(SqlOSDatabase.Resolve(db.Database).BuildLineageRebuildSql(fga), cancellation);
        }

        rebuild.Stop();
        var rebuiltLineage = await loader.LineageChecksumAsync(cancellation);
        var agrees = loadedLineage.Equals(restoredLineage) && restoredLineage.Equals(rebuiltLineage);
        lineageCheck = new LineageCheck(loadedLineage.ToString(), restoredLineage.ToString(), rebuiltLineage.ToString(), agrees, rebuild.Elapsed.TotalSeconds);
        log.Info(
            $"  lineage: loaded {loadedLineage}, after maintenance {restoredLineage}, rebuilt {rebuiltLineage} in {rebuild.Elapsed.TotalSeconds:F1}s; " +
            (agrees ? "identical." : "DIFFERENT."));
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
        Maintenance = maintenance,
        LineageCheck = lineageCheck,
    });
}

report.Gates = [.. GateEvaluator.Evaluate(report, GateConfig.Load(options.GatesPath)), .. extraGates];
report.DurationSeconds = log.Elapsed.TotalSeconds;
var summary = await ReportWriter.WriteAsync(report, outputDirectory, options.SummaryPath, cancellation);
Console.WriteLine();
Console.WriteLine(summary);
log.Info($"Results: {outputDirectory}");
if (DatabaseServer.ExplainMilliseconds is { } explainMs)
{
    // The engine's log, with the actual plan of every statement slower than the threshold (auto_explain).
    var containerLog = Path.Combine(outputDirectory, "container.log");
    await server.SaveLogsAsync(containerLog, cancellation);
    log.Info($"Plans of statements over {explainMs} ms: {containerLog}");
}

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
