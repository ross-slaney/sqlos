using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
/// Page orders at scale, on SQL Server or PostgreSQL: <c>ToPageAsync</c> against the removed
/// <c>.Where(filter).OrderBy(Price).Take(20)</c> path over a retail tree
/// (root → 5 divisions → 50 chains → 500 regions → 10,000 stores → products, one resource per product),
/// then the storage of every page-order table and the cost of writes.
/// </summary>
/// <remarks>
/// <code>
/// dotnet run -c Release --project tests/SqlOS.Benchmarks -- page-orders --provider sqlserver \
///     --connection "Server=localhost,1433;Database=SqlOSPageOrders;User Id=sa;Password=...;TrustServerCertificate=True" \
///     [--products 1000000] [--runs 15] [--budget-seconds 60]
/// </code>
/// The database named in the connection string is dropped and recreated. "Rows read" is the rows of
/// Products and of its page-order tables that the engine read, from the actual plans of every
/// statement the page ran (SET STATISTICS XML / EXPLAIN ANALYZE). Products has indexes on
/// <c>(Price, Id)</c> and <c>(StoreId, Price, Id)</c>, so it has three page orders: those two and the key.
/// </remarks>
internal static class PageOrderBenchmark
{
    private const string Permission = "product.view";
    private const string PriceTable = "SqlOSFgaPage_Products_Price";
    private const string StorePriceTable = "SqlOSFgaPage_Products_StoreId_Price";
    private const string KeyTable = "SqlOSFgaPage_Products_Id";
    private static readonly string[] EntryTables = [PriceTable, StorePriceTable, KeyTable];
    private const int PageSize = 20;
    private const int Divisions = 5;
    private const int ChainsPerDivision = 10;
    private const int RegionsPerChain = 10;
    private const int StoresPerRegion = 20;
    private const int Stores = Divisions * ChainsPerDivision * RegionsPerChain * StoresPerRegion;
    private const int Chains = Divisions * ChainsPerDivision;
    private const int ScopedStore = 5_000;

