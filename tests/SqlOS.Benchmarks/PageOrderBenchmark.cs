using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SqlOS;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;
using SqlOS.Fga.Services;

namespace SqlOS.Benchmarks;

/// <summary>
/// Page orders at scale, on SQL Server or PostgreSQL, over a retail tree
/// (root → 5 divisions → 50 chains → 500 regions → 10,000 stores → products, one resource per product).
/// </summary>
/// <remarks>
/// <code>
/// dotnet run -c Release --project tests/SqlOS.Benchmarks -- page-orders --provider sqlserver \
///     --connection "Server=localhost,1433;Database=SqlOSPageOrders;User Id=sa;Password=...;TrustServerCertificate=True" \
///     [--products 1000000] [--runs 15] [--budget-seconds 60] [--no-reference]
/// dotnet run -c Release --project tests/SqlOS.Benchmarks -- page-orders-compare --provider postgresql --connection "..."
/// </code>
/// <para>
/// <c>page-orders</c> pages Products by Price with no page order declared, for every caller and a
/// density sweep, against the removed <c>.Where(filter).OrderBy(Price).Take(20)</c> path.
/// <c>page-orders-compare</c> runs the same data and callers twice, with no declaration and with
/// <c>HasPageOrder(p =&gt; p.Price)</c>, and adds the storage and write cost of each.
/// </para>
/// <para>
/// The database named in the connection string is dropped and recreated. "Rows read" is the rows of
/// Products and of its page-order tables that the engine read, from the actual plans of every
/// statement the page ran (SET STATISTICS XML / EXPLAIN ANALYZE). "min(R1, V)" is the smaller of the
/// rows read 1 needs (the query's rows up to the page's last visible row) and the rows the caller
/// can see.
/// </para>
/// </remarks>
internal static class PageOrderBenchmark
{
    private const string Permission = "product.view";
    private const string NodeIndexTable = "SqlOSFgaPage_Products_Id";
    private const string PriceTable = "SqlOSFgaPage_Products_Price";
    private const int PageSize = 20;
    private const int Divisions = 5;
    private const int ChainsPerDivision = 10;
    private const int RegionsPerChain = 10;
    private const int StoresPerRegion = 20;
    private const int Stores = Divisions * ChainsPerDivision * RegionsPerChain * StoresPerRegion;
    private const int Chains = Divisions * ChainsPerDivision;
    private const int ScopedStore = 5_000;

    // The last chain's stores, whose products cost more than every other product.
    private const int FarStore = (Chains - 1) * RegionsPerChain * StoresPerRegion + 1;

    // Callers whose rows are spread through the order, as a share of the products: 0.01% to 5%, with
    // √(P/N) ≈ 0.45% the worst case for a million products.
    private static readonly (string Label, int Stores)[] Sweep =
    [
        ("0.01%", 1),
        ("0.1%", 10),
        ("0.45%", 45),
        ("1%", 100),
        ("5%", 500),
    ];

    public static Task RunAsync(string[] args) => RunAsync(Options.Parse(args), compare: false);

    public static Task CompareAsync(string[] args) => RunAsync(Options.Parse(args), compare: true);

