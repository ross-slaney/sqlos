using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// Exercise the ordinary application contract and live maintenance, on both providers.
/// Expected authorization comes from the original TVF through a raw command, so the
/// optimized EF path cannot accidentally serve as its own authorization oracle.
/// </summary>
[TestClass]
public sealed class SqlOSFgaNativeIndexIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow(2000)]
    [DataRow(10000)]
    public async Task SparsePages_SeekTheirScopeWithoutReadingUnrelatedApplicationRows(int defaultScale)
    {
        var unrelated = defaultScale;
        if (defaultScale == 10000 && int.TryParse(Environment.GetEnvironmentVariable("SQLOS_NATIVE_SCALE"), out var requested))
        {
            Assert.IsTrue(requested >= 2000 && requested <= 1_000_000, "SQLOS_NATIVE_SCALE must be between 2,000 and 1,000,000.");
            unrelated = requested;
        }
        await using var fixture = await NativeFixture.CreateAsync();
        await fixture.SeedScaleAsync(unrelated);
        var firstVisible = unrelated + 101;
        foreach (var (shape, cursor, ceiling, expected) in new[]
        {
            ("first", 0, int.MaxValue, Enumerable.Range(firstVisible, 20).ToArray()),
            ("later", firstVisible + 19, int.MaxValue, Enumerable.Range(firstVisible + 20, 20).ToArray()),
            ("empty", 0, firstVisible, Array.Empty<int>()),
            ("exhausted", firstVisible + 64, int.MaxValue, Array.Empty<int>())
        })
        {
            var filter = await fixture.Auth.BuildFilterAsync<NativeRecord>("scale-caller", "native.view");
            fixture.Commands.Clear();
            var rows = await fixture.Context.Records.Where(filter).Where(x => x.Id > cursor && x.Id < ceiling)
                .OrderBy(x => x.Id).Take(20).Select(x => x.Id).ToArrayAsync();
            CollectionAssert.AreEqual(expected, rows);
            AssertNativePageExecuted(fixture);
            var statement = fixture.Commands.Statements.Single(x => x.Sql.Contains("SqlOSFgaScopeIndex_", StringComparison.Ordinal));
            var (metrics, plan) = await CaptureActualPlanAsync(fixture.Context, statement);
            TestContext?.WriteLine($"{fixture.Context.Database.ProviderName}; unrelated={unrelated}; {shape}; " +
                $"projection rows read={metrics.ScopeRowsRead}; application rows read={metrics.ApplicationRowsRead}; scope seek={metrics.ScopeSeek}");
            if (!string.IsNullOrEmpty(TestContext?.ResultsDirectory))
            {
                var filename = $"native-sparse-{(TestDatabase.IsPostgreSql ? "pg" : "sql")}-{unrelated}-{shape}." +
                    (TestDatabase.IsPostgreSql ? "json" : "xml");
                Directory.CreateDirectory(TestContext.ResultsDirectory);
                var path = Path.Combine(TestContext.ResultsDirectory, filename);
                await File.WriteAllTextAsync(path, plan);
                TestContext.AddResultFile(path);
            }
            Assert.IsTrue(metrics.ScopeSeek, "The actual plan must seek a projection index using _AncestorKey.");
            // This allows the typed/untyped branch alternatives and an extra key
            // lookup while staying independent of the unrelated population.
            Assert.IsTrue(metrics.ScopeRowsRead <= 80,
                $"{shape}: read {metrics.ScopeRowsRead} projection rows for a page of 20 among {unrelated} unrelated rows.");
            Assert.IsTrue(metrics.ApplicationRowsRead <= 80,
                $"{shape}: read {metrics.ApplicationRowsRead} application rows for a page of 20 among {unrelated} unrelated rows.");
        }
    }

    [TestMethod]
    public async Task OrdinaryQueries_MatchOriginalAuthorizationAcrossGrantShapesAndNativeOrders()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        foreach (var caller in new[] { "dense", "workspace", "direct", "many", "overlap", "none" })
        {
            await AssertPageAsync(fixture, caller, q => q.OrderBy(x => x.Id).Take(7));
            await AssertPageAsync(fixture, caller, q => q.Where(x => x.Id > 12).OrderBy(x => x.Id).Take(7));
            await AssertPageAsync(fixture, caller, q => q.Where(x => x.Flag || x.Rank == null)
                .OrderBy(x => x.Title).ThenByDescending(x => x.Rank).ThenBy(x => x.Id).Take(7));
            await AssertPageAsync(fixture, caller, q => q.Where(x => x.Title == "missing")
                .OrderBy(x => x.Rank).ThenBy(x => x.Id).Take(7));
        }

        CollectionAssert.AreEqual(new[] { 24 }, await fixture.Context.Records
            .Where(await fixture.Auth.BuildFilterAsync<NativeRecord>("direct", "native.view"))
            .OrderBy(x => x.Id).Take(20).Select(x => x.Id).ToArrayAsync());
        Assert.AreEqual(24, (await fixture.OracleIdsAsync("overlap")).Length,
            "Overlapping root, workspace and direct grants must not duplicate rows.");
    }

    [TestMethod]
    public async Task ApplicationWrites_KeepNativeQueriesCurrentWithoutApplicationHelperColumns()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var context = fixture.Context;
        context.Set<SqlOSFgaResource>().Add(Resource("record25", "workspaceA"));
        await context.SaveChangesAsync();
        var added = new NativeRecord { Id = 25, ResourceId = "record25", Title = "Aardvark", Rank = null, Flag = true };
        context.Records.Add(added);
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "workspace", q => q.Where(x => x.Flag)
            .OrderBy(x => x.Title).ThenByDescending(x => x.Rank).ThenBy(x => x.Id).Take(20));

        added.Title = "Zulu";
        added.Rank = -10;
        added.Flag = false;
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "workspace", q => q.OrderBy(x => x.Rank).ThenBy(x => x.Id).Take(20));

        // An attached application row can be reassigned to another resource.
        added.ResourceId = "record24";
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "workspace", q => q.OrderBy(x => x.Id).Take(30));
        await AssertPageAsync(fixture, "direct", q => q.OrderBy(x => x.Id).Take(30));
        CollectionAssert.AreEqual(new[] { 24, 25 }, await context.Records
            .Where(await fixture.Auth.BuildFilterAsync<NativeRecord>("direct", "native.view"))
            .OrderBy(x => x.Id).Take(30).Select(x => x.Id).ToArrayAsync());

        context.Records.Remove(added);
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "direct", q => q.OrderBy(x => x.Id).Take(30));

        var columns = await fixture.ReadStringsAsync("""
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = 'NativeRecords'
            ORDER BY column_name
            """);
        CollectionAssert.AreEquivalent(new[] { "Id", "ResourceId", "Title", "Rank", "Flag" }, columns);
        var applicationIndexes = await fixture.ReadStringsAsync(TestDatabase.IsPostgreSql
            ? "SELECT indexname FROM pg_indexes WHERE schemaname = 'dbo' AND tablename = 'NativeRecords'"
            : "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.NativeRecords') AND name IS NOT NULL");
        Assert.AreEqual(3, applicationIndexes.Length, "Only the application primary key and its two declared sort indexes may exist.");
        Assert.IsFalse(applicationIndexes.Any(x => x.Contains("ResourceId", StringComparison.Ordinal)),
            "The application table deliberately has no ResourceId index; SqlOS must keep its lookup structures on managed tables.");
    }

    [TestMethod]
    public async Task NativeIndex_PreservesFullLengthPrincipalAndResourceIdentifiers()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var caller = "u" + new string('x', 449);
        var resourceId = "r" + new string('y', 449);
        fixture.Context.Add(new SqlOSFgaSubject { Id = caller, SubjectTypeId = "user", DisplayName = "Long identifier" });
        fixture.Context.Add(new SqlOSFgaUser { Id = "long-user-extension", SubjectId = caller, IsActive = true });
        fixture.Context.Add(Resource(resourceId, "workspaceA"));
        fixture.Context.Records.Add(new NativeRecord { Id = 25, ResourceId = resourceId, Title = "Long identifiers", Rank = 1 });
        fixture.Context.Add(Grant("g" + new string('z', 449), caller, resourceId));
        await fixture.Context.SaveChangesAsync();

        // The old SQL TVF declares its resource argument at 128 characters, although
        // resource IDs in the schema allow 450. Use the independent point check here.
        Assert.IsTrue((await fixture.Auth.CheckAccessAsync(caller, "native.view", resourceId)).Allowed);
        var filter = await fixture.Auth.BuildFilterAsync<NativeRecord>(caller, "native.view");
        fixture.Commands.Clear();
        CollectionAssert.AreEqual(new[] { 25 }, await fixture.Context.Records.Where(filter)
            .OrderBy(x => x.Id).Take(20).Select(x => x.Id).ToArrayAsync());
        AssertNativePageExecuted(fixture);
    }

    [TestMethod]
    public async Task ResourceEntity_SaveChangesCreatesUpdatesAndDeletesItsResourceWithoutTriggerConfiguration()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var context = fixture.Context;
        var ownedModel = context.Model.FindEntityType(typeof(NativeOwnedRecord))!;
        CollectionAssert.AreEquivalent(new[] { "Id", "ResourceId", "Title" },
            ownedModel.GetProperties().Select(property => property.Name).ToArray());
        Assert.IsTrue(ownedModel.GetForeignKeys().Any(fk => fk.PrincipalEntityType.ClrType == typeof(SqlOSFgaResource)
            && fk.Properties.Count == 1 && fk.Properties[0].Name == nameof(IHasResourceId.ResourceId)),
            "The interface alone must establish SaveChanges ordering through its existing ResourceId column.");
        Assert.IsFalse(ownedModel.GetIndexes().Any(index => index.Properties.Count == 1
            && index.Properties[0].Name == nameof(IHasResourceId.ResourceId)),
            "The automatic relationship must not add an application ResourceId index.");
        var row = new NativeOwnedRecord { Id = 1, ResourceId = "owned-resource", Title = "Created" };
        context.OwnedRecords.Add(row);
        await context.SaveChangesAsync();
        Assert.IsTrue(await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == row.ResourceId));

        var filter = await fixture.Auth.BuildFilterAsync<NativeOwnedRecord>("dense", "native.view");
        fixture.Commands.Clear();
        CollectionAssert.AreEqual(new[] { "Created" }, await context.OwnedRecords.Where(filter)
            .OrderBy(x => x.Title).ThenBy(x => x.Id).Take(20).Select(x => x.Title).ToArrayAsync());
        AssertNativePageExecuted(fixture);

        row.Title = "Renamed";
        await context.SaveChangesAsync();
        Assert.AreEqual("Renamed", await context.Set<SqlOSFgaResource>()
            .Where(x => x.Id == row.ResourceId).Select(x => x.Name).SingleAsync());
        fixture.Commands.Clear();
        CollectionAssert.AreEqual(new[] { "Renamed" }, await context.OwnedRecords.Where(filter)
            .OrderBy(x => x.Title).ThenBy(x => x.Id).Take(20).Select(x => x.Title).ToArrayAsync());
        AssertNativePageExecuted(fixture);

        context.OwnedRecords.Remove(row);
        await context.SaveChangesAsync();
        Assert.IsFalse(await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == row.ResourceId));
        fixture.Commands.Clear();
        Assert.AreEqual(0, (await context.OwnedRecords.Where(filter).OrderBy(x => x.Id).Take(20)
            .Select(x => x.Id).ToArrayAsync()).Length);
        AssertNativePageExecuted(fixture);
    }

    [TestMethod]
    public async Task ResourceMovesTypesAndInactiveBarriers_UpdateOnlyTheirCurrentVisibility()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var context = fixture.Context;
        context.Set<SqlOSFgaResource>().Add(Resource("inner", "workspaceA", "workspace"));
        await context.SaveChangesAsync();
        var first = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "record1");
        first.ParentId = "inner";
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "workspace", q => q.OrderBy(x => x.Id).Take(30));

        var inner = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "inner");
        inner.ParentId = "workspaceB";
        await context.SaveChangesAsync();
        Assert.IsFalse((await fixture.OracleIdsAsync("workspace")).Contains(1));
        await AssertPageAsync(fixture, "workspace", q => q.OrderBy(x => x.Id).Take(30));
        await AssertPageAsync(fixture, "many", q => q.OrderBy(x => x.Id).Take(30));

        var second = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "record2");
        second.ResourceTypeId = "other";
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "dense", q => q.OrderBy(x => x.Id).Take(30));
        Assert.IsFalse((await fixture.OracleIdsAsync("dense")).Contains(2));
        second.ResourceTypeId = "record";
        await context.SaveChangesAsync();

        inner.ParentId = "workspaceA";
        context.Set<SqlOSFgaGrant>().Add(Grant("local-below-barrier", "direct", "inner"));
        var workspace = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspaceA");
        workspace.IsActive = false;
        await context.SaveChangesAsync();
        CollectionAssert.AreEqual(new[] { 1, 24 }, await fixture.OracleIdsAsync("direct"));
        await AssertPageAsync(fixture, "direct", q => q.OrderBy(x => x.Id).Take(30));
        await AssertPageAsync(fixture, "dense", q => q.OrderBy(x => x.Id).Take(30));
        await AssertPageAsync(fixture, "workspace", q => q.OrderBy(x => x.Id).Take(30));

        workspace.IsActive = true;
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "dense", q => q.OrderBy(x => x.Id).Take(30));
    }

    [TestMethod]
    public async Task GroupServiceAndAgentEligibility_RemainsLiveInOrdinaryQueries()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var context = fixture.Context;
        foreach (var caller in new[] { "member", "service", "agent" })
            await AssertPageAsync(fixture, caller, q => q.OrderBy(x => x.Rank).ThenBy(x => x.Id).Take(20));

        var groupFilter = await fixture.Auth.BuildFilterAsync<NativeRecord>("member", "native.view");
        var group = await context.Set<SqlOSFgaUserGroup>().SingleAsync();
        group.IsActive = false;
        await context.SaveChangesAsync();
        await AssertPreviouslyBuiltFilterIsEmptyAsync(fixture, groupFilter);
        await AssertPageAsync(fixture, "member", q => q.OrderBy(x => x.Id).Take(20));
        Assert.AreEqual(0, (await fixture.OracleIdsAsync("member")).Length);
        group.IsActive = true;
        await context.SaveChangesAsync();

        var service = await context.Set<SqlOSFgaServiceAccount>().SingleAsync();
        var existingFilter = await fixture.Auth.BuildFilterAsync<NativeRecord>("service", "native.view");
        service.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();
        await AssertPreviouslyBuiltFilterIsEmptyAsync(fixture, existingFilter);
        await AssertPageAsync(fixture, "service", q => q.OrderBy(x => x.Id).Take(20));

        var agentFilter = await fixture.Auth.BuildFilterAsync<NativeRecord>("agent", "native.view");
        var agent = await context.Set<SqlOSFgaAgent>().SingleAsync();
        context.Remove(agent);
        await context.SaveChangesAsync();
        await AssertPreviouslyBuiltFilterIsEmptyAsync(fixture, agentFilter);
        await AssertPageAsync(fixture, "agent", q => q.OrderBy(x => x.Id).Take(20));
        Assert.AreEqual(0, (await fixture.OracleIdsAsync("agent")).Length);

        var membership = await context.Set<SqlOSFgaUserGroupMembership>().SingleAsync();
        context.Remove(membership);
        await context.SaveChangesAsync();
        await AssertPageAsync(fixture, "member", q => q.OrderBy(x => x.Id).Take(20));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConcurrentResourceMoveAndApplicationWrite_LeaveOnlyCommittedVisibility(bool moveFirst)
    {
        await using var fixture = await NativeFixture.CreateAsync();
        await using var writer = fixture.CreateSiblingContext();
        var mover = fixture.Context;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var moveTransaction = await mover.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await using var writeTransaction = await writer.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var moving = await mover.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "record1", timeout.Token);
        var changing = await writer.Records.SingleAsync(x => x.Id == 1, timeout.Token);
        moving.ParentId = "workspaceB";
        changing.Title = "Concurrent update";
        changing.Rank = -100;
        var waitingSession = await ReadSessionIdAsync(moveFirst ? writer : mover);
        Task pending;
        if (moveFirst)
        {
            await mover.SaveChangesAsync(timeout.Token);
            pending = writer.SaveChangesAsync(timeout.Token);
        }
        else
        {
            await writer.SaveChangesAsync(timeout.Token);
            pending = mover.SaveChangesAsync(timeout.Token);
        }

        // Observe an actual database lock wait, or completion by a concurrency-safe
        // implementation, before releasing the first transaction. No timing sleep
        // is used as evidence that the statements overlapped.
        Exception? coordinationFailure = null;
        try
        {
            await WaitForLockOrCompletionAsync(moveFirst ? mover : writer, waitingSession, pending);
        }
        catch (Exception error)
        {
            coordinationFailure = error;
        }
        if (moveFirst)
        {
            await moveTransaction.CommitAsync(timeout.Token);
            await CommitOrAcceptConflictAsync(pending, writeTransaction);
        }
        else
        {
            await writeTransaction.CommitAsync(timeout.Token);
            await CommitOrAcceptConflictAsync(pending, moveTransaction);
        }
        if (coordinationFailure != null)
            throw new AssertFailedException("The coordinated transaction did not reach a lock wait or complete within its bound.", coordinationFailure);

        mover.ChangeTracker.Clear();
        await AssertCommittedProjectionAsync(fixture);
    }

    [TestMethod]
    public async Task PostgreSqlRepeatableRead_StaleApplicationSnapshotCannotCommitStaleAncestry()
    {
        if (!TestDatabase.IsPostgreSql)
            Assert.Inconclusive("This test exercises PostgreSQL's REPEATABLE READ snapshot behavior.");
        await using var fixture = await NativeFixture.CreateAsync();
        await using var writer = fixture.CreateSiblingContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var staleTransaction = await writer.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token);
        // This query establishes the old snapshot before the other connection moves
        // the resource. The application row itself has not changed in that move.
        var changing = await writer.Records.SingleAsync(x => x.Id == 1, timeout.Token);
        await using (var moveTransaction = await fixture.Context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token))
        {
            var moving = await fixture.Context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "record1", timeout.Token);
            moving.ParentId = "workspaceB";
            await fixture.Context.SaveChangesAsync(timeout.Token);
            await moveTransaction.CommitAsync(timeout.Token);
        }
        changing.Title = "Concurrent update";
        changing.Rank = -100;
        await CommitOrAcceptConflictAsync(writer.SaveChangesAsync(timeout.Token), staleTransaction);

        fixture.Context.ChangeTracker.Clear();
        await AssertCommittedProjectionAsync(fixture);
        Assert.IsFalse((await fixture.OracleIdsAsync("workspace")).Contains(1), "The resource move must have committed.");
    }

    private static async Task AssertCommittedProjectionAsync(NativeFixture fixture)
    {
        foreach (var caller in new[] { "workspace", "many", "service", "dense" })
        {
            await AssertPageAsync(fixture, caller, q => q.OrderBy(x => x.Id).Take(30));
            await AssertPageAsync(fixture, caller, q => q.Where(x => x.Title == "Concurrent update")
                .OrderBy(x => x.Title).ThenByDescending(x => x.Rank).ThenBy(x => x.Id).Take(7));
            await AssertPageAsync(fixture, caller, q => q.Where(x => x.Rank < 0)
                .OrderBy(x => x.Rank).ThenBy(x => x.Id).Take(7));
        }
    }

    private static async Task<int> ReadSessionIdAsync(NativeIndexContext context)
        => await ReadTransactionScalarAsync(context, TestDatabase.IsPostgreSql ? "SELECT pg_backend_pid()" : "SELECT @@SPID");

    private static async Task WaitForLockOrCompletionAsync(NativeIndexContext holdingContext, int waitingSession, Task pending)
    {
        var elapsed = Stopwatch.StartNew();
        while (!pending.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            var sql = TestDatabase.IsPostgreSql
                ? "SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_locks WHERE pid = @session AND NOT granted) THEN 1 ELSE 0 END"
                : "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests WHERE session_id = @session AND wait_type LIKE 'LCK_M_%') THEN 1 ELSE 0 END";
            if (await ReadTransactionScalarAsync(holdingContext, sql, waitingSession) == 1)
                return;
            await Task.WhenAny(pending, Task.Delay(25));
        }
        if (!pending.IsCompleted)
            throw new TimeoutException("No server lock wait or statement completion was observed in 10 seconds.");
    }

    private static async Task<int> ReadTransactionScalarAsync(NativeIndexContext context, string sql, int? session = null)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        if (session.HasValue) TestDatabase.AddParameter(command, "session", session.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task CommitOrAcceptConflictAsync(Task operation, IDbContextTransaction transaction)
    {
        try
        {
            await operation;
            await transaction.CommitAsync();
        }
        catch (Exception error) when (IsTransactionConflict(error))
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // SQL Server can already have rolled back a deadlock victim.
            }
        }
    }

    private static bool IsTransactionConflict(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: "40001" or "40P01" }) return true;
            if (current is SqlException { Number: 1205 or 3960 }) return true;
        }
        return false;
    }

    private static async Task<(PlanMetrics Metrics, string Plan)> CaptureActualPlanAsync(NativeIndexContext context, CapturedStatement statement)
    {
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = TestDatabase.IsPostgreSql
                ? "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + statement.Sql
                : "SET STATISTICS XML ON;\n" + statement.Sql + "\nSET STATISTICS XML OFF;";
            foreach (var parameter in statement.Parameters)
                command.Parameters.Add(((ICloneable)parameter).Clone());
            string? plan = null;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                do
                {
                    while (await reader.ReadAsync())
                    {
                        if (reader.FieldCount == 1 && reader.GetValue(0) is string value &&
                            (TestDatabase.IsPostgreSql || value.Contains("ShowPlanXML", StringComparison.Ordinal)))
                            plan = value;
                    }
                } while (await reader.NextResultAsync());
            }
            Assert.IsNotNull(plan, "The database did not return an actual execution plan for the captured library statement.");
            return (TestDatabase.IsPostgreSql ? ReadPostgreSqlPlan(plan) : ReadSqlServerPlan(plan), plan);
        }
        finally
        {
            if (TestDatabase.IsSqlServer && connection.State == ConnectionState.Open)
            {
                await using var reset = connection.CreateCommand();
                reset.CommandText = "SET STATISTICS XML OFF";
                await reset.ExecuteNonQueryAsync();
            }
            if (opened) await connection.CloseAsync();
        }
    }

    private static PlanMetrics ReadPostgreSqlPlan(string json)
    {
        using var document = JsonDocument.Parse(json);
        double scope = 0, application = 0;
        var scopeSeek = false;
        void Visit(JsonElement node, bool parentIsScope)
        {
            var relation = node.TryGetProperty("Relation Name", out var r) ? r.GetString() : null;
            var isScope = relation?.StartsWith("SqlOSFgaScopeIndex_", StringComparison.Ordinal) == true;
            var type = node.GetProperty("Node Type").GetString() ?? "";
            if ((isScope || relation == "NativeRecords") && type.Contains("Scan", StringComparison.Ordinal))
            {
                var count = node.GetProperty("Actual Rows").GetDouble();
                foreach (var key in new[] { "Rows Removed by Filter", "Rows Removed by Index Recheck" })
                    if (node.TryGetProperty(key, out var removed)) count += removed.GetDouble();
                count *= node.GetProperty("Actual Loops").GetDouble();
                if (isScope) scope += count;
                else application += count;
            }
            if ((isScope || parentIsScope) && type.Contains("Index", StringComparison.Ordinal) &&
                node.TryGetProperty("Actual Loops", out var seekLoops) && seekLoops.GetDouble() > 0 &&
                node.TryGetProperty("Index Cond", out var condition) &&
                condition.GetString()?.Contains("_AncestorKey", StringComparison.Ordinal) == true)
                scopeSeek = true;
            if (node.TryGetProperty("Plans", out var children))
                foreach (var child in children.EnumerateArray()) Visit(child, isScope || (parentIsScope && relation == null));
        }
        Visit(document.RootElement[0].GetProperty("Plan"), false);
        return new(scope, application, scopeSeek);
    }

    private static PlanMetrics ReadSqlServerPlan(string xml)
    {
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        double scope = 0, application = 0;
        var scopeSeek = false;
        foreach (var operation in document.Descendants(ns + "RelOp"))
        {
            var scan = operation.Element(ns + "IndexScan") ?? operation.Element(ns + "TableScan");
            var table = scan?.Element(ns + "Object")?.Attribute("Table")?.Value.Trim('[', ']');
            var isScope = table?.StartsWith("SqlOSFgaScopeIndex_", StringComparison.Ordinal) == true;
            if (!isScope && table != "NativeRecords") continue;
            var counters = (operation.Element(ns + "RunTimeInformation")?.Elements(ns + "RunTimeCountersPerThread") ?? []).ToArray();
            foreach (var counter in counters)
            {
                var readAttribute = counter.Attribute("ActualRowsRead");
                if (readAttribute == null)
                {
                    Assert.AreEqual(0d, (double?)counter.Attribute("ActualExecutions") ?? 0d,
                        "An executed table-access operator omitted ActualRowsRead; a missing counter is not a zero-read proof.");
                    continue;
                }
                if (isScope) scope += (double)readAttribute;
                else application += (double)readAttribute;
            }
            if (isScope && operation.Attribute("PhysicalOp")?.Value.EndsWith("Index Seek", StringComparison.Ordinal) == true &&
                counters.Any(x => ((double?)x.Attribute("ActualExecutions") ?? 0) > 0) &&
                scan!.Descendants(ns + "SeekPredicates").Descendants(ns + "ColumnReference")
                    .Any(x => x.Attribute("Column")?.Value == "_AncestorKey"))
                scopeSeek = true;
        }
        return new(scope, application, scopeSeek);
    }

    private sealed record PlanMetrics(double ScopeRowsRead, double ApplicationRowsRead, bool ScopeSeek);
    private sealed record CapturedStatement(string Sql, IReadOnlyList<DbParameter> Parameters);

    [TestMethod]
    public async Task ScheduledGrant_BecomesUsableWithoutRebuildingTheIndex()
    {
        await using var fixture = await NativeFixture.CreateAsync();
        var scheduled = Grant("scheduled", "none", "workspaceB");
        scheduled.EffectiveFrom = DateTime.UtcNow.AddHours(1);
        fixture.Context.Set<SqlOSFgaGrant>().Add(scheduled);
        await fixture.Context.SaveChangesAsync();
        var filter = await fixture.Auth.BuildFilterAsync<NativeRecord>("none", "native.view");
        Assert.AreEqual(0, await fixture.Context.Records.Where(filter).OrderBy(x => x.Id).Take(20).CountAsync());
        // Establish the denied state before scheduling the short clock transition,
        // so principal resolution and the first query cannot consume that window.
        var start = DateTime.UtcNow.AddSeconds(4);
        scheduled.EffectiveFrom = start;
        await fixture.Context.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, (start - DateTime.UtcNow).TotalMilliseconds + 150)));
        CollectionAssert.AreEqual(Enumerable.Range(9, 8).ToArray(), await fixture.Context.Records.Where(filter)
            .OrderBy(x => x.Id).Take(20).Select(x => x.Id).ToArrayAsync());
        await AssertPageAsync(fixture, "none", q => q.OrderBy(x => x.Id).Take(20));

        scheduled.EffectiveTo = DateTime.UtcNow.AddSeconds(-1);
        await fixture.Context.SaveChangesAsync();
        await AssertPageAsync(fixture, "none", q => q.OrderBy(x => x.Id).Take(20));
    }

    private static async Task AssertPreviouslyBuiltFilterIsEmptyAsync(NativeFixture fixture,
        Expression<Func<NativeRecord, bool>> filter)
    {
        fixture.Commands.Clear();
        var rows = await fixture.Context.Records.Where(filter).OrderBy(x => x.Id).Take(20)
            .Select(x => x.Id).ToArrayAsync();
        Assert.AreEqual(0, rows.Length, "A previously constructed filter must recheck current principal eligibility.");
        AssertNativePageExecuted(fixture);
    }

    private static async Task AssertPageAsync(NativeFixture fixture, string caller,
        Func<IQueryable<NativeRecord>, IQueryable<NativeRecord>> compose)
    {
        var allowed = await fixture.OracleIdsAsync(caller);
        var expected = await compose(fixture.Context.Records.AsNoTracking().Where(x => allowed.Contains(x.Id)))
            .Select(x => x.Id).ToArrayAsync();
        var filter = await fixture.Auth.BuildFilterAsync<NativeRecord>(caller, "native.view");
        fixture.Commands.Clear();
        var actual = await compose(fixture.Context.Records.AsNoTracking().Where(filter))
            .Select(x => x.Id).ToArrayAsync();
        CollectionAssert.AreEqual(expected, actual, $"Caller {caller}; provider {fixture.Context.Database.ProviderName}");
        if (filter.Body is not ConstantExpression { Value: false })
        {
            AssertNativePageExecuted(fixture);
        }
    }

    private static void AssertNativePageExecuted(NativeFixture fixture)
        => Assert.IsTrue(fixture.Commands.Commands.Any(sql => sql.Contains("SqlOSFgaScopeIndex_", StringComparison.Ordinal)),
            "This test must execute the native scope-index page, rather than passing through the old TVF fallback. " +
            string.Join(Environment.NewLine, fixture.Commands.Commands));

    private static SqlOSFgaResource Resource(string id, string parent, string type = "record")
        => new() { Id = id, Name = id, ParentId = parent, ResourceTypeId = type };

    private static SqlOSFgaGrant Grant(string id, string caller, string resource)
        => new() { Id = id, SubjectId = caller, ResourceId = resource, RoleId = "reader" };

    private sealed class NativeFixture : IAsyncDisposable
    {
        private readonly string _databaseName;
        public NativeIndexContext Context { get; }
        public SqlOSFgaAuthService Auth { get; }
        public CommandCapture Commands { get; }

        private NativeFixture(string databaseName, NativeIndexContext context, CommandCapture commands)
        {
            _databaseName = databaseName;
            Context = context;
            Commands = commands;
            Auth = new SqlOSFgaAuthService(context, Options.Create(new SqlOSFgaOptions()),
                NullLogger<SqlOSFgaAuthService>.Instance);
        }

        public NativeIndexContext CreateSiblingContext()
            => new(new DbContextOptionsBuilder<NativeIndexContext>()
                .UseTestProvider(Context.Database.GetConnectionString()!).Options);

        public static async Task<NativeFixture> CreateAsync()
        {
            var databaseName = $"NativeFga_{Guid.NewGuid():N}";
            await TestDatabase.CreateDatabaseAsync(AspireFixture.SqlConnectionString, databaseName);
            var connectionString = TestDatabase.CreateIsolatedConnectionString(AspireFixture.SqlConnectionString, databaseName);
            var commands = new CommandCapture();
            var context = new NativeIndexContext(new DbContextOptionsBuilder<NativeIndexContext>()
                .UseTestProvider(connectionString).AddInterceptors(commands).Options);
            var fixture = new NativeFixture(databaseName, context, commands);
            try
            {
                var options = Options.Create(new SqlOSFgaOptions());
                await new SqlOSFgaSchemaInitializer(context, options, NullLogger<SqlOSFgaSchemaInitializer>.Instance)
                    .EnsureSchemaAsync();
                await context.Database.ExecuteSqlRawAsync(TestDatabase.Rewrite("""
                    CREATE TABLE [dbo].[NativeRecords] (
                        [Id] int NOT NULL PRIMARY KEY,
                        [ResourceId] nvarchar(450) NOT NULL REFERENCES [dbo].[SqlOSFgaResources]([Id]),
                        [Title] nvarchar(120) NOT NULL,
                        [Rank] int NULL,
                        [Flag] bit NOT NULL
                    );
                    CREATE INDEX [IX_NativeRecords_Title_Rank_Id] ON [dbo].[NativeRecords]([Title], [Rank] DESC, [Id]);
                    CREATE INDEX [IX_NativeRecords_Rank_Id] ON [dbo].[NativeRecords]([Rank], [Id]);
                    CREATE TABLE [dbo].[NativeOwnedRecords] (
                        [Id] int NOT NULL PRIMARY KEY,
                        [ResourceId] nvarchar(450) NOT NULL REFERENCES [dbo].[SqlOSFgaResources]([Id]),
                        [Title] nvarchar(120) NOT NULL
                    );
                    CREATE INDEX [IX_NativeOwnedRecords_Title_Id] ON [dbo].[NativeOwnedRecords]([Title], [Id]);
                    """));
                await new SqlOSFgaFunctionInitializer(context, options, NullLogger<SqlOSFgaFunctionInitializer>.Instance)
                    .EnsureFunctionsExistAsync();
                await new SqlOSFgaSeedService(context, options, NullLogger<SqlOSFgaSeedService>.Instance).SeedCoreAsync();
                await fixture.SeedAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private async Task SeedAsync()
        {
            Context.AddRange(new[] { "workspace", "record", "other" }
                .Select(id => new SqlOSFgaResourceType { Id = id, Name = id }));
            Context.Add(new SqlOSFgaRole { Id = "reader", Key = "native.reader", Name = "Reader" });
            Context.Add(new SqlOSFgaPermission { Id = "view", Key = "native.view", Name = "View", ResourceTypeId = "record" });
            Context.Add(new SqlOSFgaRolePermission { RoleId = "reader", PermissionId = "view" });
            foreach (var caller in new[] { "dense", "workspace", "direct", "many", "overlap", "none", "member" })
            {
                Context.Add(new SqlOSFgaSubject { Id = caller, SubjectTypeId = "user", DisplayName = caller });
                Context.Add(new SqlOSFgaUser { Id = "user_" + caller, SubjectId = caller, IsActive = true });
            }
            Context.Add(new SqlOSFgaSubject { Id = "group", SubjectTypeId = "group", DisplayName = "Group" });
            Context.Add(new SqlOSFgaUserGroup { Id = "group_row", SubjectId = "group", Name = "Group" });
            Context.Add(new SqlOSFgaUserGroupMembership { SubjectId = "member", UserGroupId = "group_row" });
            Context.Add(new SqlOSFgaSubject { Id = "service", SubjectTypeId = "service_account", DisplayName = "Service" });
            Context.Add(new SqlOSFgaServiceAccount { Id = "service_row", SubjectId = "service", ClientId = "native-service",
                ClientSecretHash = "not-a-real-secret", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            Context.Add(new SqlOSFgaSubject { Id = "agent", SubjectTypeId = "agent", DisplayName = "Agent" });
            Context.Add(new SqlOSFgaAgent { Id = "agent_row", SubjectId = "agent" });
            Context.AddRange(new[] { "workspaceA", "workspaceB", "workspaceC" }.Select(id => Resource(id, "root", "workspace")));
            for (var i = 1; i <= 24; i++)
            {
                var parent = i <= 8 ? "workspaceA" : i <= 16 ? "workspaceB" : "workspaceC";
                Context.Add(Resource("record" + i, parent));
                Context.Records.Add(new NativeRecord
                {
                    Id = i, ResourceId = "record" + i,
                    Title = new[] { "alpha", "Alpha", "Éclair", "eclair", "beta", "Zulu" }[(i - 1) % 6],
                    Rank = i % 5 == 0 ? null : i % 4, Flag = i % 3 == 0
                });
            }
            Context.AddRange(
                Grant("dense-root", "dense", "root"), Grant("workspace-A", "workspace", "workspaceA"),
                Grant("direct-24", "direct", "record24"), Grant("many-A", "many", "workspaceA"),
                Grant("many-C", "many", "workspaceC"), Grant("overlap-root", "overlap", "root"),
                Grant("overlap-A", "overlap", "workspaceA"), Grant("overlap-1", "overlap", "record1"),
                Grant("group-A", "group", "workspaceA"), Grant("service-B", "service", "workspaceB"),
                Grant("agent-C", "agent", "workspaceC"));
            await Context.SaveChangesAsync();
        }

        public async Task SeedScaleAsync(int unrelated)
        {
            Context.Add(Resource("scale-other", "root", "workspace"));
            Context.Add(Resource("scale-allowed", "root", "workspace"));
            Context.Add(new SqlOSFgaSubject { Id = "scale-caller", SubjectTypeId = "user", DisplayName = "Scale caller" });
            Context.Add(new SqlOSFgaUser { Id = "scale-user", SubjectId = "scale-caller", IsActive = true });
            Context.Add(Grant("scale-grant", "scale-caller", "scale-allowed"));
            await Context.SaveChangesAsync();
            var total = unrelated + 65;
            var sql = TestDatabase.IsPostgreSql
                ? $"""
                    INSERT INTO "dbo"."SqlOSFgaResources"
                        ("Id", "ParentId", "Name", "ResourceTypeId", "IsActive", "CreatedAt", "UpdatedAt")
                    SELECT 'scale-resource-' || i, CASE WHEN i <= {unrelated} THEN 'scale-other' ELSE 'scale-allowed' END,
                           'Scale resource', 'record', TRUE, CURRENT_TIMESTAMP AT TIME ZONE 'UTC', CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
                    FROM generate_series(1, {total}) AS numbers(i);
                    INSERT INTO "dbo"."NativeRecords" ("Id", "ResourceId", "Title", "Rank", "Flag")
                    SELECT i + 100, 'scale-resource-' || i, 'Scale row', i, FALSE
                    FROM generate_series(1, {total}) AS numbers(i);
                    """
                : $"""
                    CREATE TABLE #NativeScaleNumbers (i int NOT NULL PRIMARY KEY);
                    WITH digits AS (SELECT d FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) numbers(d))
                    INSERT INTO #NativeScaleNumbers (i)
                    SELECT TOP ({total}) CONVERT(int, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
                    FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d
                    CROSS JOIN digits e CROSS JOIN digits f CROSS JOIN digits g;
                    INSERT INTO [dbo].[SqlOSFgaResources]
                        ([Id], [ParentId], [Name], [ResourceTypeId], [IsActive], [CreatedAt], [UpdatedAt])
                    SELECT N'scale-resource-' + CONVERT(nvarchar(10), i),
                           CASE WHEN i <= {unrelated} THEN N'scale-other' ELSE N'scale-allowed' END,
                           N'Scale resource', N'record', 1, SYSUTCDATETIME(), SYSUTCDATETIME()
                    FROM #NativeScaleNumbers;
                    INSERT INTO [dbo].[NativeRecords] ([Id], [ResourceId], [Title], [Rank], [Flag])
                    SELECT i + 100, N'scale-resource-' + CONVERT(nvarchar(10), i), N'Scale row', i, 0
                    FROM #NativeScaleNumbers;
                    DROP TABLE #NativeScaleNumbers;
                    """;
            var priorTimeout = Context.Database.GetCommandTimeout();
            Context.Database.SetCommandTimeout(120);
            try
            {
                await Context.Database.ExecuteSqlRawAsync(sql);
                var tables = new List<string> { "NativeRecords", "SqlOSFgaResources", "SqlOSFgaGrants" };
                tables.AddRange(await ReadStringsAsync("""
                    SELECT table_name FROM information_schema.tables
                    WHERE table_schema = 'dbo' AND table_name LIKE 'SqlOSFgaScopeIndex%'
                    """));
                foreach (var table in tables)
                {
                    var stats = TestDatabase.IsPostgreSql
                        ? $"ANALYZE {TestDatabase.Qualify(table)}"
                        : $"UPDATE STATISTICS {TestDatabase.Qualify(table)} WITH FULLSCAN";
                    await Context.Database.ExecuteSqlRawAsync(stats);
                }
            }
            finally
            {
                Context.Database.SetCommandTimeout(priorTimeout);
            }
        }

        public async Task<int[]> OracleIdsAsync(string caller)
        {
            var subjects = new List<string> { caller };
            subjects.AddRange(await (from membership in Context.Set<SqlOSFgaUserGroupMembership>()
                                     join userGroup in Context.Set<SqlOSFgaUserGroup>() on membership.UserGroupId equals userGroup.Id
                                     where membership.SubjectId == caller && userGroup.IsActive
                                     select userGroup.SubjectId).ToArrayAsync());
            var connection = Context.Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;
            if (opened) await connection.OpenAsync();
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = TestDatabase.IsPostgreSql
                    ? """SELECT app."Id" FROM "dbo"."NativeRecords" app WHERE EXISTS (SELECT 1 FROM "dbo"."fn_IsResourceAccessible"(app."ResourceId", @subjects, @permission)) ORDER BY 1"""
                    : """SELECT app.[Id] FROM [dbo].[NativeRecords] app WHERE EXISTS (SELECT 1 FROM [dbo].[fn_IsResourceAccessible](app.[ResourceId], @subjects, @permission)) ORDER BY app.[Id]""";
                TestDatabase.AddParameter(command, "subjects", JsonSerializer.Serialize(subjects));
                TestDatabase.AddParameter(command, "permission", "view");
                var result = new List<int>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) result.Add(reader.GetInt32(0));
                return result.ToArray();
            }
            finally
            {
                if (opened) await connection.CloseAsync();
            }
        }

        public async Task<string[]> ReadStringsAsync(string sql)
        {
            var connection = Context.Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;
            if (opened) await connection.OpenAsync();
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var result = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) result.Add(reader.GetString(0));
                return result.ToArray();
            }
            finally
            {
                if (opened) await connection.CloseAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await TestDatabase.DropDatabaseAsync(AspireFixture.SqlConnectionString, _databaseName);
        }
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<CapturedStatement> Statements { get; } = [];
        public void Clear()
        {
            Commands.Clear();
            Statements.Clear();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Statements.Add(new CapturedStatement(command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(p => (DbParameter)((ICloneable)p).Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NativeIndexContext(DbContextOptions<NativeIndexContext> options)
        : SqlOSDbContext<NativeIndexContext>(options)
    {
        public DbSet<NativeRecord> Records => Set<NativeRecord>();
        public DbSet<NativeOwnedRecord> OwnedRecords => Set<NativeOwnedRecord>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NativeRecord>(entity =>
            {
                entity.ToTable("NativeRecords", "dbo");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).ValueGeneratedNever();
                entity.Property(x => x.ResourceId).HasMaxLength(450);
                entity.Property(x => x.Title).HasMaxLength(120);
                entity.HasOne<SqlOSFgaResource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => new { x.Title, x.Rank, x.Id }).IsDescending(false, true, false);
                entity.HasIndex(x => new { x.Rank, x.Id });
            });
            modelBuilder.Entity<NativeOwnedRecord>(entity =>
            {
                entity.ToTable("NativeOwnedRecords", "dbo");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).ValueGeneratedNever();
                entity.Property(x => x.ResourceId).HasMaxLength(450);
                entity.Property(x => x.Title).HasMaxLength(120);
                entity.HasIndex(x => new { x.Title, x.Id });
                entity.Ignore(x => x.ResourceTypeId);
                entity.Ignore(x => x.ResourceName);
                entity.Ignore(x => x.ParentResourceId);
                entity.Ignore(x => x.ResourceDescription);
                entity.Ignore(x => x.ResourceIsActive);
            });
        }
    }

    private sealed class NativeRecord : IHasResourceId
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Rank { get; set; }
        public bool Flag { get; set; }
    }

    private sealed class NativeOwnedRecord : ISqlOSResourceEntity
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = "";
        public string Title { get; set; } = "";
        public string ResourceTypeId => "record";
        public string ResourceName => Title;
        public string? ParentResourceId => "root";
        public string? ResourceDescription => null;
        public bool ResourceIsActive => true;
    }
}