    public static async Task RunAsync(string[] args)
    {
        var options = Options.Parse(args);
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

        Console.WriteLine($"=== SqlOS page orders: {(options.PostgreSql ? "PostgreSQL" : "SQL Server")}, {products:N0} products ===");
        var setup = Stopwatch.StartNew();
        await bench.RecreateDatabaseAsync();
        await bench.CreateSchemaAsync();
        Console.WriteLine($"  schema: {setup.Elapsed.TotalSeconds:F1}s");

        var step = Stopwatch.StartNew();
        await bench.SeedTreeAsync(productsPerStore);
        Console.WriteLine($"  resources and products: {step.Elapsed.TotalSeconds:F1}s");

        step.Restart();
        var callers = await bench.SeedCallersAsync(productsPerStore);
        Console.WriteLine($"  subjects and grants: {step.Elapsed.TotalSeconds:F1}s");

        step.Restart();
        var storage = await bench.RebuildAsync();
        Console.WriteLine($"  RebuildPageOrdersAsync: {string.Join(", ", storage.Select(table => $"{table.Entries:N0} entries in {table.Table}"))} in {step.Elapsed.TotalSeconds:F1}s");

        await bench.AnalyzeAsync();
        Console.WriteLine($"  setup total: {setup.Elapsed.TotalMinutes:F1} min");
        Console.WriteLine();

        var results = new List<Result>();
        foreach (var scenario in Scenarios(callers))
        {
            var result = await bench.MeasureAsync(scenario, options);
            results.Add(result);
            Console.WriteLine(
                $"  {scenario.Name,-26} visible {result.Visible,9:N0}  ToPageAsync {result.Page.MedianMs,9:F1} ms {result.Page.RowsRead,9:N0} rows ({result.Page.Statements} statements)  removed .Where(filter).Take(20) {result.Plain.MedianMs,9:F1} ms {result.Plain.RowsRead,9:N0} rows");
        }

        Console.WriteLine();
        Console.WriteLine($"| {engine}, {products:N0} products | Visible rows | `ToPageAsync` ms | rows read | removed `.Where(filter).OrderBy(Price).Take(20)` ms | rows read |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var result in results)
        {
            Console.WriteLine(
                $"| {result.Scenario.Name} | {result.Visible:N0} | {result.Page.MedianMs:F1} | {result.Page.RowsRead:N0} | {result.Plain.MedianMs:F1} | {result.Plain.RowsRead:N0} |");
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
        Console.WriteLine($"| {engine}, writes (median ms) | no protected entity | three page orders |");
        Console.WriteLine("| --- | ---: | ---: |");
        foreach (var write in writes)
        {
            Console.WriteLine($"| {write.Name} | {write.BaselineMs:F1} | {write.IndexedMs:F1} |");
        }
    }

    private static IEnumerable<Scenario> Scenarios(Callers callers)
    {
        yield return new Scenario("Admin (root)", callers.Admin, Scoped: false);
        yield return new Scenario("Region manager (0.2%)", callers.Region, Scoped: false);
        yield return new Scenario("Store manager (0.01%)", callers.Store, Scoped: false);
        yield return new Scenario("100 stores (1%)", callers.HundredStores, Scoped: false);
        yield return new Scenario("10,000 single products", callers.TenThousandProducts, Scoped: false);
        yield return new Scenario("Far end (one chain, last in price)", callers.FarEnd, Scoped: false);
        yield return new Scenario("Scoped (store manager, StoreId)", callers.Store, Scoped: true);
    }

    private sealed record Scenario(string Name, string SubjectId, bool Scoped);

    private sealed record Measurement(double MedianMs, int Runs, long RowsRead, int Statements);

    private sealed record Result(Scenario Scenario, long Visible, Measurement Page, Measurement Plain);

    private sealed record Storage(string Table, long Entries, double Megabytes);

    private sealed record Write(string Name, double BaselineMs, double IndexedMs);

    private sealed record Callers(string Admin, string Region, string Store, string HundredStores, string TenThousandProducts, string FarEnd);

    private sealed class Bench(bool postgreSql, string connectionString, int productsPerStore)
    {
        private int _nextProduct = Stores * productsPerStore + 1;

        public BenchDbContext CreateContext(params IInterceptor[] interceptors)
            => new(Configure(new DbContextOptionsBuilder<BenchDbContext>(), interceptors).Options);

        private BaselineBenchDbContext CreateBaselineContext()
            => new(Configure(new DbContextOptionsBuilder<BaselineBenchDbContext>(), []).Options);

        private DbContextOptionsBuilder<T> Configure<T>(DbContextOptionsBuilder<T> builder, IInterceptor[] interceptors)
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

        public async Task CreateSchemaAsync()
        {
            await using var context = CreateContext();
            var fga = Microsoft.Extensions.Options.Options.Create(new SqlOSFgaOptions());
            await new SqlOSFgaSchemaInitializer(context, fga, NullLogger<SqlOSFgaSchemaInitializer>.Instance).EnsureSchemaAsync();
            await new SqlOSFgaFunctionInitializer(context, fga, NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync();

            // The application's tables and the three page-order tables, as a migration would create them
            // before the application starts and seeds.
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await new SqlOSFgaSeedService(context, fga, NullLogger<SqlOSFgaSeedService>.Instance).SeedCoreAsync();

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

        public async Task SeedTreeAsync(int productsPerStore)
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
            var farStore = (Chains - 1) * RegionsPerChain * StoresPerRegion + 1;
            var storeOf = $"(i - 1) / {productsPerStore} + 1";
            var price = $"CAST((i * 7919) % 99991 AS decimal(10,2)) / 100 + CASE WHEN {storeOf} >= {farStore} THEN 1000 ELSE 0 END";
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

        public async Task<Callers> SeedCallersAsync(int productsPerStore)
        {
            await using var context = CreateContext();
            var callers = new Callers("bench_admin", "bench_region", "bench_store", "bench_100_stores", "bench_10k_products", "bench_far_end");
            foreach (var subject in new[] { callers.Admin, callers.Region, callers.Store, callers.HundredStores, callers.TenThousandProducts, callers.FarEnd })
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

        public async Task<IReadOnlyList<Storage>> RebuildAsync()
        {
            await using var context = CreateContext();
            await AuthService(context).RebuildPageOrdersAsync();
            var storage = new List<Storage>();
            foreach (var table in EntryTables)
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

        /// <summary>
        /// What a save costs with no protected entity (no page orders at all) and with Products' three
        /// page orders: one product with its resource, a thousand in one save, and a region moved to
        /// another chain, which rewrites the entries under it.
        /// </summary>
        public async Task<IReadOnlyList<Write>> MeasureWritesAsync()
        {
            var writes = new List<Write>();

            writes.Add(new Write(
                "Insert one product with its resource (median of 25 saves)",
                await SingleInsertsAsync(() => CreateBaselineContext()),
                await SingleInsertsAsync(() => CreateContext())));

            writes.Add(new Write(
                "Insert 1,000 products with their resources (one save)",
                await BatchInsertAsync(() => CreateBaselineContext()),
                await BatchInsertAsync(() => CreateContext())));

            writes.Add(new Write(
                $"Move a region ({StoresPerRegion * productsPerStore:N0} products) to another chain (one save)",
                await MoveRegionAsync(() => CreateBaselineContext()),
                await MoveRegionAsync(() => CreateContext())));

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

            // Back through the protected context, which rewrites every table, so the next measurement
            // starts from entries equal to a rebuild.
            await using var restore = CreateContext();
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

        /// <summary>Deletes products and their resources through the protected context, which deletes every table's entries.</summary>
        private async Task RemoveProductsAsync(IReadOnlyCollection<int> ids)
        {
            await using var context = CreateContext();
            context.Products.RemoveRange(await context.Products.Where(product => ids.Contains(product.Id)).ToListAsync());
            var resourceIds = ids.Select(id => $"product:{id}").ToList();
            context.Set<SqlOSFgaResource>().RemoveRange(await context.Set<SqlOSFgaResource>().Where(resource => resourceIds.Contains(resource.Id)).ToListAsync());
            await context.SaveChangesAsync();
        }

        public async Task AnalyzeAsync()
        {
            await using var context = CreateContext();
            await context.Database.ExecuteSqlRawAsync(postgreSql ? "ANALYZE" : "EXEC sp_updatestats");
        }

        public async Task<Result> MeasureAsync(Scenario scenario, Options options)
        {
            await using var context = CreateContext();
            var removedFilter = await RemovedFilterAsync(context, scenario.SubjectId);
            var visible = await Query(context, scenario).Where(removedFilter).LongCountAsync();

            // The plan pass runs each method once first; it doubles as the warmup.
            var (pageRows, pageStatements, pageFirstRun) = await RowsReadAsync(ctx => Query(ctx, scenario).OrderBy(product => product.Price).ToPageAsync(scenario.SubjectId, Permission, PageSize));
            var page = await TimeAsync(async () =>
                (await Query(context, scenario).OrderBy(product => product.Price).ToPageAsync(scenario.SubjectId, Permission, PageSize)).Data.Count, options, pageFirstRun);

            var (plainRows, plainStatements, plainFirstRun) = await RowsReadAsync(async ctx =>
            {
                var filter = await RemovedFilterAsync(ctx, scenario.SubjectId);
                await Query(ctx, scenario).Where(filter).OrderBy(product => product.Price).ThenBy(product => product.Id).Take(PageSize).ToListAsync();
            }, skipStatements: 2);
            var plain = await TimeAsync(async () =>
                (await Query(context, scenario).Where(removedFilter).OrderBy(product => product.Price).ThenBy(product => product.Id).Take(PageSize).ToListAsync()).Count, options, plainFirstRun);

            return new Result(
                scenario,
                visible,
                page with { RowsRead = pageRows, Statements = pageStatements },
                plain with { RowsRead = plainRows, Statements = plainStatements });
        }

        private static IQueryable<BenchProduct> Query(BenchDbContext context, Scenario scenario)
        {
            IQueryable<BenchProduct> query = context.Products.AsNoTracking();
            return scenario.Scoped ? query.Where(product => product.StoreId == ScopedStore) : query;
        }

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

        /// <summary>
        /// The removed <c>.Where(filter)</c> path: the predicate the removed <c>BuildFilterAsync</c> returned,
        /// built by the internal helper <c>ToPageAsync</c> checks rows with. The caller is resolved once,
        /// outside the timing, as the old filter was.
        /// </summary>
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
        private async Task<(long Rows, int Statements, TimeSpan FirstRun)> RowsReadAsync(Func<BenchDbContext, Task> run, int skipStatements = 0)
        {
            var capture = new StatementCapture();
            await using var context = CreateContext(capture);
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
            var tables = EntryTables.Select(table => $"[{table}]").Append("[Products]").ToArray();
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
                && relation.GetString() is { } name
                && (name == "Products" || EntryTables.Contains(name)))
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

        private static SqlOSFgaAuthService AuthService(BenchDbContext context)
            => new(context, Microsoft.Extensions.Options.Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);

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

        public void Clear() => _statements.Clear();

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

    private sealed record Options(bool PostgreSql, string ConnectionString, int Products, int Runs, TimeSpan Budget)
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
                TimeSpan.FromSeconds(int.Parse(Value("--budget-seconds") ?? "60", CultureInfo.InvariantCulture)));
        }
    }
}

/// <summary>Products with its indexes: three page orders, (Price, Id), (StoreId, Price, Id) and the key.</summary>
internal sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : SqlOSDbContext<BenchDbContext>(options)
{
    public DbSet<BenchProduct> Products => Set<BenchProduct>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchProduct>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Id).ValueGeneratedNever();
            entity.Property(product => product.ResourceId).HasMaxLength(64);
            entity.Property(product => product.Name).HasMaxLength(100);
            entity.Property(product => product.Price).HasPrecision(10, 2);
            entity.HasIndex(product => product.ResourceId).IsUnique();
            // The application's own indexes for its order and for the store-scoped list; each is a page order.
            entity.HasIndex(product => new { product.Price, product.Id });
            entity.HasIndex(product => new { product.StoreId, product.Price, product.Id });
        });
    }
}

/// <summary>Products as an entity that is not protected: saves write no page-order entries.</summary>
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
}

internal sealed class BaselineBenchProduct
{
    public int Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StoreId { get; set; }
}