    private static async Task RunAsync(Options options, bool compare)
    {
        if (options.PostgreSql)
        {
            // SqlOS stores UTC times in timestamp columns. Npgsql reads this switch once, when it is
            // first used, and the database is created over a plain connection before any SqlOSDbContext.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        var productsPerStore = Math.Max(1, options.Products / Stores);
        var products = productsPerStore * Stores;
        var bench = new Bench(options.PostgreSql, options.ConnectionString, productsPerStore);
        var engine = options.PostgreSql ? "PostgreSQL" : "SQL Server";

        Console.WriteLine($"=== SqlOS page orders{(compare ? ", plain vs declared" : string.Empty)}: {engine}, {products:N0} products ===");
        var setup = Stopwatch.StartNew();
        await bench.RecreateDatabaseAsync();
        await bench.CreateSchemaAsync(declared: compare);
        Console.WriteLine($"  schema: {setup.Elapsed.TotalSeconds:F1}s");

        var step = Stopwatch.StartNew();
        await bench.SeedTreeAsync();
        Console.WriteLine($"  resources and products: {step.Elapsed.TotalSeconds:F1}s");

        step.Restart();
        var callers = await bench.SeedCallersAsync();
        Console.WriteLine($"  subjects and grants: {step.Elapsed.TotalSeconds:F1}s");

        step.Restart();
        var storage = await bench.RebuildAsync(declared: compare);
        Console.WriteLine($"  RebuildPageOrdersAsync: {string.Join(", ", storage.Select(table => $"{table.Entries:N0} entries in {table.Table}"))} in {step.Elapsed.TotalSeconds:F1}s");

        await bench.AnalyzeAsync();
        Console.WriteLine($"  setup total: {setup.Elapsed.TotalMinutes:F1} min");
        Console.WriteLine();

        if (compare)
        {
            await CompareAsync(bench, callers, options, engine, products, storage);
        }
        else
        {
            await PlainAsync(bench, callers, options, engine, products);
        }
    }

    private static async Task PlainAsync(Bench bench, Callers callers, Options options, string engine, int products)
    {
        var results = new List<(Scenario Scenario, Shape Shape, Measurement Page, Measurement? Reference)>();
        foreach (var scenario in Scenarios(callers))
        {
            var shape = await bench.ShapeAsync(scenario);
            var page = await bench.MeasurePageAsync(scenario, declared: false, options);
            var reference = options.Reference ? await bench.MeasureReferenceAsync(scenario, options) : null;
            results.Add((scenario, shape, page, reference));
            Console.WriteLine(
                $"  {scenario.Name,-36} visible {shape.Visible,9:N0}  min(R1,V) {shape.Smaller,9:N0}  ToPageAsync {page.MedianMs,9:F1} ms {page.RowsRead,9:N0} rows ({page.Statements} statements, {page.Source}, {page.Rounds} rounds, {Ratio(page.RowsRead, shape.Smaller)}x)"
                + (reference == null ? string.Empty : $"  removed .Where(filter) {reference.MedianMs,9:F1} ms {reference.RowsRead,9:N0} rows"));
        }

        Console.WriteLine();
        Console.WriteLine($"| {engine}, {products:N0} products, no page order | Visible | min(R1, V) | `ToPageAsync` ms | statements | served by | rows read | rows / max(min(R1, V), 20) | removed `.Where(filter)` ms | rows read |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |");
        foreach (var (scenario, shape, page, reference) in results)
        {
            Console.WriteLine(
                $"| {scenario.Name} | {shape.Visible:N0} | {shape.Smaller:N0} | {page.MedianMs:F1} | {page.Statements} | {page.Source}, {page.Rounds} round(s) | {page.RowsRead:N0} | {Ratio(page.RowsRead, shape.Smaller)} | {(reference == null ? "-" : reference.MedianMs.ToString("F1", CultureInfo.InvariantCulture))} | {(reference == null ? "-" : reference.RowsRead.ToString("N0", CultureInfo.InvariantCulture))} |");
        }
    }

    private static async Task CompareAsync(Bench bench, Callers callers, Options options, string engine, int products, IReadOnlyList<Storage> storage)
    {
        var results = new List<(Scenario Scenario, Shape Shape, Measurement Plain, Measurement Declared)>();
        foreach (var scenario in Scenarios(callers))
        {
            var shape = await bench.ShapeAsync(scenario);
            var plain = await bench.MeasurePageAsync(scenario, declared: false, options);
            var declared = await bench.MeasurePageAsync(scenario, declared: true, options);
            results.Add((scenario, shape, plain, declared));
            Console.WriteLine(
                $"  {scenario.Name,-36} min(R1,V) {shape.Smaller,9:N0}  plain {plain.MedianMs,9:F1} ms {plain.RowsRead,9:N0} rows ({plain.Statements})  declared {declared.MedianMs,9:F1} ms {declared.RowsRead,9:N0} rows ({declared.Statements})");
        }

        Console.WriteLine();
        Console.WriteLine($"| {engine}, {products:N0} products | Visible | min(R1, V) | plain ms | statements | rows read | `HasPageOrder(p => p.Price)` ms | statements | rows read |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (scenario, shape, plain, declared) in results)
        {
            Console.WriteLine(
                $"| {scenario.Name} | {shape.Visible:N0} | {shape.Smaller:N0} | {plain.MedianMs:F1} | {plain.Statements} | {plain.RowsRead:N0} | {declared.MedianMs:F1} | {declared.Statements} | {declared.RowsRead:N0} |");
        }

        Console.WriteLine();
        Console.WriteLine($"| {engine}, storage | Entries | Size |");
        Console.WriteLine("| --- | ---: | ---: |");
        foreach (var table in storage)
        {
            Console.WriteLine($"| `{table.Table}` | {table.Entries:N0} | {table.Megabytes:N0} MB |");
        }

        Console.WriteLine();
        var writes = await bench.MeasureWritesAsync();
        Console.WriteLine($"| {engine}, writes (median ms) | no protected entity | plain (node index) | declared (node index + Price) |");
        Console.WriteLine("| --- | ---: | ---: | ---: |");
        foreach (var write in writes)
        {
            Console.WriteLine($"| {write.Name} | {write.BaselineMs:F1} | {write.PlainMs:F1} | {write.DeclaredMs:F1} |");
        }
    }

    /// <summary>Rows read per row of max(min(R1, V), P): any page reads at least a page.</summary>
    private static string Ratio(long rows, long smaller)
        => (rows / (double)Math.Max(smaller, PageSize)).ToString("F1", CultureInfo.InvariantCulture);

    private static IEnumerable<Scenario> Scenarios(Callers callers)
    {
        yield return new Scenario("Admin (root)", callers.Admin, Scoped: false);
        yield return new Scenario("Region manager (0.2%)", callers.Region, Scoped: false);
        yield return new Scenario("Store manager (0.01%)", callers.Store, Scoped: false);
        yield return new Scenario("100 stores (1%)", callers.HundredStores, Scoped: false);
        yield return new Scenario("10,000 single products", callers.TenThousandProducts, Scoped: false);
        yield return new Scenario("Far end (one chain, last in price)", callers.FarEnd, Scoped: false);
        yield return new Scenario("Scoped (store manager, StoreId)", callers.Store, Scoped: true);
        foreach (var (label, subject) in callers.Sweep)
        {
            yield return new Scenario($"Spread {label}", subject, Scoped: false);
        }
    }

    private sealed record Scenario(string Name, string SubjectId, bool Scoped);

    /// <summary>The rows the caller can see, and the smaller of that and the rows read 1 needs.</summary>
    private sealed record Shape(long Visible, long Read1Needs)
    {
        public long Smaller => Math.Min(Visible, Read1Needs);
    }

    private sealed record Measurement(double MedianMs, int Runs, long RowsRead, int Statements, string Source = "-", int Rounds = 0);

    private sealed record Storage(string Table, long Entries, double Megabytes);

    private sealed record Write(string Name, double BaselineMs, double PlainMs, double DeclaredMs);

    private sealed record Callers(
        string Admin,
        string Region,
        string Store,
        string HundredStores,
        string TenThousandProducts,
        string FarEnd,
        IReadOnlyList<(string Label, string Subject)> Sweep);

    private sealed class Bench(bool postgreSql, string connectionString, int productsPerStore)
    {
        private int _nextProduct = Stores * productsPerStore + 1;

        // One logger factory for every plan pass: EF Core builds an internal service provider (and its
        // query cache) per logger factory, so a new one per page would compile every page again.
        private readonly DebugLines _lines = new();
        private DebugLinesFactory? _linesFactory;

        public BenchDbContext CreateContext(params IInterceptor[] interceptors)
            => new(Configure(new DbContextOptionsBuilder<BenchDbContext>(), null, interceptors).Options);

        public DeclaredBenchDbContext CreateDeclaredContext(params IInterceptor[] interceptors)
            => new(Configure(new DbContextOptionsBuilder<DeclaredBenchDbContext>(), null, interceptors).Options);

        private BaselineBenchDbContext CreateBaselineContext()
            => new(Configure(new DbContextOptionsBuilder<BaselineBenchDbContext>(), null, []).Options);

        private DbContext CreatePageContext(bool declared, ILoggerFactory? loggerFactory, params IInterceptor[] interceptors)
            => declared
                ? new DeclaredBenchDbContext(Configure(new DbContextOptionsBuilder<DeclaredBenchDbContext>(), loggerFactory, interceptors).Options)
                : new BenchDbContext(Configure(new DbContextOptionsBuilder<BenchDbContext>(), loggerFactory, interceptors).Options);

        private DbContextOptionsBuilder<T> Configure<T>(DbContextOptionsBuilder<T> builder, ILoggerFactory? loggerFactory, IInterceptor[] interceptors)
            where T : DbContext
        {
            if (postgreSql)
            {
                builder.UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(3600));
            }
            else
            {
                builder.UseSqlServer(connectionString, sqlServer => sqlServer.CommandTimeout(3600));
            }

            if (loggerFactory != null)
            {
                builder.UseLoggerFactory(loggerFactory);
            }

            if (interceptors.Length > 0)
            {
                builder.AddInterceptors(interceptors);
            }

            return builder;
        }

        public async Task RecreateDatabaseAsync()
        {
            if (postgreSql)
            {
                var target = new NpgsqlConnectionStringBuilder(connectionString);
                var database = target.Database!;
                var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
                await using var connection = new NpgsqlConnection(admin.ConnectionString);
                await connection.OpenAsync();
                await ExecuteAsync(connection, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid()");
                await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{database}\"");
                await ExecuteAsync(connection, $"CREATE DATABASE \"{database}\"");
                return;
            }

            var sqlTarget = new SqlConnectionStringBuilder(connectionString);
            var sqlDatabase = sqlTarget.InitialCatalog;
            var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
            await using var sqlConnection = new SqlConnection(master.ConnectionString);
            await sqlConnection.OpenAsync();
            await ExecuteAsync(sqlConnection, $"""
                IF DB_ID(N'{sqlDatabase}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{sqlDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{sqlDatabase}];
                END
                CREATE DATABASE [{sqlDatabase}];
                """);
        }

        /// <summary>
        /// The SqlOS schema and functions, then the application's tables as a migration would create
        /// them: Products and its node index, plus the Price entry table when the order is declared.
        /// </summary>
        public async Task CreateSchemaAsync(bool declared)
        {
            await using var context = declared ? (DbContext)CreateDeclaredContext() : CreateContext();
            var fga = Microsoft.Extensions.Options.Options.Create(new SqlOSFgaOptions());
            var fgaContext = (ISqlOSFgaDbContext)context;
            await new SqlOSFgaSchemaInitializer(fgaContext, fga, NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync();
            await new SqlOSFgaFunctionInitializer(fgaContext, fga, NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync();
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await new SqlOSFgaSeedService(fgaContext, fga, NullLogger<SqlOSFgaSeedService>.Instance).SeedCoreAsync();

            foreach (var type in new[] { "division", "chain", "region", "store", "product" })
            {
                context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType { Id = type, Name = type });
            }

            context.Set<SqlOSFgaPermission>().Add(new SqlOSFgaPermission { Id = "perm_product_view", Key = Permission, Name = "View products" });
            context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole { Id = "role_viewer", Key = "viewer", Name = "Viewer" });
            await context.SaveChangesAsync();
            context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission { RoleId = "role_viewer", PermissionId = "perm_product_view" });
            await context.SaveChangesAsync();
        }

        public async Task SeedTreeAsync()
        {
            await using var context = CreateContext();
            await InsertResourcesAsync(context, "division", Divisions, parent: null, perParent: 1);
            await InsertResourcesAsync(context, "chain", Chains, "division", ChainsPerDivision);
            await InsertResourcesAsync(context, "region", Chains * RegionsPerChain, "chain", RegionsPerChain);
            await InsertResourcesAsync(context, "store", Stores, "region", StoresPerRegion);
            await InsertResourcesAsync(context, "product", Stores * productsPerStore, "store", productsPerStore);

            // Prices are spread evenly; the last chain's products cost more than every other product
            // (the far-end caller's rows sit at the end of the price order).
            var count = Stores * productsPerStore;
            var storeOf = $"(i - 1) / {productsPerStore} + 1";
            var price = $"CAST((i * 7919) % 99991 AS decimal(10,2)) / 100 + CASE WHEN {storeOf} >= {FarStore} THEN 1000 ELSE 0 END";
            await context.Database.ExecuteSqlRawAsync(postgreSql
                ? $"""
                  INSERT INTO "Products" ("Id", "ResourceId", "Name", "Price", "StoreId")
                  SELECT i, 'product:' || i, 'Product ' || i, {price.Replace("(i * 7919)", "(i::bigint * 7919)")}, {storeOf}
                  FROM generate_series(1, {count}) AS i
                  """
                : $"""
                  WITH n AS (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
                  INSERT INTO [Products] ([Id], [ResourceId], [Name], [Price], [StoreId])
                  SELECT i, CONCAT('product:', i), CONCAT('Product ', i), {price}, {storeOf}
                  FROM n
                  """);
        }

        private Task InsertResourcesAsync(BenchDbContext context, string type, int count, string? parent, int perParent)
        {
            var parentId = parent == null
                ? "'root'"
                : postgreSql ? $"'{parent}:' || ((i - 1) / {perParent} + 1)" : $"CONCAT('{parent}:', (i - 1) / {perParent} + 1)";
            return context.Database.ExecuteSqlRawAsync(postgreSql
                ? $"""
                  INSERT INTO "dbo"."SqlOSFgaResources" ("Id", "ParentId", "Name", "ResourceTypeId", "IsActive", "CreatedAt", "UpdatedAt")
                  SELECT '{type}:' || i, {parentId}, '{type} ' || i, '{type}', TRUE, now() AT TIME ZONE 'utc', now() AT TIME ZONE 'utc'
                  FROM generate_series(1, {count}) AS i
                  """
                : $"""
                  WITH n AS (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
                  INSERT INTO [dbo].[SqlOSFgaResources] ([Id], [ParentId], [Name], [ResourceTypeId], [IsActive], [CreatedAt], [UpdatedAt])
                  SELECT CONCAT('{type}:', i), {parentId}, CONCAT('{type} ', i), '{type}', 1, SYSUTCDATETIME(), SYSUTCDATETIME()
                  FROM n
                  """);
        }

        public async Task<Callers> SeedCallersAsync()
        {
            await using var context = CreateContext();
            var sweep = Sweep.Select(density => (density.Label, Subject: $"bench_spread_{density.Stores}")).ToList();
            var callers = new Callers("bench_admin", "bench_region", "bench_store", "bench_100_stores", "bench_10k_products", "bench_far_end", sweep);
            foreach (var subject in new[] { callers.Admin, callers.Region, callers.Store, callers.HundredStores, callers.TenThousandProducts, callers.FarEnd }
                         .Concat(sweep.Select(item => item.Subject)))
            {
                context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = subject, SubjectTypeId = "user", DisplayName = subject });
                context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = $"usr_{subject}", SubjectId = subject, IsActive = true });
            }

            await context.SaveChangesAsync();

            var grants = new List<(string Subject, string Resource)>
            {
                (callers.Admin, "root"),
                (callers.Region, $"region:{Chains * RegionsPerChain / 2}"),
                (callers.Store, $"store:{ScopedStore}"),
                (callers.FarEnd, $"chain:{Chains}"),
            };
            grants.AddRange(Enumerable.Range(0, 100).Select(index => (callers.HundredStores, $"store:{index * (Stores / 100) + 1}")));
            var products = Stores * productsPerStore;
            var stride = Math.Max(1, products / 10_000);
            grants.AddRange(Enumerable.Range(0, Math.Min(10_000, products)).Select(index => (callers.TenThousandProducts, $"product:{index * stride + 1}")));

            // The sweep's stores are spaced evenly through the chains before the last, whose prices are spread.
            foreach (var ((_, subject), (_, stores)) in sweep.Zip(Sweep))
            {
                var spacing = (FarStore - 1) / stores;
                grants.AddRange(Enumerable.Range(0, stores).Select(index => (subject, $"store:{index * spacing + 1}")));
            }

            var number = 0;
            foreach (var (subject, resource) in grants)
            {
                context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
                {
                    Id = $"grant_bench_{number++}",
                    SubjectId = subject,
                    ResourceId = resource,
                    RoleId = "role_viewer"
                });
            }

            await context.SaveChangesAsync();
            return callers;
        }

        public async Task<IReadOnlyList<Storage>> RebuildAsync(bool declared)
        {
            await using var context = declared ? (DbContext)CreateDeclaredContext() : CreateContext();
            await AuthService(context).RebuildPageOrdersAsync();
            var storage = new List<Storage>();
            foreach (var table in declared ? new[] { NodeIndexTable, PriceTable } : [NodeIndexTable])
            {
                var entries = await context.Set<Dictionary<string, object>>(table).LongCountAsync();
                var megabytes = (await context.Database.SqlQueryRaw<long>(postgreSql
                        ? $"SELECT pg_total_relation_size('dbo.\"{table}\"') AS \"Value\""
                        : $"SELECT CAST(SUM(a.total_pages) * 8 * 1024 AS bigint) AS [Value] FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id WHERE p.object_id = OBJECT_ID(N'dbo.{table}')")
                    .ToListAsync()).Single() / 1024.0 / 1024.0;
                storage.Add(new Storage(table, entries, megabytes));
            }

            return storage;
        }

        public async Task AnalyzeAsync()
        {
            await using var context = CreateContext();
            await context.Database.ExecuteSqlRawAsync(postgreSql ? "ANALYZE" : "EXEC sp_updatestats");
        }

        /// <summary>
        /// The caller's visible rows (its node index rows, with the scope), and the rows read 1 needs:
        /// the query's rows in price order up to the caller's 20th row, or all of them.
        /// </summary>
        public async Task<Shape> ShapeAsync(Scenario scenario)
        {
            await using var context = CreateContext();
            var caller = await SqlOSFgaPrincipals.ResolveCallerAsync(context, scenario.SubjectId, Permission, NullLogger.Instance)
                ?? throw new InvalidOperationException($"{scenario.SubjectId} has no {Permission} permission.");
            var keys = context.PageNodes(caller.SubjectIds, caller.PermissionId)
                .Join(
                    context.Set<Dictionary<string, object>>(NodeIndexTable),
                    node => node.NodeId,
                    entry => EF.Property<string>(entry, "NodeId"),
                    (_, entry) => EF.Property<int>(entry, "RowKey"));
            var rows = Query(context, scenario).Where(product => keys.Contains(product.Id));
            var visible = await rows.LongCountAsync();
            var last = await rows.OrderBy(product => product.Price).ThenBy(product => product.Id)
                .Skip(PageSize - 1)
                .Select(product => new { product.Price, product.Id })
                .FirstOrDefaultAsync();
            var read1Needs = last == null
                ? await Query(context, scenario).LongCountAsync()
                : await Query(context, scenario).LongCountAsync(product => product.Price < last.Price || (product.Price == last.Price && product.Id <= last.Id));
            return new Shape(visible, read1Needs);
        }

        public async Task<Measurement> MeasurePageAsync(Scenario scenario, bool declared, Options options)
        {
            // The plan pass runs the page once first; it doubles as the warmup.
            _lines.Clear();
            var (rows, statements, firstRun) = await RowsReadAsync(
                context => Page(context, scenario).ToPageAsync(scenario.SubjectId, Permission, PageSize),
                declared,
                logs: true);
            var line = _lines.Lines.Last();
            var source = Regex.Match(line, "page from (read \\d);").Groups[1].Value;
            var rounds = int.Parse(Regex.Match(line, "(\\d+) round\\(s\\)").Groups[1].Value, CultureInfo.InvariantCulture);

            await using var context = CreatePageContext(declared, null);
            var timed = await TimeAsync(
                async () => (await Page(context, scenario).ToPageAsync(scenario.SubjectId, Permission, PageSize)).Data.Count,
                options,
                firstRun);
            return timed with { RowsRead = rows, Statements = statements, Source = source, Rounds = rounds };
        }

        /// <summary>
        /// The removed <c>.Where(filter)</c> path: the predicate the removed <c>BuildFilterAsync</c> returned,
        /// built by the internal helper <c>ToPageAsync</c> checks rows with. The caller is resolved once,
        /// outside the timing, as the old filter was.
        /// </summary>
        public async Task<Measurement> MeasureReferenceAsync(Scenario scenario, Options options)
        {
            var (rows, statements, firstRun) = await RowsReadAsync(async context =>
            {
                var filter = await RemovedFilterAsync((BenchDbContext)context, scenario.SubjectId);
                await Query((BenchDbContext)context, scenario).Where(filter).OrderBy(product => product.Price).ThenBy(product => product.Id).Take(PageSize).ToListAsync();
            }, declared: false, logs: false, skipStatements: 2);

            await using var timing = CreateContext();
            var removedFilter = await RemovedFilterAsync(timing, scenario.SubjectId);
            var timed = await TimeAsync(
                async () => (await Query(timing, scenario).Where(removedFilter).OrderBy(product => product.Price).ThenBy(product => product.Id).Take(PageSize).ToListAsync()).Count,
                options,
                firstRun);
            return timed with { RowsRead = rows, Statements = statements };
        }

        /// <summary>
        /// What a save costs with no protected entity (the cost before node indexes), with the node
        /// index alone, and with the node index and the declared Price order.
        /// </summary>
        public async Task<IReadOnlyList<Write>> MeasureWritesAsync()
        {
            var writes = new List<Write>();

            // One product, with its own resource, per save.
            writes.Add(new Write(
                "Insert one product with its resource (median of 25 saves)",
                await SingleInsertsAsync(() => CreateBaselineContext()),
                await SingleInsertsAsync(() => CreateContext()),
                await SingleInsertsAsync(() => CreateDeclaredContext())));

            // A thousand products, with their resources, in one save.
            writes.Add(new Write(
                "Insert 1,000 products with their resources (one save)",
                await BatchInsertAsync(() => CreateBaselineContext()),
                await BatchInsertAsync(() => CreateContext()),
                await BatchInsertAsync(() => CreateDeclaredContext())));

            // Moving a region (20 stores, their products) to another chain rewrites the entries under it.
            writes.Add(new Write(
                $"Move a region ({StoresPerRegion * productsPerStore:N0} products) to another chain (one save)",
                await MoveRegionAsync(() => CreateBaselineContext()),
                await MoveRegionAsync(() => CreateContext()),
                await MoveRegionAsync(() => CreateDeclaredContext())));

            return writes;
        }

        private async Task<double> SingleInsertsAsync(Func<DbContext> create)
        {
            var times = new List<double>();
            var added = new List<int>();
            for (var run = 0; run < 26; run++)
            {
                await using var context = create();
                var id = _nextProduct++;
                AddProduct(context, id, $"store:{run * 97 + 1}");
                var timer = Stopwatch.StartNew();
                await context.SaveChangesAsync();
                timer.Stop();
                added.Add(id);
                if (run > 0)
                {
                    times.Add(timer.Elapsed.TotalMilliseconds);
                }
            }

            await RemoveProductsAsync(added);
            return Median(times);
        }

        private async Task<double> BatchInsertAsync(Func<DbContext> create)
        {
            await using var context = create();
            var added = Enumerable.Range(0, 1_000).Select(_ => _nextProduct++).ToList();
            foreach (var (id, index) in added.Select((id, index) => (id, index)))
            {
                AddProduct(context, id, $"store:{index % 1_000 * 7 + 1}");
            }

            var timer = Stopwatch.StartNew();
            await context.SaveChangesAsync();
            var elapsed = timer.Elapsed.TotalMilliseconds;
            await RemoveProductsAsync(added);
            return elapsed;
        }

        private async Task<double> MoveRegionAsync(Func<DbContext> create)
        {
            const string region = "region:123";
            double elapsed;
            string originalParent;
            await using (var context = create())
            {
                var resource = await context.Set<SqlOSFgaResource>().SingleAsync(candidate => candidate.Id == region);
                originalParent = resource.ParentId!;
                resource.ParentId = "chain:7";
                var timer = Stopwatch.StartNew();
                await context.SaveChangesAsync();
                elapsed = timer.Elapsed.TotalMilliseconds;
            }

            // Back through the declared context, which rewrites every table, so the next measurement
            // starts from entries equal to a rebuild.
            await using var restore = CreateDeclaredContext();
            var moved = await restore.Set<SqlOSFgaResource>().SingleAsync(candidate => candidate.Id == region);
            moved.ParentId = originalParent;
            await restore.SaveChangesAsync();
            return elapsed;
        }

        private static void AddProduct(DbContext context, int id, string store)
        {
            context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = $"product:{id}",
                ParentId = store,
                Name = $"Product {id}",
                ResourceTypeId = "product"
            });
            var storeId = int.Parse(store["store:".Length..], CultureInfo.InvariantCulture);
            if (context is BaselineBenchDbContext)
            {
                context.Add(new BaselineBenchProduct { Id = id, ResourceId = $"product:{id}", Name = $"Product {id}", Price = 1, StoreId = storeId });
            }
            else
            {
                context.Add(new BenchProduct { Id = id, ResourceId = $"product:{id}", Name = $"Product {id}", Price = 1, StoreId = storeId });
            }
        }

        /// <summary>Deletes products and their resources through the declared context, which deletes every table's entries.</summary>
        private async Task RemoveProductsAsync(IReadOnlyCollection<int> ids)
        {
            await using var context = CreateDeclaredContext();
            context.Products.RemoveRange(await context.Products.Where(product => ids.Contains(product.Id)).ToListAsync());
            var resourceIds = ids.Select(id => $"product:{id}").ToList();
            context.Set<SqlOSFgaResource>().RemoveRange(await context.Set<SqlOSFgaResource>().Where(resource => resourceIds.Contains(resource.Id)).ToListAsync());
            await context.SaveChangesAsync();
        }

        private static IQueryable<BenchProduct> Query(DbContext context, Scenario scenario)
        {
            IQueryable<BenchProduct> query = context.Set<BenchProduct>().AsNoTracking();
            return scenario.Scoped ? query.Where(product => product.StoreId == ScopedStore) : query;
        }

        private static IOrderedQueryable<BenchProduct> Page(DbContext context, Scenario scenario)
            => Query(context, scenario).OrderBy(product => product.Price);

        private static async Task<Measurement> TimeAsync(Func<Task<int>> run, Options options, TimeSpan firstRun)
        {
            // A run longer than a minute (the old scan for a caller who sees little of a large table)
            // is not repeated: the plan pass's own run is the measurement.
            if (firstRun > TimeSpan.FromMinutes(1))
            {
                return new Measurement(firstRun.TotalMilliseconds, 1, 0, 0);
            }

            if (firstRun < TimeSpan.FromSeconds(2))
            {
                await run();
            }

            // Runs slower than 30 seconds (the old scan for callers who see little) are measured once.
            var minimumRuns = firstRun > TimeSpan.FromSeconds(30) ? 1 : 3;
            var times = new List<double>();
            var budget = Stopwatch.StartNew();
            while (times.Count < options.Runs && (times.Count < minimumRuns || budget.Elapsed < options.Budget))
            {
                var timer = Stopwatch.StartNew();
                await run();
                times.Add(timer.Elapsed.TotalMilliseconds);
            }

            return new Measurement(Median(times), times.Count, 0, 0);
        }

        private static double Median(List<double> times)
        {
            times.Sort();
            return times.Count % 2 == 1
                ? times[times.Count / 2]
                : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
        }

        private static async Task<System.Linq.Expressions.Expression<Func<BenchProduct, bool>>> RemovedFilterAsync(BenchDbContext context, string subjectId)
        {
            var caller = await SqlOSFgaPrincipals.ResolveCallerAsync(context, subjectId, Permission, NullLogger.Instance)
                ?? throw new InvalidOperationException($"{subjectId} has no {Permission} permission.");
            return SqlOSFgaVisibility.Predicate<BenchProduct>(context, caller.SubjectIds, caller.PermissionId);
        }

        /// <summary>
        /// Runs once, capturing every statement, and re-runs each with the engine's actual plan. The first
        /// <paramref name="skipStatements"/> statements (resolving the caller outside the timing) are left out.
        /// </summary>
        private async Task<(long Rows, int Statements, TimeSpan FirstRun)> RowsReadAsync(
            Func<DbContext, Task> run,
            bool declared,
            bool logs,
            int skipStatements = 0)
        {
            var capture = new StatementCapture();
            await using var context = CreatePageContext(declared, logs ? _linesFactory ??= new DebugLinesFactory(_lines) : null, capture);
            var firstRun = Stopwatch.StartNew();
            await run(context);
            firstRun.Stop();

            var statements = capture.Statements.Skip(skipStatements).ToList();
            var rows = 0L;
            foreach (var statement in statements)
            {
                rows += await PlanRowsAsync(statement);
            }

            return (rows, statements.Count, firstRun.Elapsed);
        }

        private async Task<long> PlanRowsAsync(DbCommand captured)
        {
            if (postgreSql)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var explain = connection.CreateCommand();
                explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + captured.CommandText;
                explain.CommandTimeout = 3600;
                CopyParameters(captured, explain);
                var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
                return PostgreSqlRows(plan.RootElement[0].GetProperty("Plan"));
            }

            await using var sqlConnection = new SqlConnection(connectionString);
            await sqlConnection.OpenAsync();
            await ExecuteAsync(sqlConnection, "SET STATISTICS XML ON");
            await using var command = sqlConnection.CreateCommand();
            command.CommandText = captured.CommandText;
            command.CommandTimeout = 3600;
            CopyParameters(captured, command);
            var total = 0L;
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
                while (await reader.ReadAsync())
                {
                    if (isPlan)
                    {
                        total += SqlServerRows(XDocument.Parse(reader.GetString(0)));
                    }
                }
            }
            while (await reader.NextResultAsync());

            return total;
        }

        private static long SqlServerRows(XDocument plan)
        {
            XNamespace showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var tables = new[] { "[Products]", $"[{NodeIndexTable}]", $"[{PriceTable}]" };
            return plan.Descendants(showplan + "RelOp")
                .Where(relOp => relOp.Elements()
                    .Any(@operator => @operator.Elements(showplan + "Object")
                        .Any(target => tables.Contains((string?)target.Attribute("Table")))))
                .SelectMany(relOp => relOp.Elements(showplan + "RunTimeInformation").Elements(showplan + "RunTimeCountersPerThread"))
                .Sum(counter => (long?)counter.Attribute("ActualRows") ?? 0);
        }

        private static long PostgreSqlRows(JsonElement node)
        {
            var rows = 0L;
            if (node.TryGetProperty("Relation Name", out var relation)
                && relation.GetString() is "Products" or NodeIndexTable or PriceTable)
            {
                // A scan's "Actual Rows" are the rows that passed its filter; the authorization check
                // is often that filter, so the rows it removed were read too.
                var read = node.GetProperty("Actual Rows").GetDouble()
                    + (node.TryGetProperty("Rows Removed by Filter", out var filtered) ? filtered.GetDouble() : 0)
                    + (node.TryGetProperty("Rows Removed by Index Recheck", out var rechecked) ? rechecked.GetDouble() : 0);
                rows += (long)Math.Round(read * node.GetProperty("Actual Loops").GetDouble());
            }

            if (node.TryGetProperty("Plans", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    rows += PostgreSqlRows(child);
                }
            }

            return rows;
        }

        private static void CopyParameters(DbCommand from, DbCommand to)
        {
            foreach (DbParameter parameter in from.Parameters)
            {
                to.Parameters.Add((DbParameter)((ICloneable)parameter).Clone());
            }
        }

        private static SqlOSFgaAuthService AuthService(DbContext context)
            => new((ISqlOSFgaDbContext)context, Microsoft.Extensions.Options.Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);

        private static async Task ExecuteAsync(DbConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 3600;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Keeps a copy of every reader command a context runs.</summary>
    private sealed class StatementCapture : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<DbCommand> _statements = new();

        public IReadOnlyList<DbCommand> Statements => _statements.ToList();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var copy = command switch
            {
                SqlCommand sql => (DbCommand)sql.Clone(),
                NpgsqlCommand npgsql => npgsql.Clone(),
                _ => throw new NotSupportedException(command.GetType().Name)
            };
            _statements.Enqueue(copy);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>The Debug lines <c>ToPageAsync</c> logs under <c>SqlOS.Fga</c>: which read served the page, in how many rounds.</summary>
    private sealed class DebugLines
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines.ToList();

        public void Add(string line) => _lines.Enqueue(line);

        public void Clear() => _lines.Clear();
    }

    private sealed class DebugLinesFactory(DebugLines lines) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName == "SqlOS.Fga" ? lines : null);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(DebugLines? lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => lines != null && logLevel == LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lines!.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed record Options(bool PostgreSql, string ConnectionString, int Products, int Runs, TimeSpan Budget, bool Reference)
    {
        public static Options Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            var provider = Value("--provider") ?? "sqlserver";
            var connection = Value("--connection") ?? throw new ArgumentException("Pass --connection \"<connection string with a database name>\".");
            return new Options(
                provider.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("npgsql", StringComparison.OrdinalIgnoreCase),
                connection,
                int.Parse(Value("--products") ?? "1000000", CultureInfo.InvariantCulture),
                int.Parse(Value("--runs") ?? "15", CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(int.Parse(Value("--budget-seconds") ?? "60", CultureInfo.InvariantCulture)),
                !args.Contains("--no-reference"));
        }
    }
}

/// <summary>Products with no page order declared: pages by Price read through the node index.</summary>
internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : SqlOSDbContext<BenchDbContext>(options)
{
    public DbSet<BenchProduct> Products => Set<BenchProduct>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<BenchProduct>(BenchProduct.Configure);
}

/// <summary>The same Products with <c>HasPageOrder(p =&gt; p.Price)</c>.</summary>
internal sealed class DeclaredBenchDbContext(DbContextOptions<DeclaredBenchDbContext> options) : SqlOSDbContext<DeclaredBenchDbContext>(options)
{
    public DbSet<BenchProduct> Products => Set<BenchProduct>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<BenchProduct>(entity =>
        {
            BenchProduct.Configure(entity);
            entity.HasPageOrder(product => product.Price);
        });
}

/// <summary>Products as an entity that is not protected: saves without node index or page-order entries.</summary>
internal sealed class BaselineBenchDbContext(DbContextOptions<BaselineBenchDbContext> options) : SqlOSDbContext<BaselineBenchDbContext>(options)
{
    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<BaselineBenchProduct>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Id).ValueGeneratedNever();
            entity.Property(product => product.ResourceId).HasMaxLength(64);
            entity.Property(product => product.Name).HasMaxLength(100);
            entity.Property(product => product.Price).HasPrecision(10, 2);
        });
}

internal sealed class BenchProduct : IHasResourceId
{
    public int Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StoreId { get; set; }

    public static void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<BenchProduct> entity)
    {
        entity.ToTable("Products");
        entity.HasKey(product => product.Id);
        entity.Property(product => product.Id).ValueGeneratedNever();
        entity.Property(product => product.ResourceId).HasMaxLength(64);
        entity.Property(product => product.Name).HasMaxLength(100);
        entity.Property(product => product.Price).HasPrecision(10, 2);
        entity.HasIndex(product => product.ResourceId).IsUnique();
        // The application's own indexes for its order and for the store-scoped list.
        entity.HasIndex(product => new { product.Price, product.Id });
        entity.HasIndex(product => new { product.StoreId, product.Price, product.Id });
    }
}

internal sealed class BaselineBenchProduct
{
    public int Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StoreId { get; set; }
}
