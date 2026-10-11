using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

// =============================================================================
// SqlOS FGA Benchmark Suite
// Measures fn_IsResourceAccessible TVF performance using the real SqlOS FGA
// schema (SqlOSFgaResources, SqlOSFgaGrants, SqlOSFgaRolePermissions, etc.)
//
// Benchmarks 1-7: Small-scale isolation tests (1K → 100K products)
//   1. Product count scaling — proves query time is O(k) not O(N)
//   2. Hierarchy depth sensitivity — proves per-page cost scales linearly with D
//   3. Principal set size sensitivity — how M principal IDs affect STRING_SPLIT join
//   6. Baseline comparison — TVF EXISTS vs pre-materialized permissions JOIN
//   7. Offset vs cursor pagination — proves COUNT(*) is catastrophic at scale
//
//   All benchmarks query a Products table with realistic domain columns (Name, SKU, Price).
//
// Benchmarks 8-10: Resource-scale tests (1.2M resources, D=5 hierarchy)
//   8. List filtering at 1.2M resources — page size, access scope, deep cursor
//   9. Point access check at 1.2M resources
//  10. Dimensional analysis at 1.2M resources
//
// Benchmarks 11-13: Deep hierarchy tests (1.5M resources, D=10 hierarchy)
//  11. List filtering at D=10 — page size, access scope, deep cursor
//  12. Point access check at D=10 (depth 0-9)
//  13. Dimensional analysis at D=10
//
// Benchmarks 8-10 share a D=5 seed; 11-13 share a D=10 seed.
// Real domain tables (Products, Stores, Regions, Chains) replace TestEntities.
// =============================================================================

var connectionString = args.Length > 0
    ? args[0]
    : "Server=localhost,52684;Database=SqlOSFga_Benchmark;User Id=sa;Password=LocalDevPassword123!;TrustServerCertificate=True;";

Console.WriteLine("=== SqlOS FGA Benchmark Suite ===");
Console.WriteLine($"Target: {connectionString}");
Console.WriteLine();

await EnsureDatabaseAsync();

var results = new List<BenchmarkResult>();

// -------------------------------------------------------------------------
// Benchmarks 1-7: Small-scale isolation tests
// Each benchmark re-seeds with a small dataset to isolate one dimension.
// -------------------------------------------------------------------------

Console.WriteLine("--- Benchmark 1: Product Count Scaling (TVF, cursor pagination) ---");
results.AddRange(await RunEntityCountScalingAsync());

Console.WriteLine();
Console.WriteLine("--- Benchmark 2: Hierarchy Depth Sensitivity ---");
results.AddRange(await RunDepthSensitivityAsync());

Console.WriteLine();
Console.WriteLine("--- Benchmark 3: Principal Set Size Sensitivity ---");
results.AddRange(await RunPrincipalSetSensitivityAsync());

Console.WriteLine();
Console.WriteLine("--- Benchmark 6: TVF vs Materialized Permissions ---");
results.AddRange(await RunBaselineComparisonAsync());

Console.WriteLine();
Console.WriteLine("--- Benchmark 7: Offset vs Cursor Pagination ---");
results.AddRange(await RunPaginationComparisonAsync());

// -------------------------------------------------------------------------
// Benchmarks 8-10: D=5 Resource-scale tests (1.2M resources)
// Real domain tables: Products, Stores, Regions, Chains
// -------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- Seeding D=5 Resource Tree (1.2M resources, used by Benchmarks 8-10) ---");
var seedSw = Stopwatch.StartNew();
var d5Principals = await SeedRetailD5Async();
seedSw.Stop();
Console.WriteLine($"  D=5 seeding complete in {seedSw.Elapsed.TotalSeconds:F1}s");

Console.WriteLine();
Console.WriteLine("--- Benchmark 8: List Filtering at 1.2M Resources (D=5) ---");
results.AddRange(await RunListFilteringD5Async(d5Principals));

Console.WriteLine();
Console.WriteLine("--- Benchmark 9: Point Access Check at 1.2M Resources (D=5) ---");
results.AddRange(await RunPointAccessCheckD5Async(d5Principals));

Console.WriteLine();
Console.WriteLine("--- Benchmark 10: Dimensional Analysis at 1.2M Resources (D=5) ---");
results.AddRange(await RunDimensionalAnalysisD5Async(d5Principals));

// -------------------------------------------------------------------------
// Benchmarks 11-13: D=10 Deep hierarchy tests (1.5M resources)
// -------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- Seeding D=10 Resource Tree (1.5M resources, used by Benchmarks 11-13) ---");
var seedSw2 = Stopwatch.StartNew();
var d10Principals = await SeedRetailD10Async();
seedSw2.Stop();
Console.WriteLine($"  D=10 seeding complete in {seedSw2.Elapsed.TotalSeconds:F1}s");

Console.WriteLine();
Console.WriteLine("--- Benchmark 11: List Filtering at 1.5M Resources (D=10) ---");
results.AddRange(await RunListFilteringD10Async(d10Principals));

Console.WriteLine();
Console.WriteLine("--- Benchmark 12: Point Access Check at D=10 ---");
results.AddRange(await RunPointAccessCheckD10Async(d10Principals));

Console.WriteLine();
Console.WriteLine("--- Benchmark 13: Dimensional Analysis at D=10 ---");
results.AddRange(await RunDimensionalAnalysisD10Async(d10Principals));

Console.WriteLine();
Console.WriteLine("=== FULL RESULTS ===");
Console.WriteLine();
PrintResults(results);

Console.WriteLine();
Console.WriteLine("=== MARKDOWN TABLE (for paper) ===");
Console.WriteLine();
PrintMarkdownTable(results);

await CleanupAsync();

return;

// =============================================================================
// Database Setup
// =============================================================================

async Task EnsureDatabaseAsync()
{
    var masterConn = connectionString.Replace("Database=SqlOSFga_Benchmark", "Database=master");
    await using var conn = new SqlConnection(masterConn);
    await conn.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        IF EXISTS (SELECT 1 FROM sys.databases WHERE name = 'SqlOSFga_Benchmark')
        BEGIN
            ALTER DATABASE SqlOSFga_Benchmark SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE SqlOSFga_Benchmark;
        END
        CREATE DATABASE SqlOSFga_Benchmark;";
    await cmd.ExecuteNonQueryAsync();
    Console.WriteLine("Database created.");
}

async Task<SqlConnection> GetConnectionAsync()
{
    var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    return conn;
}

async Task ExecuteNonQueryAsync(string sql)
{
    await using var conn = await GetConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync();
}

async Task<object?> ExecuteScalarAsync(string sql)
{
    await using var conn = await GetConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    return await cmd.ExecuteScalarAsync();
}

// =============================================================================
// SqlOSFga Schema + TVF Setup
// =============================================================================

async Task SetupSchemaAsync()
{
    // Drop in dependency order
    await ExecuteNonQueryAsync(@"
        DROP TABLE IF EXISTS MaterializedPermissions;
        DROP TABLE IF EXISTS Products;
        DROP TABLE IF EXISTS Stores;
        DROP TABLE IF EXISTS Regions;
        DROP TABLE IF EXISTS Chains;
        DROP TABLE IF EXISTS Sections;
        DROP TABLE IF EXISTS Departments;
        DROP TABLE IF EXISTS Zones;
        DROP TABLE IF EXISTS Areas;
        DROP TABLE IF EXISTS Districts;
        DROP TABLE IF EXISTS Divisions;
        DROP TABLE IF EXISTS SqlOSFgaUserGroupMemberships;
        DROP TABLE IF EXISTS SqlOSFgaUserGroups;
        DROP TABLE IF EXISTS SqlOSFgaGrants;
        DROP TABLE IF EXISTS SqlOSFgaRolePermissions;
        DROP TABLE IF EXISTS SqlOSFgaPermissions;
        DROP TABLE IF EXISTS SqlOSFgaRoles;
        DROP TABLE IF EXISTS SqlOSFgaResources;
        DROP TABLE IF EXISTS SqlOSFgaResourceTypes;
        DROP TABLE IF EXISTS SqlOSFgaServiceAccounts;
        DROP TABLE IF EXISTS SqlOSFgaPrincipals;
        DROP TABLE IF EXISTS SqlOSFgaPrincipalTypes;
    ");

    await ExecuteNonQueryAsync(@"
        -- Principal types (user, group, service_account)
        CREATE TABLE SqlOSFgaPrincipalTypes (
            Id NVARCHAR(128) PRIMARY KEY,
            Name NVARCHAR(256) NOT NULL,
            Description NVARCHAR(500) NULL
        );

        -- Principals
        CREATE TABLE SqlOSFgaPrincipals (
            Id NVARCHAR(128) PRIMARY KEY,
            PrincipalTypeId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaPrincipalTypes(Id),
            OrganizationId NVARCHAR(128) NULL,
            ExternalRef NVARCHAR(256) NULL,
            DisplayName NVARCHAR(256) NOT NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );

        -- User groups
        CREATE TABLE SqlOSFgaUserGroups (
            Id NVARCHAR(128) PRIMARY KEY,
            PrincipalId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaPrincipals(Id),
            Name NVARCHAR(256) NOT NULL,
            Description NVARCHAR(500) NULL,
            GroupType NVARCHAR(128) NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );

        -- Group memberships
        CREATE TABLE SqlOSFgaUserGroupMemberships (
            PrincipalId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaPrincipals(Id),
            UserGroupId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaUserGroups(Id),
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            PRIMARY KEY (PrincipalId, UserGroupId)
        );

        -- Resource types
        CREATE TABLE SqlOSFgaResourceTypes (
            Id NVARCHAR(128) PRIMARY KEY,
            Name NVARCHAR(256) NOT NULL,
            Description NVARCHAR(500) NULL
        );

        -- Resources (hierarchy)
        CREATE TABLE SqlOSFgaResources (
            Id NVARCHAR(128) PRIMARY KEY CLUSTERED,
            ParentId NVARCHAR(128) NULL REFERENCES SqlOSFgaResources(Id),
            Name NVARCHAR(256) NOT NULL,
            Description NVARCHAR(500) NULL,
            ResourceTypeId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaResourceTypes(Id),
            IsActive BIT NOT NULL DEFAULT 1,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );
        CREATE NONCLUSTERED INDEX IX_SqlOSFgaResources_ParentId ON SqlOSFgaResources(ParentId);

        -- Roles
        CREATE TABLE SqlOSFgaRoles (
            Id NVARCHAR(128) PRIMARY KEY,
            [Key] NVARCHAR(256) NOT NULL,
            Name NVARCHAR(256) NOT NULL,
            Description NVARCHAR(500) NULL,
            IsVirtual BIT NOT NULL DEFAULT 0
        );

        -- Permissions
        CREATE TABLE SqlOSFgaPermissions (
            Id NVARCHAR(128) PRIMARY KEY,
            [Key] NVARCHAR(256) NOT NULL,
            Name NVARCHAR(256) NOT NULL,
            Description NVARCHAR(500) NULL,
            ResourceTypeId NVARCHAR(128) NULL REFERENCES SqlOSFgaResourceTypes(Id)
        );

        -- Role-permission mapping
        CREATE TABLE SqlOSFgaRolePermissions (
            RoleId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaRoles(Id),
            PermissionId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaPermissions(Id),
            PRIMARY KEY (RoleId, PermissionId)
        );
        CREATE NONCLUSTERED INDEX IX_SqlOSFgaRolePermissions_PermId
            ON SqlOSFgaRolePermissions(PermissionId, RoleId);

        -- Grants
        CREATE TABLE SqlOSFgaGrants (
            Id NVARCHAR(128) PRIMARY KEY,
            PrincipalId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaPrincipals(Id),
            RoleId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaRoles(Id),
            ResourceId NVARCHAR(128) NOT NULL REFERENCES SqlOSFgaResources(Id),
            EffectiveFrom DATETIME2 NULL,
            EffectiveTo DATETIME2 NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );
        CREATE NONCLUSTERED INDEX IX_SqlOSFgaGrants_ResId_PrinId
            ON SqlOSFgaGrants(ResourceId, PrincipalId) INCLUDE (RoleId, EffectiveFrom, EffectiveTo);
        CREATE NONCLUSTERED INDEX IX_SqlOSFgaGrants_PrinId
            ON SqlOSFgaGrants(PrincipalId);

        -- Materialized permissions (baseline comparison)
        CREATE TABLE MaterializedPermissions (
            PrincipalId NVARCHAR(128) NOT NULL,
            PermissionId NVARCHAR(128) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL,
            PRIMARY KEY (PrincipalId, PermissionId, ResourceId)
        );
        CREATE NONCLUSTERED INDEX IX_MatPerm_ResId
            ON MaterializedPermissions(ResourceId, PrincipalId, PermissionId);

        -- Domain tables (real app schema for retail benchmarks)
        CREATE TABLE Chains (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Chains_ResourceId ON Chains(ResourceId);

        CREATE TABLE Regions (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Regions_ResourceId ON Regions(ResourceId);

        CREATE TABLE Stores (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            Address NVARCHAR(500) NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Stores_ResourceId ON Stores(ResourceId);

        CREATE TABLE Products (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            SKU NVARCHAR(50) NOT NULL,
            Price DECIMAL(10,2) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Products_ResourceId ON Products(ResourceId);

        -- D=10 additional domain tables
        CREATE TABLE Divisions (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Divisions_ResourceId ON Divisions(ResourceId);

        CREATE TABLE Districts (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Districts_ResourceId ON Districts(ResourceId);

        CREATE TABLE Areas (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Areas_ResourceId ON Areas(ResourceId);

        CREATE TABLE Zones (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Zones_ResourceId ON Zones(ResourceId);

        CREATE TABLE Departments (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Departments_ResourceId ON Departments(ResourceId);

        CREATE TABLE Sections (
            Id INT IDENTITY(1,1) PRIMARY KEY CLUSTERED,
            Name NVARCHAR(256) NOT NULL,
            ResourceId NVARCHAR(128) NOT NULL
        );
        CREATE NONCLUSTERED INDEX IX_Sections_ResourceId ON Sections(ResourceId);
    ");

    // Create the TVF (matches SqlOSFgaFunctionInitializer output)
    await ExecuteNonQueryAsync("DROP FUNCTION IF EXISTS dbo.fn_IsResourceAccessible");
    await ExecuteNonQueryAsync(@"
        CREATE FUNCTION dbo.fn_IsResourceAccessible(
            @ResourceId NVARCHAR(128),
            @PrincipalIds NVARCHAR(MAX),
            @PermissionId NVARCHAR(128)
        )
        RETURNS TABLE
        AS
        RETURN
        (
            WITH ancestors AS (
                SELECT Id, ParentId, 0 AS Depth
                FROM [dbo].[SqlOSFgaResources]
                WHERE Id = @ResourceId

                UNION ALL

                SELECT r.Id, r.ParentId, a.Depth + 1
                FROM [dbo].[SqlOSFgaResources] r
                INNER JOIN ancestors a ON r.Id = a.ParentId
                WHERE a.Depth < 10
            )
            SELECT TOP 1 a.Id
            FROM ancestors a
            INNER JOIN [dbo].[SqlOSFgaGrants] g ON a.Id = g.ResourceId
            INNER JOIN [dbo].[SqlOSFgaRolePermissions] rp ON g.RoleId = rp.RoleId
            WHERE g.PrincipalId IN (SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@PrincipalIds, ','))
              AND rp.PermissionId = @PermissionId
              AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
              AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
        )
    ");

    Console.WriteLine("  SqlOSFga schema + TVF created.");
}

// =============================================================================
// Data Seeding (Benchmarks 1-7) — Retail Domain Tables
// =============================================================================

// Retail hierarchy levels from leaf upward: product, store, region, chain, division
// For depth D, the leaf is always Products and we use D-1 intermediate retail levels.
// D=1: root → products
// D=2: root → stores → products
// D=3: root → regions → stores → products
// D=4: root → chains → regions → stores → products
// D=5: root → divisions → chains → regions → stores → products

string DomainInsertPrefix(string typeId) => typeId switch
{
    "store" => "INSERT INTO Stores (Name, Address, ResourceId) VALUES ",
    "region" => "INSERT INTO Regions (Name, ResourceId) VALUES ",
    "chain" => "INSERT INTO Chains (Name, ResourceId) VALUES ",
    "division" => "INSERT INTO Divisions (Name, ResourceId) VALUES ",
    _ => throw new ArgumentException($"No domain table for type: {typeId}")
};

string DomainValuesSql(string typeId, int index, string resId) => typeId switch
{
    "store" => $"('Store {index}','{index} Main St','{resId}')",
    "region" => $"('Region {index}','{resId}')",
    "chain" => $"('Chain {index}','{resId}')",
    "division" => $"('Division {index}','{resId}')",
    _ => throw new ArgumentException($"No domain table for type: {typeId}")
};

string DomainTableName(string typeId) => typeId switch
{
    "product" => "Products", "store" => "Stores", "region" => "Regions",
    "chain" => "Chains", "division" => "Divisions",
    _ => throw new ArgumentException($"No domain table for type: {typeId}")
};

async Task SeedDataAsync(int productCount, int depth, int principalGroupCount)
{
    await SetupSchemaAsync();
    var rng = new Random(42);

    // Retail hierarchy levels from leaf upward: product, store, region, chain, division
    string[] retailLevels = { "product", "store", "region", "chain", "division" };

    if (depth > retailLevels.Length)
        throw new ArgumentException($"Max supported depth is {retailLevels.Length}");

    // Seed core types
    var typesSql = new StringBuilder();
    typesSql.Append("INSERT INTO SqlOSFgaPrincipalTypes (Id, Name) VALUES ('user', 'User'), ('group', 'Group'), ('service_account', 'Service Account');");
    typesSql.Append("INSERT INTO SqlOSFgaResourceTypes (Id, Name) VALUES ('root', 'Root')");
    for (int i = 0; i < depth; i++)
    {
        var t = retailLevels[i];
        typesSql.Append($", ('{t}', '{char.ToUpper(t[0])}{t[1..]}')");
    }
    typesSql.Append(';');
    await ExecuteNonQueryAsync(typesSql.ToString());

    // Root resource
    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('root', NULL, 'Root', 'root');");
    int resourceCount = 1;

    // Build intermediate retail hierarchy (from root children down to just above products)
    // Level indices: depth-1 (closest to root) down to 1 (stores, just above products)
    const int branchingFactor = 10;
    var parentIds = new List<string> { "root" };

    for (int lvlIdx = depth - 1; lvlIdx >= 1; lvlIdx--)
    {
        var typeId = retailLevels[lvlIdx];
        var nextParents = new List<string>();
        var resBatch = new StringBuilder("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
        var domPrefix = DomainInsertPrefix(typeId);
        var domBatch = new StringBuilder(domPrefix);
        int resBc = 0, domBc = 0, nodeNum = 0;

        foreach (var parentId in parentIds)
        {
            for (int b = 1; b <= branchingFactor; b++)
            {
                nodeNum++;
                var nodeId = $"{typeId}_{nodeNum}";
                nextParents.Add(nodeId);

                if (resBc > 0) resBatch.Append(',');
                resBatch.Append($"('{nodeId}','{parentId}','{char.ToUpper(typeId[0])}{typeId[1..]} {nodeNum}','{typeId}')");
                resBc++;

                if (domBc > 0) domBatch.Append(',');
                domBatch.Append(DomainValuesSql(typeId, nodeNum, nodeId));
                domBc++;

                resourceCount++;

                if (resBc >= 500)
                {
                    resBatch.Append(';');
                    await ExecuteNonQueryAsync(resBatch.ToString());
                    resBatch.Clear().Append("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
                    resBc = 0;
                }
                if (domBc >= 500)
                {
                    domBatch.Append(';');
                    await ExecuteNonQueryAsync(domBatch.ToString());
                    domBatch.Clear().Append(domPrefix);
                    domBc = 0;
                }
            }
        }
        if (resBc > 0) { resBatch.Append(';'); await ExecuteNonQueryAsync(resBatch.ToString()); }
        if (domBc > 0) { domBatch.Append(';'); await ExecuteNonQueryAsync(domBatch.ToString()); }
        parentIds = nextParents;
    }

    // Leaf level: Products (every product gets its own resource)
    var prodResBatch = new StringBuilder("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
    var prodBatch = new StringBuilder("INSERT INTO Products (Name, SKU, Price, ResourceId) VALUES ");
    int prodResBc = 0, prodBc = 0;

    for (int i = 0; i < productCount; i++)
    {
        var parentId = parentIds[i % parentIds.Count];
        var prodResId = $"prd_{i + 1}";
        var price = (rng.Next(99, 99999) / 100.0m).ToString("F2");

        if (prodResBc > 0) prodResBatch.Append(',');
        prodResBatch.Append($"('{prodResId}','{parentId}','Product {i + 1}','product')");
        prodResBc++;

        if (prodBc > 0) prodBatch.Append(',');
        prodBatch.Append($"('Product {i + 1}','SKU-{(i + 1):D7}',{price},'{prodResId}')");
        prodBc++;

        resourceCount++;

        if (prodResBc >= 500)
        {
            prodResBatch.Append(';');
            await ExecuteNonQueryAsync(prodResBatch.ToString());
            prodResBatch.Clear().Append("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
            prodResBc = 0;
        }
        if (prodBc >= 500)
        {
            prodBatch.Append(';');
            await ExecuteNonQueryAsync(prodBatch.ToString());
            prodBatch.Clear().Append("INSERT INTO Products (Name, SKU, Price, ResourceId) VALUES ");
            prodBc = 0;
        }
    }
    if (prodResBc > 0) { prodResBatch.Append(';'); await ExecuteNonQueryAsync(prodResBatch.ToString()); }
    if (prodBc > 0) { prodBatch.Append(';'); await ExecuteNonQueryAsync(prodBatch.ToString()); }

    // Roles + Permissions
    await ExecuteNonQueryAsync(@"
        INSERT INTO SqlOSFgaRoles (Id, [Key], Name) VALUES ('admin', 'Admin', 'Admin'), ('viewer', 'Viewer', 'Viewer');
        INSERT INTO SqlOSFgaPermissions (Id, [Key], Name) VALUES ('product_view', 'PRODUCT_VIEW', 'View Products'), ('product_edit', 'PRODUCT_EDIT', 'Edit Products');
        INSERT INTO SqlOSFgaRolePermissions (RoleId, PermissionId) VALUES
            ('admin', 'product_view'), ('admin', 'product_edit'),
            ('viewer', 'product_view');
    ");

    // Principal (user) + groups
    var sb = new StringBuilder();
    sb.AppendLine("INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('user_1', 'user', 'Test User');");
    for (int g = 1; g <= principalGroupCount; g++)
    {
        sb.AppendLine($"INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('group_{g}', 'group', 'Group {g}');");
        sb.AppendLine($"INSERT INTO SqlOSFgaUserGroups (Id, PrincipalId, Name) VALUES ('ug_{g}', 'group_{g}', 'UserGroup {g}');");
        sb.AppendLine($"INSERT INTO SqlOSFgaUserGroupMemberships (PrincipalId, UserGroupId) VALUES ('user_1', 'ug_{g}');");
    }
    await ExecuteNonQueryAsync(sb.ToString());

    // Grant at root (full access)
    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES ('grant_1', 'user_1', 'viewer', 'root');");

    // Materialized permissions (expand grants through hierarchy for baseline comparison)
    await ExecuteNonQueryAsync($@"
        ;WITH grant_expansions AS (
            SELECT g.PrincipalId, g.RoleId, r.Id AS ResourceId
            FROM SqlOSFgaGrants g
            INNER JOIN SqlOSFgaResources r ON r.Id = g.ResourceId
            WHERE (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
              AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
            UNION ALL
            SELECT ge.PrincipalId, ge.RoleId, r.Id
            FROM SqlOSFgaResources r
            INNER JOIN grant_expansions ge ON r.ParentId = ge.ResourceId
        )
        INSERT INTO MaterializedPermissions (PrincipalId, PermissionId, ResourceId)
        SELECT DISTINCT ge.PrincipalId, rp.PermissionId, ge.ResourceId
        FROM grant_expansions ge
        INNER JOIN SqlOSFgaRolePermissions rp ON ge.RoleId = rp.RoleId
        OPTION (MAXRECURSION {Math.Max(100, depth + 10)});
    ");

    // Update stats on all used tables
    var statsSql = new StringBuilder("UPDATE STATISTICS SqlOSFgaResources; UPDATE STATISTICS SqlOSFgaGrants; UPDATE STATISTICS SqlOSFgaRolePermissions; UPDATE STATISTICS Products; UPDATE STATISTICS MaterializedPermissions;");
    for (int i = depth - 1; i >= 1; i--)
        statsSql.Append($" UPDATE STATISTICS {DomainTableName(retailLevels[i])};");
    await ExecuteNonQueryAsync(statsSql.ToString());

    var prodCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM Products");
    var resCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM SqlOSFgaResources");
    var matCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM MaterializedPermissions");
    Console.WriteLine($"  Seeded: {prodCount} products, {resCount} resources, {matCount} mat. perms, depth={depth}, M={principalGroupCount + 1}");
}

// =============================================================================
// Benchmark Helpers
// =============================================================================

async Task<(double medianMs, double p95Ms, double iqrMs)> MeasureQueryAsync(string sql, int warmupRuns = 3, int measuredRuns = 20)
{
    for (int i = 0; i < warmupRuns; i++)
    {
        await using var conn = await GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
    }

    var timings = new List<double>();

    for (int i = 0; i < measuredRuns; i++)
    {
        await using var conn = await GetConnectionAsync();
        var sw = Stopwatch.StartNew();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
        sw.Stop();
        timings.Add(sw.Elapsed.TotalMilliseconds);
    }

    timings.Sort();
    double median = timings[timings.Count / 2];
    double p95 = timings[(int)(timings.Count * 0.95)];
    double q1 = timings[timings.Count / 4];
    double q3 = timings[(int)(timings.Count * 0.75)];
    double iqr = q3 - q1;

    return (median, p95, iqr);
}

string BuildTvfQuery(string principalIds, int pageSize, int offset = 0, bool useCursor = true, int? cursorId = null, string permission = "product_view")
{
    if (useCursor && cursorId.HasValue)
    {
        return $@"
            SELECT TOP {pageSize + 1} p.Id, p.Name, p.SKU, p.Price, p.ResourceId
            FROM Products p
            WHERE EXISTS (
                SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', '{permission}')
            )
            AND p.Id > {cursorId.Value}
            ORDER BY p.Id";
    }
    else if (useCursor)
    {
        return $@"
            SELECT TOP {pageSize + 1} p.Id, p.Name, p.SKU, p.Price, p.ResourceId
            FROM Products p
            WHERE EXISTS (
                SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', '{permission}')
            )
            ORDER BY p.Id";
    }
    else
    {
        return $@"
            SELECT p.Id, p.Name, p.SKU, p.Price, p.ResourceId
            FROM Products p
            WHERE EXISTS (
                SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', '{permission}')
            )
            ORDER BY p.Id
            OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
    }
}

string BuildTvfCountQuery(string principalIds)
{
    return $@"
        SELECT COUNT(*)
        FROM Products p
        WHERE EXISTS (
            SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', 'product_view')
        )";
}

string BuildPointCheckQuery(string resourceId, string principalIds, string permission = "product_view")
{
    return $@"
        SELECT TOP 1 Id
        FROM dbo.fn_IsResourceAccessible('{resourceId}', '{principalIds}', '{permission}')";
}

string BuildMaterializedJoinQuery(string principalId, int pageSize)
{
    return $@"
        SELECT TOP {pageSize + 1} p.Id, p.Name, p.SKU, p.Price, p.ResourceId
        FROM Products p
        INNER JOIN MaterializedPermissions mp
            ON p.ResourceId = mp.ResourceId
            AND mp.PrincipalId = '{principalId}'
            AND mp.PermissionId = 'product_view'
        ORDER BY p.Id";
}

// =============================================================================
// Benchmark Suites 1-7: Small-Scale Isolation Tests
// =============================================================================

// ---------------------------------------------------------------------------
// Benchmark 1: Product Count Scaling
// ---------------------------------------------------------------------------
async Task<List<BenchmarkResult>> RunEntityCountScalingAsync()
{
    var results = new List<BenchmarkResult>();
    var productCounts = new[] { 1_000, 5_000, 10_000, 50_000, 100_000 };
    const int pageSize = 20;

    foreach (var n in productCounts)
    {
        Console.Write($"  N={n:N0}...");
        await SeedDataAsync(productCount: n, depth: 3, principalGroupCount: 3);

        var principalIds = "user_1,group_1,group_2,group_3";
        var query = BuildTvfQuery(principalIds, pageSize);

        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");

        results.Add(new("Product Scaling (TVF cursor)", $"N={n:N0}", median, p95, iqr, pageSize, "TVF EXISTS"));
    }
    return results;
}

// ---------------------------------------------------------------------------
// Benchmark 2: Hierarchy Depth Sensitivity
// ---------------------------------------------------------------------------
async Task<List<BenchmarkResult>> RunDepthSensitivityAsync()
{
    var results = new List<BenchmarkResult>();
    var depths = new[] { 1, 2, 3, 4, 5 };
    const int productCount = 10_000;
    const int pageSize = 20;

    foreach (var d in depths)
    {
        Console.Write($"  D={d}...");
        await SeedDataAsync(productCount: productCount, depth: d, principalGroupCount: 3);

        var principalIds = "user_1,group_1,group_2,group_3";
        var query = BuildTvfQuery(principalIds, pageSize);

        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");

        results.Add(new("Depth Sensitivity", $"D={d}", median, p95, iqr, pageSize, "TVF EXISTS"));
    }
    return results;
}

// ---------------------------------------------------------------------------
// Benchmark 3: Principal Set Size Sensitivity
// ---------------------------------------------------------------------------
async Task<List<BenchmarkResult>> RunPrincipalSetSensitivityAsync()
{
    var results = new List<BenchmarkResult>();
    var groupCounts = new[] { 0, 3, 5, 10, 20 };
    const int productCount = 10_000;
    const int pageSize = 20;

    foreach (var m in groupCounts)
    {
        Console.Write($"  M={m + 1}...");
        await SeedDataAsync(productCount: productCount, depth: 3, principalGroupCount: m);

        var principalIds = new StringBuilder("user_1");
        for (int g = 1; g <= m; g++) principalIds.Append($",group_{g}");

        var query = BuildTvfQuery(principalIds.ToString(), pageSize);

        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");

        results.Add(new("Principal Set Size", $"M={m + 1}", median, p95, iqr, pageSize, "TVF EXISTS"));
    }
    return results;
}

// ---------------------------------------------------------------------------
// Benchmark 6: TVF EXISTS vs Materialized Permissions
// ---------------------------------------------------------------------------
async Task<List<BenchmarkResult>> RunBaselineComparisonAsync()
{
    var results = new List<BenchmarkResult>();
    const int pageSize = 20;

    foreach (var n in new[] { 10_000, 50_000, 100_000 })
    {
        Console.WriteLine($"  N={n:N0}:");
        await SeedDataAsync(productCount: n, depth: 3, principalGroupCount: 3);
        var principalIds = "user_1,group_1,group_2,group_3";

        Console.Write("    TVF EXISTS...");
        var (tvfMedian, tvfP95, tvfIqr) = await MeasureQueryAsync(BuildTvfQuery(principalIds, pageSize));
        Console.WriteLine($" median={tvfMedian:F2}ms, p95={tvfP95:F2}ms, iqr={tvfIqr:F2}ms");
        results.Add(new("Baseline Comparison", $"TVF N={n:N0}", tvfMedian, tvfP95, tvfIqr, pageSize, "TVF EXISTS"));

        Console.Write("    Materialized JOIN...");
        var (matMedian, matP95, matIqr) = await MeasureQueryAsync(BuildMaterializedJoinQuery("user_1", pageSize));
        Console.WriteLine($" median={matMedian:F2}ms, p95={matP95:F2}ms, iqr={matIqr:F2}ms");
        results.Add(new("Baseline Comparison", $"Materialized N={n:N0}", matMedian, matP95, matIqr, pageSize, "Materialized JOIN"));
    }
    return results;
}

// ---------------------------------------------------------------------------
// Benchmark 7: Offset vs Cursor Pagination + COUNT
// ---------------------------------------------------------------------------
async Task<List<BenchmarkResult>> RunPaginationComparisonAsync()
{
    var results = new List<BenchmarkResult>();

    foreach (var n in new[] { 10_000, 50_000, 100_000 })
    {
        Console.WriteLine($"  N={n:N0}:");
        await SeedDataAsync(productCount: n, depth: 3, principalGroupCount: 3);
        var principalIds = "user_1,group_1,group_2,group_3";
        const int pageSize = 20;

        Console.Write("    Cursor (page 1)...");
        var (cursorMedian, cursorP95, cursorIqr) = await MeasureQueryAsync(BuildTvfQuery(principalIds, pageSize, useCursor: true));
        Console.WriteLine($" median={cursorMedian:F2}ms, p95={cursorP95:F2}ms, iqr={cursorIqr:F2}ms");
        results.Add(new("Pagination", $"Cursor p1 N={n:N0}", cursorMedian, cursorP95, cursorIqr, pageSize, "Cursor"));

        Console.Write("    Offset (page 1)...");
        var (offsetMedian, offsetP95, offsetIqr) = await MeasureQueryAsync(BuildTvfQuery(principalIds, pageSize, useCursor: false, offset: 0));
        Console.WriteLine($" median={offsetMedian:F2}ms, p95={offsetP95:F2}ms, iqr={offsetIqr:F2}ms");
        results.Add(new("Pagination", $"Offset p1 N={n:N0}", offsetMedian, offsetP95, offsetIqr, pageSize, "Offset"));

        Console.Write("    COUNT query...");
        var (countMedian, countP95, countIqr) = await MeasureQueryAsync(BuildTvfCountQuery(principalIds));
        Console.WriteLine($" median={countMedian:F2}ms, p95={countP95:F2}ms, iqr={countIqr:F2}ms");
        results.Add(new("Pagination", $"COUNT N={n:N0}", countMedian, countP95, countIqr, pageSize, "Offset COUNT"));
    }
    return results;
}

// =============================================================================
// Domain Query Builders (real app tables)
// =============================================================================

string BuildProductQuery(string principalIds, int pageSize, int? cursorId = null, string permission = "product_view")
{
    if (cursorId.HasValue)
    {
        return $@"
            SELECT TOP {pageSize + 1} p.Id, p.Name, p.SKU, p.Price, p.ResourceId
            FROM Products p
            WHERE EXISTS (
                SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', '{permission}')
            )
            AND p.Id > {cursorId.Value}
            ORDER BY p.Id";
    }
    return $@"
        SELECT TOP {pageSize + 1} p.Id, p.Name, p.SKU, p.Price, p.ResourceId
        FROM Products p
        WHERE EXISTS (
            SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', '{permission}')
        )
        ORDER BY p.Id";
}

// =============================================================================
// Benchmarks 8-13: Resource-Scale Tests with Real Domain Tables
// =============================================================================
//
// D=5 hierarchy: root → 15 chains → 150 regions → 15K stores → 1.2M products
// = 1,215,166 total resources. Every row IS a resource (1:1 mapping).
//
// D=10 hierarchy: root → 5 div → 25 reg → 125 dist → 500 areas → 2K zones
// → 12K stores → 60K depts → 240K sections → 1.2M products = ~1,514,656 resources.
//

// =============================================================================
// D=5 Seed: 1.2M Resources with Real Domain Tables
// =============================================================================

async Task<D5Principals> SeedRetailD5Async()
{
    await SetupSchemaAsync();

    var rng = new Random(42); // deterministic for reproducibility

    // Core types
    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaPrincipalTypes (Id, Name) VALUES ('user', 'User'), ('group', 'Group'), ('service_account', 'Service Account');");
    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaResourceTypes (Id, Name) VALUES ('root', 'Root'), ('chain', 'Chain'), ('region', 'Region'), ('store', 'Store'), ('product', 'Product');");

    const int chainCount = 15;
    const int regionsPerChain = 10;
    const int storesPerRegion = 100;
    const int productsPerStore = 80;

    // Root resource
    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('retail_root', NULL, 'Retail Root', 'root');");
    int resourceCount = 1;

    // Chains (L1): 15
    Console.Write("    Inserting chains...");
    var resSb = new StringBuilder();
    var domSb = new StringBuilder();
    for (int c = 1; c <= chainCount; c++)
    {
        var chainId = $"chain_{c}";
        resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{chainId}', 'retail_root', 'Chain {c}', 'chain');");
        domSb.AppendLine($"INSERT INTO Chains (Name, ResourceId) VALUES ('Chain {c}', '{chainId}');");
        resourceCount++;
    }
    await ExecuteNonQueryAsync(resSb.ToString());
    await ExecuteNonQueryAsync(domSb.ToString());
    Console.WriteLine($" {chainCount}");

    // Regions (L2): 150
    Console.Write("    Inserting regions...");
    resSb.Clear(); domSb.Clear();
    for (int c = 1; c <= chainCount; c++)
    {
        for (int r = 1; r <= regionsPerChain; r++)
        {
            var regionId = $"reg_{c}_{r}";
            resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{regionId}', 'chain_{c}', 'Region {c}-{r}', 'region');");
            domSb.AppendLine($"INSERT INTO Regions (Name, ResourceId) VALUES ('Region {c}-{r}', '{regionId}');");
            resourceCount++;
        }
    }
    await ExecuteNonQueryAsync(resSb.ToString());
    await ExecuteNonQueryAsync(domSb.ToString());
    Console.WriteLine($" {chainCount * regionsPerChain}");

    // Stores (L3): 15,000
    Console.Write("    Inserting stores...");
    resSb.Clear(); domSb.Clear();
    int storeCount = 0;
    for (int c = 1; c <= chainCount; c++)
    for (int r = 1; r <= regionsPerChain; r++)
    for (int s = 1; s <= storesPerRegion; s++)
    {
        var storeId = $"str_{c}_{r}_{s}";
        resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{storeId}', 'reg_{c}_{r}', 'Store {c}-{r}-{s}', 'store');");
        domSb.AppendLine($"INSERT INTO Stores (Name, Address, ResourceId) VALUES ('Store {c}-{r}-{s}', '{storeCount + 100} Main St', '{storeId}');");
        storeCount++;
        resourceCount++;

        if (resSb.Length > 50000)
        {
            await ExecuteNonQueryAsync(resSb.ToString());
            resSb.Clear();
        }
        if (domSb.Length > 50000)
        {
            await ExecuteNonQueryAsync(domSb.ToString());
            domSb.Clear();
        }
    }
    if (resSb.Length > 0) await ExecuteNonQueryAsync(resSb.ToString());
    if (domSb.Length > 0) await ExecuteNonQueryAsync(domSb.ToString());
    Console.WriteLine($" {storeCount:N0}");

    // Products (L4): 1,200,000 — multi-row VALUES batching
    Console.Write("    Inserting products (1.2M)...");
    var prodSw = Stopwatch.StartNew();
    int productCount = 0;

    var resBatch = new StringBuilder("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
    var prodBatch = new StringBuilder("INSERT INTO Products (Name, SKU, Price, ResourceId) VALUES ");
    int resBatchCount = 0, prodBatchCount = 0;
    const int batchSize = 500;

    for (int c = 1; c <= chainCount; c++)
    for (int r = 1; r <= regionsPerChain; r++)
    for (int s = 1; s <= storesPerRegion; s++)
    {
        var storeResId = $"str_{c}_{r}_{s}";
        for (int p = 1; p <= productsPerStore; p++)
        {
            productCount++;
            var prodResId = $"prd_{c}_{r}_{s}_{p}";
            var price = (rng.Next(99, 99999) / 100.0m).ToString("F2");

            if (resBatchCount > 0) resBatch.Append(',');
            resBatch.Append($"('{prodResId}','{storeResId}','Product {productCount}','product')");
            resBatchCount++;

            if (prodBatchCount > 0) prodBatch.Append(',');
            prodBatch.Append($"('Product {productCount}','SKU-{productCount:D7}',{price},'{prodResId}')");
            prodBatchCount++;

            resourceCount++;

            if (resBatchCount >= batchSize)
            {
                resBatch.Append(';');
                await ExecuteNonQueryAsync(resBatch.ToString());
                resBatch.Clear();
                resBatch.Append("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
                resBatchCount = 0;
            }
            if (prodBatchCount >= batchSize)
            {
                prodBatch.Append(';');
                await ExecuteNonQueryAsync(prodBatch.ToString());
                prodBatch.Clear();
                prodBatch.Append("INSERT INTO Products (Name, SKU, Price, ResourceId) VALUES ");
                prodBatchCount = 0;
            }
        }
    }
    if (resBatchCount > 0) { resBatch.Append(';'); await ExecuteNonQueryAsync(resBatch.ToString()); }
    if (prodBatchCount > 0) { prodBatch.Append(';'); await ExecuteNonQueryAsync(prodBatch.ToString()); }
    prodSw.Stop();
    Console.WriteLine($" {productCount:N0} ({prodSw.Elapsed.TotalSeconds:F1}s)");

    Console.WriteLine($"    Total resources: {resourceCount:N0}");

    // Roles + Permissions
    await ExecuteNonQueryAsync(@"
        INSERT INTO SqlOSFgaRoles (Id, [Key], Name) VALUES
            ('company_admin', 'CompanyAdmin', 'Company Admin'),
            ('chain_manager', 'ChainManager', 'Chain Manager'),
            ('region_manager', 'RegionManager', 'Region Manager'),
            ('store_manager', 'StoreManager', 'Store Manager');
        INSERT INTO SqlOSFgaPermissions (Id, [Key], Name) VALUES
            ('product_view', 'PRODUCT_VIEW', 'View Products'),
            ('product_edit', 'PRODUCT_EDIT', 'Edit Products'),
            ('store_view', 'STORE_VIEW', 'View Stores');
        INSERT INTO SqlOSFgaRolePermissions (RoleId, PermissionId) VALUES
            ('company_admin', 'product_view'), ('company_admin', 'product_edit'), ('company_admin', 'store_view'),
            ('chain_manager', 'product_view'), ('chain_manager', 'product_edit'), ('chain_manager', 'store_view'),
            ('region_manager', 'product_view'), ('region_manager', 'product_edit'), ('region_manager', 'store_view'),
            ('store_manager', 'product_view'), ('store_manager', 'store_view');
    ");

    // Principals: 4 users + 2 groups each (M=3)
    var principalSb = new StringBuilder();
    var userNames = new[] { "d5_admin", "d5_chain_mgr", "d5_region_mgr", "d5_store_mgr" };
    foreach (var u in userNames)
    {
        principalSb.AppendLine($"INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('{u}', 'user', '{u}');");
        for (int g = 1; g <= 2; g++)
        {
            var groupId = $"{u}_grp{g}";
            var ugId = $"{u}_ug{g}";
            principalSb.AppendLine($"INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('{groupId}', 'group', '{groupId}');");
            principalSb.AppendLine($"INSERT INTO SqlOSFgaUserGroups (Id, PrincipalId, Name) VALUES ('{ugId}', '{groupId}', '{u} Group {g}');");
            principalSb.AppendLine($"INSERT INTO SqlOSFgaUserGroupMemberships (PrincipalId, UserGroupId) VALUES ('{u}', '{ugId}');");
        }
    }
    await ExecuteNonQueryAsync(principalSb.ToString());

    // Grants at different hierarchy levels
    await ExecuteNonQueryAsync(@"
        INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES
            ('g_d5_admin',  'd5_admin',      'company_admin',  'retail_root'),
            ('g_d5_chain',  'd5_chain_mgr',  'chain_manager',  'chain_1'),
            ('g_d5_region', 'd5_region_mgr', 'region_manager', 'reg_1_1'),
            ('g_d5_store',  'd5_store_mgr',  'store_manager',  'str_1_1_1');
    ");

    // Update statistics
    Console.Write("    Updating statistics...");
    await ExecuteNonQueryAsync(@"
        UPDATE STATISTICS SqlOSFgaResources;
        UPDATE STATISTICS SqlOSFgaGrants;
        UPDATE STATISTICS SqlOSFgaRolePermissions;
        UPDATE STATISTICS Products;
        UPDATE STATISTICS Stores;
        UPDATE STATISTICS Regions;
        UPDATE STATISTICS Chains;
    ");
    Console.WriteLine(" done");

    var resCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM SqlOSFgaResources");
    var prodCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM Products");
    Console.WriteLine($"    Final: {resCount:N0} resources, {prodCount:N0} products (D=5 hierarchy)");

    return new D5Principals(
        CompanyAdmin: "d5_admin,d5_admin_grp1,d5_admin_grp2",
        ChainMgr: "d5_chain_mgr,d5_chain_mgr_grp1,d5_chain_mgr_grp2",
        RegionMgr: "d5_region_mgr,d5_region_mgr_grp1,d5_region_mgr_grp2",
        StoreMgr: "d5_store_mgr,d5_store_mgr_grp1,d5_store_mgr_grp2"
    );
}

// =============================================================================
// D=10 Seed: ~1.5M Resources with Real Domain Tables
// =============================================================================

async Task<D10Principals> SeedRetailD10Async()
{
    await SetupSchemaAsync();

    var rng = new Random(42);

    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaPrincipalTypes (Id, Name) VALUES ('user', 'User'), ('group', 'Group'), ('service_account', 'Service Account');");
    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaResourceTypes (Id, Name) VALUES ('root', 'Root'), ('division', 'Division'), ('region', 'Region'), ('district', 'District'), ('area', 'Area'), ('zone', 'Zone'), ('store', 'Store'), ('department', 'Department'), ('section', 'Section'), ('product', 'Product');");

    const int divCount = 5;
    const int regsPerDiv = 5;
    const int distsPerReg = 5;
    const int areasPerDist = 4;
    const int zonesPerArea = 4;
    const int storesPerZone = 6;
    const int deptsPerStore = 5;
    const int sectsPerDept = 4;
    const int prodsPerSect = 5;

    await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('retail_root', NULL, 'Retail Root', 'root');");
    int resourceCount = 1;

    // L1-L5: Divisions → Regions → Districts → Areas → Zones
    Console.Write("    Inserting L1-L5 (divisions through zones)...");
    var resSb = new StringBuilder();
    var domSb = new StringBuilder();

    for (int dv = 1; dv <= divCount; dv++)
    {
        var divId = $"div_{dv}";
        resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{divId}', 'retail_root', 'Division {dv}', 'division');");
        domSb.AppendLine($"INSERT INTO Divisions (Name, ResourceId) VALUES ('Division {dv}', '{divId}');");
        resourceCount++;

        for (int rg = 1; rg <= regsPerDiv; rg++)
        {
            var regId = $"d10_reg_{dv}_{rg}";
            resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{regId}', '{divId}', 'Region {dv}-{rg}', 'region');");
            domSb.AppendLine($"INSERT INTO Regions (Name, ResourceId) VALUES ('Region {dv}-{rg}', '{regId}');");
            resourceCount++;

            for (int ds = 1; ds <= distsPerReg; ds++)
            {
                var distId = $"dist_{dv}_{rg}_{ds}";
                resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{distId}', '{regId}', 'District {dv}-{rg}-{ds}', 'district');");
                domSb.AppendLine($"INSERT INTO Districts (Name, ResourceId) VALUES ('District {dv}-{rg}-{ds}', '{distId}');");
                resourceCount++;

                for (int ar = 1; ar <= areasPerDist; ar++)
                {
                    var areaId = $"area_{dv}_{rg}_{ds}_{ar}";
                    resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{areaId}', '{distId}', 'Area {dv}-{rg}-{ds}-{ar}', 'area');");
                    domSb.AppendLine($"INSERT INTO Areas (Name, ResourceId) VALUES ('Area {dv}-{rg}-{ds}-{ar}', '{areaId}');");
                    resourceCount++;

                    for (int zn = 1; zn <= zonesPerArea; zn++)
                    {
                        var zoneId = $"zone_{dv}_{rg}_{ds}_{ar}_{zn}";
                        resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{zoneId}', '{areaId}', 'Zone {dv}-{rg}-{ds}-{ar}-{zn}', 'zone');");
                        domSb.AppendLine($"INSERT INTO Zones (Name, ResourceId) VALUES ('Zone {dv}-{rg}-{ds}-{ar}-{zn}', '{zoneId}');");
                        resourceCount++;

                        if (resSb.Length > 50000) { await ExecuteNonQueryAsync(resSb.ToString()); resSb.Clear(); }
                        if (domSb.Length > 50000) { await ExecuteNonQueryAsync(domSb.ToString()); domSb.Clear(); }
                    }
                }
            }
        }
    }
    if (resSb.Length > 0) { await ExecuteNonQueryAsync(resSb.ToString()); resSb.Clear(); }
    if (domSb.Length > 0) { await ExecuteNonQueryAsync(domSb.ToString()); domSb.Clear(); }
    Console.WriteLine($" {resourceCount - 1:N0} resources");

    // L6: Stores (12,000)
    Console.Write("    Inserting stores (12K)...");
    int storeCount = 0;
    for (int dv = 1; dv <= divCount; dv++)
    for (int rg = 1; rg <= regsPerDiv; rg++)
    for (int ds = 1; ds <= distsPerReg; ds++)
    for (int ar = 1; ar <= areasPerDist; ar++)
    for (int zn = 1; zn <= zonesPerArea; zn++)
    {
        var zoneId = $"zone_{dv}_{rg}_{ds}_{ar}_{zn}";
        for (int st = 1; st <= storesPerZone; st++)
        {
            storeCount++;
            var storeId = $"d10_str_{dv}_{rg}_{ds}_{ar}_{zn}_{st}";
            resSb.AppendLine($"INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ('{storeId}', '{zoneId}', 'Store {storeCount}', 'store');");
            domSb.AppendLine($"INSERT INTO Stores (Name, Address, ResourceId) VALUES ('Store {storeCount}', '{storeCount} Oak Ave', '{storeId}');");
            resourceCount++;
            if (resSb.Length > 50000) { await ExecuteNonQueryAsync(resSb.ToString()); resSb.Clear(); }
            if (domSb.Length > 50000) { await ExecuteNonQueryAsync(domSb.ToString()); domSb.Clear(); }
        }
    }
    if (resSb.Length > 0) { await ExecuteNonQueryAsync(resSb.ToString()); resSb.Clear(); }
    if (domSb.Length > 0) { await ExecuteNonQueryAsync(domSb.ToString()); domSb.Clear(); }
    Console.WriteLine($" {storeCount:N0}");

    // L7: Departments (60,000) — multi-row VALUES
    Console.Write("    Inserting departments (60K)...");
    int deptCount = 0;
    var deptResBatch = new StringBuilder("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
    var deptDomBatch = new StringBuilder("INSERT INTO Departments (Name, ResourceId) VALUES ");
    int deptResBc = 0, deptDomBc = 0;

    for (int dv = 1; dv <= divCount; dv++)
    for (int rg = 1; rg <= regsPerDiv; rg++)
    for (int ds = 1; ds <= distsPerReg; ds++)
    for (int ar = 1; ar <= areasPerDist; ar++)
    for (int zn = 1; zn <= zonesPerArea; zn++)
    for (int st = 1; st <= storesPerZone; st++)
    {
        var storeId = $"d10_str_{dv}_{rg}_{ds}_{ar}_{zn}_{st}";
        for (int dp = 1; dp <= deptsPerStore; dp++)
        {
            deptCount++;
            var deptId = $"dept_{dv}_{rg}_{ds}_{ar}_{zn}_{st}_{dp}";
            if (deptResBc > 0) deptResBatch.Append(',');
            deptResBatch.Append($"('{deptId}','{storeId}','Department {deptCount}','department')");
            deptResBc++;
            if (deptDomBc > 0) deptDomBatch.Append(',');
            deptDomBatch.Append($"('Department {deptCount}','{deptId}')");
            deptDomBc++;
            resourceCount++;

            if (deptResBc >= 500) { deptResBatch.Append(';'); await ExecuteNonQueryAsync(deptResBatch.ToString()); deptResBatch.Clear(); deptResBatch.Append("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES "); deptResBc = 0; }
            if (deptDomBc >= 500) { deptDomBatch.Append(';'); await ExecuteNonQueryAsync(deptDomBatch.ToString()); deptDomBatch.Clear(); deptDomBatch.Append("INSERT INTO Departments (Name, ResourceId) VALUES "); deptDomBc = 0; }
        }
    }
    if (deptResBc > 0) { deptResBatch.Append(';'); await ExecuteNonQueryAsync(deptResBatch.ToString()); }
    if (deptDomBc > 0) { deptDomBatch.Append(';'); await ExecuteNonQueryAsync(deptDomBatch.ToString()); }
    Console.WriteLine($" {deptCount:N0}");

    // L8: Sections (240,000)
    Console.Write("    Inserting sections (240K)...");
    int sectCount = 0;
    var sectResBatch = new StringBuilder("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
    var sectDomBatch = new StringBuilder("INSERT INTO Sections (Name, ResourceId) VALUES ");
    int sectResBc = 0, sectDomBc = 0;

    for (int dv = 1; dv <= divCount; dv++)
    for (int rg = 1; rg <= regsPerDiv; rg++)
    for (int ds = 1; ds <= distsPerReg; ds++)
    for (int ar = 1; ar <= areasPerDist; ar++)
    for (int zn = 1; zn <= zonesPerArea; zn++)
    for (int st = 1; st <= storesPerZone; st++)
    for (int dp = 1; dp <= deptsPerStore; dp++)
    {
        var deptId = $"dept_{dv}_{rg}_{ds}_{ar}_{zn}_{st}_{dp}";
        for (int sc = 1; sc <= sectsPerDept; sc++)
        {
            sectCount++;
            var sectId = $"sect_{dv}_{rg}_{ds}_{ar}_{zn}_{st}_{dp}_{sc}";
            if (sectResBc > 0) sectResBatch.Append(',');
            sectResBatch.Append($"('{sectId}','{deptId}','Section {sectCount}','section')");
            sectResBc++;
            if (sectDomBc > 0) sectDomBatch.Append(',');
            sectDomBatch.Append($"('Section {sectCount}','{sectId}')");
            sectDomBc++;
            resourceCount++;

            if (sectResBc >= 500) { sectResBatch.Append(';'); await ExecuteNonQueryAsync(sectResBatch.ToString()); sectResBatch.Clear(); sectResBatch.Append("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES "); sectResBc = 0; }
            if (sectDomBc >= 500) { sectDomBatch.Append(';'); await ExecuteNonQueryAsync(sectDomBatch.ToString()); sectDomBatch.Clear(); sectDomBatch.Append("INSERT INTO Sections (Name, ResourceId) VALUES "); sectDomBc = 0; }
        }
    }
    if (sectResBc > 0) { sectResBatch.Append(';'); await ExecuteNonQueryAsync(sectResBatch.ToString()); }
    if (sectDomBc > 0) { sectDomBatch.Append(';'); await ExecuteNonQueryAsync(sectDomBatch.ToString()); }
    Console.WriteLine($" {sectCount:N0}");

    // L9: Products (1,200,000)
    Console.Write("    Inserting products (1.2M)...");
    var d10ProdSw = Stopwatch.StartNew();
    int d10ProdCount = 0;
    var pResBatch = new StringBuilder("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES ");
    var pDomBatch = new StringBuilder("INSERT INTO Products (Name, SKU, Price, ResourceId) VALUES ");
    int pResBc = 0, pDomBc = 0;

    for (int dv = 1; dv <= divCount; dv++)
    for (int rg = 1; rg <= regsPerDiv; rg++)
    for (int ds = 1; ds <= distsPerReg; ds++)
    for (int ar = 1; ar <= areasPerDist; ar++)
    for (int zn = 1; zn <= zonesPerArea; zn++)
    for (int st = 1; st <= storesPerZone; st++)
    for (int dp = 1; dp <= deptsPerStore; dp++)
    for (int sc = 1; sc <= sectsPerDept; sc++)
    {
        var sectId = $"sect_{dv}_{rg}_{ds}_{ar}_{zn}_{st}_{dp}_{sc}";
        for (int pr = 1; pr <= prodsPerSect; pr++)
        {
            d10ProdCount++;
            var prodResId = $"d10prd_{dv}_{rg}_{ds}_{ar}_{zn}_{st}_{dp}_{sc}_{pr}";
            var price = (rng.Next(99, 99999) / 100.0m).ToString("F2");

            if (pResBc > 0) pResBatch.Append(',');
            pResBatch.Append($"('{prodResId}','{sectId}','Product {d10ProdCount}','product')");
            pResBc++;
            if (pDomBc > 0) pDomBatch.Append(',');
            pDomBatch.Append($"('Product {d10ProdCount}','SKU-{d10ProdCount:D7}',{price},'{prodResId}')");
            pDomBc++;
            resourceCount++;

            if (pResBc >= 500) { pResBatch.Append(';'); await ExecuteNonQueryAsync(pResBatch.ToString()); pResBatch.Clear(); pResBatch.Append("INSERT INTO SqlOSFgaResources (Id, ParentId, Name, ResourceTypeId) VALUES "); pResBc = 0; }
            if (pDomBc >= 500) { pDomBatch.Append(';'); await ExecuteNonQueryAsync(pDomBatch.ToString()); pDomBatch.Clear(); pDomBatch.Append("INSERT INTO Products (Name, SKU, Price, ResourceId) VALUES "); pDomBc = 0; }
        }
    }
    if (pResBc > 0) { pResBatch.Append(';'); await ExecuteNonQueryAsync(pResBatch.ToString()); }
    if (pDomBc > 0) { pDomBatch.Append(';'); await ExecuteNonQueryAsync(pDomBatch.ToString()); }
    d10ProdSw.Stop();
    Console.WriteLine($" {d10ProdCount:N0} ({d10ProdSw.Elapsed.TotalSeconds:F1}s)");

    Console.WriteLine($"    Total resources: {resourceCount:N0}");

    // Roles + Permissions
    await ExecuteNonQueryAsync(@"
        INSERT INTO SqlOSFgaRoles (Id, [Key], Name) VALUES
            ('company_admin', 'CompanyAdmin', 'Company Admin'),
            ('division_manager', 'DivisionManager', 'Division Manager'),
            ('region_manager', 'RegionManager', 'Region Manager'),
            ('store_manager', 'StoreManager', 'Store Manager');
        INSERT INTO SqlOSFgaPermissions (Id, [Key], Name) VALUES
            ('product_view', 'PRODUCT_VIEW', 'View Products'),
            ('product_edit', 'PRODUCT_EDIT', 'Edit Products'),
            ('store_view', 'STORE_VIEW', 'View Stores');
        INSERT INTO SqlOSFgaRolePermissions (RoleId, PermissionId) VALUES
            ('company_admin', 'product_view'), ('company_admin', 'product_edit'), ('company_admin', 'store_view'),
            ('division_manager', 'product_view'), ('division_manager', 'product_edit'), ('division_manager', 'store_view'),
            ('region_manager', 'product_view'), ('region_manager', 'product_edit'), ('region_manager', 'store_view'),
            ('store_manager', 'product_view'), ('store_manager', 'store_view');
    ");

    // Principals
    var d10PSb = new StringBuilder();
    foreach (var u in new[] { "d10_admin", "d10_div_mgr", "d10_reg_mgr", "d10_store_mgr" })
    {
        d10PSb.AppendLine($"INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('{u}', 'user', '{u}');");
        for (int g = 1; g <= 2; g++)
        {
            d10PSb.AppendLine($"INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('{u}_grp{g}', 'group', '{u}_grp{g}');");
            d10PSb.AppendLine($"INSERT INTO SqlOSFgaUserGroups (Id, PrincipalId, Name) VALUES ('{u}_ug{g}', '{u}_grp{g}', '{u} Group {g}');");
            d10PSb.AppendLine($"INSERT INTO SqlOSFgaUserGroupMemberships (PrincipalId, UserGroupId) VALUES ('{u}', '{u}_ug{g}');");
        }
    }
    await ExecuteNonQueryAsync(d10PSb.ToString());

    // Grants
    await ExecuteNonQueryAsync(@"
        INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES
            ('g_d10_admin',  'd10_admin',      'company_admin',    'retail_root'),
            ('g_d10_div',    'd10_div_mgr',    'division_manager', 'div_1'),
            ('g_d10_reg',    'd10_reg_mgr',    'region_manager',   'd10_reg_1_1'),
            ('g_d10_store',  'd10_store_mgr',  'store_manager',    'd10_str_1_1_1_1_1_1');
    ");

    Console.Write("    Updating statistics...");
    await ExecuteNonQueryAsync(@"
        UPDATE STATISTICS SqlOSFgaResources;
        UPDATE STATISTICS SqlOSFgaGrants;
        UPDATE STATISTICS SqlOSFgaRolePermissions;
        UPDATE STATISTICS Products; UPDATE STATISTICS Stores;
        UPDATE STATISTICS Departments; UPDATE STATISTICS Sections;
        UPDATE STATISTICS Divisions; UPDATE STATISTICS Districts;
        UPDATE STATISTICS Areas; UPDATE STATISTICS Zones; UPDATE STATISTICS Regions;
    ");
    Console.WriteLine(" done");

    var d10ResCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM SqlOSFgaResources");
    var d10ProdCountFinal = await ExecuteScalarAsync("SELECT COUNT(*) FROM Products");
    Console.WriteLine($"    Final: {d10ResCount:N0} resources, {d10ProdCountFinal:N0} products (D=10 hierarchy)");

    return new D10Principals(
        CompanyAdmin: "d10_admin,d10_admin_grp1,d10_admin_grp2",
        DivMgr: "d10_div_mgr,d10_div_mgr_grp1,d10_div_mgr_grp2",
        RegMgr: "d10_reg_mgr,d10_reg_mgr_grp1,d10_reg_mgr_grp2",
        StoreMgr: "d10_store_mgr,d10_store_mgr_grp1,d10_store_mgr_grp2"
    );
}

// =============================================================================
// Benchmark 8: List Filtering at 1.2M Resources (D=5)
// =============================================================================

async Task<List<BenchmarkResult>> RunListFilteringD5Async(D5Principals principals)
{
    var results = new List<BenchmarkResult>();

    // 8a. Page size scaling
    Console.WriteLine("  --- 8a: Page Size Scaling (company_admin, σ=1.0, 1.2M resources) ---");
    foreach (var k in new[] { 10, 20, 50, 100 })
    {
        Console.Write($"    k={k}...");
        var query = BuildProductQuery(principals.CompanyAdmin, k);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 1.2M PageSize", $"k={k} (admin σ=1.0)", median, p95, iqr, k, "TVF Cursor"));
    }

    // 8b. Access scope
    Console.WriteLine("  --- 8b: Access Scope at Scale (k=20, 1.2M resources) ---");
    var scopeTests = new (string label, string principalIds, string sigma)[]
    {
        ("company_admin", principals.CompanyAdmin, "σ=1.0"),
        ("chain_mgr",     principals.ChainMgr,     "σ≈0.067"),
        ("region_mgr",    principals.RegionMgr,     "σ≈0.0067"),
        ("store_mgr",     principals.StoreMgr,      "σ≈0.00007"),
    };
    foreach (var (label, principalIds, sigma) in scopeTests)
    {
        Console.Write($"    {label} ({sigma})...");
        var query = BuildProductQuery(principalIds, 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 1.2M Scope", $"{label} ({sigma})", median, p95, iqr, 20, "TVF Cursor"));
    }

    // 8c. Deep cursor pagination
    Console.WriteLine("  --- 8c: Deep Cursor Pagination (chain_mgr, σ≈0.067, k=20) ---");
    var cursorPositions = await FindProductCursorPositionsAsync(principals.ChainMgr, new[] { 0, 1000, 10000 });
    var cursorLabels = new[] { "page 1", "~page 50 (row 1K)", "~page 500 (row 10K)" };
    for (int i = 0; i < cursorPositions.Count; i++)
    {
        var cursorId = cursorPositions[i];
        Console.Write($"    {cursorLabels[i]}...");
        var query = cursorId == 0
            ? BuildProductQuery(principals.ChainMgr, 20)
            : BuildProductQuery(principals.ChainMgr, 20, cursorId: cursorId);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 1.2M DeepCursor", $"chain_mgr {cursorLabels[i]}", median, p95, iqr, 20, "TVF Cursor"));
    }

    return results;
}

async Task<List<int>> FindProductCursorPositionsAsync(string principalIds, int[] offsets, string permission = "product_view")
{
    var positions = new List<int>();
    foreach (var offset in offsets)
    {
        if (offset == 0) { positions.Add(0); continue; }
        var sql = $@"
            SELECT p.Id FROM Products p
            WHERE EXISTS (SELECT 1 FROM dbo.fn_IsResourceAccessible(p.ResourceId, '{principalIds}', '{permission}'))
            ORDER BY p.Id
            OFFSET {offset} ROWS FETCH NEXT 1 ROWS ONLY";
        var id = await ExecuteScalarAsync(sql);
        positions.Add(id is int intId ? intId : 0);
    }
    return positions;
}

// =============================================================================
// Benchmark 9: Point Access Check at 1.2M Resources (D=5)
// =============================================================================

async Task<List<BenchmarkResult>> RunPointAccessCheckD5Async(D5Principals principals)
{
    var results = new List<BenchmarkResult>();

    // 9a. Point check at depths 0-4
    Console.WriteLine("  --- 9a: Point Check vs Hierarchy Depth (D=5) ---");
    var sampleProductRes = (await ExecuteScalarAsync("SELECT TOP 1 ResourceId FROM Products ORDER BY Id"))?.ToString() ?? "prd_1_1_1_1";
    var depthTests = new (string label, string resourceId)[]
    {
        ("root (depth=0)",    "retail_root"),
        ("chain (depth=1)",   "chain_1"),
        ("region (depth=2)",  "reg_1_1"),
        ("store (depth=3)",   "str_1_1_1"),
        ("product (depth=4)", sampleProductRes),
    };
    foreach (var (label, resourceId) in depthTests)
    {
        Console.Write($"    {label}...");
        var query = BuildPointCheckQuery(resourceId, principals.CompanyAdmin, "product_view");
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 Point Depth", $"{label}", median, p95, iqr, 1, "TVF Point"));
    }

    // 9b. Grant set size
    Console.WriteLine("  --- 9b: Point Check vs Grant Set Size (D=5) ---");
    foreach (var grantCount in new[] { 1, 5, 10, 20 })
    {
        await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE Id LIKE 'g_extra_%'");
        if (grantCount > 1)
        {
            var grantSb = new StringBuilder();
            for (int i = 1; i < grantCount; i++)
            {
                var targetRes = i % 3 == 0 ? $"chain_{(i % 15) + 1}" : i % 3 == 1 ? $"reg_{(i % 15) + 1}_{(i % 10) + 1}" : $"str_{(i % 15) + 1}_{(i % 10) + 1}_{(i % 100) + 1}";
                grantSb.AppendLine($"INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES ('g_extra_{i}', 'd5_admin', 'company_admin', '{targetRes}');");
            }
            await ExecuteNonQueryAsync(grantSb.ToString());
        }
        Console.Write($"    grants={grantCount}...");
        var query = BuildPointCheckQuery(sampleProductRes, principals.CompanyAdmin, "product_view");
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 Point Grants", $"grants={grantCount}", median, p95, iqr, 1, "TVF Point"));
    }
    await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE Id LIKE 'g_extra_%'");

    // 9c. Principal set size
    Console.WriteLine("  --- 9c: Point Check vs Principal Set Size (D=5) ---");
    var dummySb = new StringBuilder();
    for (int i = 1; i <= 20; i++)
        dummySb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM SqlOSFgaPrincipals WHERE Id = 'dummy_p_{i}') INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('dummy_p_{i}', 'user', 'Dummy {i}');");
    await ExecuteNonQueryAsync(dummySb.ToString());

    foreach (var m in new[] { 1, 3, 6, 11, 21 })
    {
        var pIds = new StringBuilder("d5_admin");
        if (m >= 3) pIds.Append(",d5_admin_grp1,d5_admin_grp2");
        for (int i = 1; i <= m - 3 && m > 3; i++) pIds.Append($",dummy_p_{i}");
        Console.Write($"    M={m}...");
        var query = BuildPointCheckQuery(sampleProductRes, pIds.ToString(), "product_view");
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 Point Principals", $"M={m}", median, p95, iqr, 1, "TVF Point"));
    }

    return results;
}

// =============================================================================
// Benchmark 10: Dimensional Analysis at 1.2M Resources (D=5)
// =============================================================================

async Task<List<BenchmarkResult>> RunDimensionalAnalysisD5Async(D5Principals principals)
{
    var results = new List<BenchmarkResult>();

    // 10a. Principal set size
    Console.WriteLine("  --- 10a: Principal Set Size at 1.2M (company_admin, k=20) ---");
    var dummySb = new StringBuilder();
    for (int i = 1; i <= 20; i++)
        dummySb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM SqlOSFgaPrincipals WHERE Id = 'dummy_p_{i}') INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('dummy_p_{i}', 'user', 'Dummy {i}');");
    await ExecuteNonQueryAsync(dummySb.ToString());

    foreach (var m in new[] { 1, 3, 6, 11 })
    {
        var pIds = new StringBuilder("d5_admin");
        if (m >= 3) pIds.Append(",d5_admin_grp1,d5_admin_grp2");
        for (int i = 1; i <= m - 3 && m > 3; i++) pIds.Append($",dummy_p_{i}");
        Console.Write($"    M={m}...");
        var query = BuildProductQuery(pIds.ToString(), 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 Dimensional M", $"M={m} (1.2M, k=20)", median, p95, iqr, 20, "TVF Cursor"));
    }

    // 10b. Grant density
    Console.WriteLine("  --- 10b: Grant Density — Multi-Chain Access (k=20) ---");
    await ExecuteNonQueryAsync("IF NOT EXISTS (SELECT 1 FROM SqlOSFgaPrincipals WHERE Id = 'grant_density_user') INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('grant_density_user', 'user', 'Grant Density Test');");

    foreach (var chainCount in new[] { 1, 3, 5, 10 })
    {
        await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE PrincipalId = 'grant_density_user'");
        var grantSb = new StringBuilder();
        for (int c = 1; c <= chainCount; c++)
            grantSb.AppendLine($"INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES ('g_density_{c}', 'grant_density_user', 'chain_manager', 'chain_{c}');");
        await ExecuteNonQueryAsync(grantSb.ToString());
        await ExecuteNonQueryAsync("UPDATE STATISTICS SqlOSFgaGrants;");
        Console.Write($"    chains={chainCount} (~{chainCount * 80_000:N0} accessible)...");
        var query = BuildProductQuery("grant_density_user", 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 Dimensional Grants", $"chains={chainCount}", median, p95, iqr, 20, "TVF Cursor"));
    }

    // 10c. Sparse intra-node access
    Console.WriteLine("  --- 10c: Sparse Intra-Node Access (store-level grants, k=20) ---");
    await ExecuteNonQueryAsync("IF NOT EXISTS (SELECT 1 FROM SqlOSFgaPrincipals WHERE Id = 'sparse_store_user') INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('sparse_store_user', 'user', 'Sparse Store Test');");

    foreach (var (label, storeGrantCount) in new (string, int)[] { ("chain grant (inherit all)", 0), ("100 store grants", 100), ("10 store grants", 10), ("1 store grant", 1) })
    {
        await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE PrincipalId = 'sparse_store_user'");
        if (storeGrantCount == 0)
        {
            await ExecuteNonQueryAsync("INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES ('g_sparse_chain', 'sparse_store_user', 'chain_manager', 'chain_1');");
        }
        else
        {
            var grantSb = new StringBuilder();
            for (int s = 1; s <= storeGrantCount; s++)
                grantSb.AppendLine($"INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES ('g_sparse_{s}', 'sparse_store_user', 'store_manager', 'str_1_1_{s}');");
            await ExecuteNonQueryAsync(grantSb.ToString());
        }
        await ExecuteNonQueryAsync("UPDATE STATISTICS SqlOSFgaGrants;");
        Console.Write($"    {label}...");
        var query = BuildProductQuery("sparse_store_user", 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D5 Dimensional Sparse", $"{label}", median, p95, iqr, 20, "TVF Cursor"));
    }

    await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE PrincipalId IN ('grant_density_user', 'sparse_store_user')");
    return results;
}

// =============================================================================
// Benchmark 11: List Filtering at 1.5M Resources (D=10)
// =============================================================================

async Task<List<BenchmarkResult>> RunListFilteringD10Async(D10Principals principals)
{
    var results = new List<BenchmarkResult>();

    Console.WriteLine("  --- 11a: Page Size Scaling (company_admin, σ=1.0, 1.5M resources, D=10) ---");
    foreach (var k in new[] { 10, 20, 50, 100 })
    {
        Console.Write($"    k={k}...");
        var query = BuildProductQuery(principals.CompanyAdmin, k);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D10 1.5M PageSize", $"k={k} (admin σ=1.0)", median, p95, iqr, k, "TVF Cursor"));
    }

    Console.WriteLine("  --- 11b: Access Scope at Scale (k=20, 1.5M resources, D=10) ---");
    var scopeTests = new (string label, string principalIds, string sigma)[]
    {
        ("company_admin", principals.CompanyAdmin, "σ=1.0"),
        ("div_mgr",       principals.DivMgr,       "σ≈0.20"),
        ("reg_mgr",       principals.RegMgr,        "σ≈0.04"),
        ("store_mgr",     principals.StoreMgr,      "σ≈0.00008"),
    };
    foreach (var (label, principalIds, sigma) in scopeTests)
    {
        Console.Write($"    {label} ({sigma})...");
        var query = BuildProductQuery(principalIds, 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D10 1.5M Scope", $"{label} ({sigma})", median, p95, iqr, 20, "TVF Cursor"));
    }

    Console.WriteLine("  --- 11c: Deep Cursor Pagination (div_mgr, σ≈0.20, k=20) ---");
    var cursorPositions = await FindProductCursorPositionsAsync(principals.DivMgr, new[] { 0, 1000, 10000 });
    var cursorLabels = new[] { "page 1", "~page 50 (row 1K)", "~page 500 (row 10K)" };
    for (int i = 0; i < cursorPositions.Count; i++)
    {
        var cursorId = cursorPositions[i];
        Console.Write($"    {cursorLabels[i]}...");
        var query = cursorId == 0
            ? BuildProductQuery(principals.DivMgr, 20)
            : BuildProductQuery(principals.DivMgr, 20, cursorId: cursorId);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D10 1.5M DeepCursor", $"div_mgr {cursorLabels[i]}", median, p95, iqr, 20, "TVF Cursor"));
    }

    return results;
}

// =============================================================================
// Benchmark 12: Point Access Check at D=10
// =============================================================================

async Task<List<BenchmarkResult>> RunPointAccessCheckD10Async(D10Principals principals)
{
    var results = new List<BenchmarkResult>();

    Console.WriteLine("  --- 12a: Point Check vs Hierarchy Depth (D=10) ---");
    var sampleProduct = (await ExecuteScalarAsync("SELECT TOP 1 ResourceId FROM Products ORDER BY Id"))?.ToString() ?? "d10prd_1_1_1_1_1_1_1_1_1";
    var sampleSection = (await ExecuteScalarAsync("SELECT TOP 1 ResourceId FROM Sections ORDER BY Id"))?.ToString() ?? "sect_1_1_1_1_1_1_1_1";
    var sampleDept = (await ExecuteScalarAsync("SELECT TOP 1 ResourceId FROM Departments ORDER BY Id"))?.ToString() ?? "dept_1_1_1_1_1_1_1";
    var sampleStore = (await ExecuteScalarAsync("SELECT TOP 1 ResourceId FROM Stores WHERE ResourceId LIKE 'd10_str_%' ORDER BY Id"))?.ToString() ?? "d10_str_1_1_1_1_1_1";

    var depthTests = new (string label, string resourceId)[]
    {
        ("root (depth=0)",       "retail_root"),
        ("division (depth=1)",   "div_1"),
        ("region (depth=2)",     "d10_reg_1_1"),
        ("district (depth=3)",   "dist_1_1_1"),
        ("area (depth=4)",       "area_1_1_1_1"),
        ("zone (depth=5)",       "zone_1_1_1_1_1"),
        ("store (depth=6)",      sampleStore),
        ("department (depth=7)", sampleDept),
        ("section (depth=8)",    sampleSection),
        ("product (depth=9)",    sampleProduct),
    };
    foreach (var (label, resourceId) in depthTests)
    {
        Console.Write($"    {label}...");
        var query = BuildPointCheckQuery(resourceId, principals.CompanyAdmin, "product_view");
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D10 Point Depth", $"{label}", median, p95, iqr, 1, "TVF Point"));
    }

    return results;
}

// =============================================================================
// Benchmark 13: Dimensional Analysis at D=10
// =============================================================================

async Task<List<BenchmarkResult>> RunDimensionalAnalysisD10Async(D10Principals principals)
{
    var results = new List<BenchmarkResult>();

    // 13a. Principal set size
    Console.WriteLine("  --- 13a: Principal Set Size at D=10 (company_admin, k=20) ---");
    var dummySb = new StringBuilder();
    for (int i = 1; i <= 20; i++)
        dummySb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM SqlOSFgaPrincipals WHERE Id = 'dummy_p_{i}') INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('dummy_p_{i}', 'user', 'Dummy {i}');");
    await ExecuteNonQueryAsync(dummySb.ToString());

    foreach (var m in new[] { 1, 3, 6, 11 })
    {
        var pIds = new StringBuilder("d10_admin");
        if (m >= 3) pIds.Append(",d10_admin_grp1,d10_admin_grp2");
        for (int i = 1; i <= m - 3 && m > 3; i++) pIds.Append($",dummy_p_{i}");
        Console.Write($"    M={m}...");
        var query = BuildProductQuery(pIds.ToString(), 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D10 Dimensional M", $"M={m} (1.5M, k=20)", median, p95, iqr, 20, "TVF Cursor"));
    }

    // 13b. Grant density (multi-division)
    Console.WriteLine("  --- 13b: Grant Density — Multi-Division Access (k=20, D=10) ---");
    await ExecuteNonQueryAsync("IF NOT EXISTS (SELECT 1 FROM SqlOSFgaPrincipals WHERE Id = 'd10_density_user') INSERT INTO SqlOSFgaPrincipals (Id, PrincipalTypeId, DisplayName) VALUES ('d10_density_user', 'user', 'D10 Grant Density Test');");

    foreach (var divisionCount in new[] { 1, 2, 3, 5 })
    {
        await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE PrincipalId = 'd10_density_user'");
        var grantSb = new StringBuilder();
        for (int d = 1; d <= divisionCount; d++)
            grantSb.AppendLine($"INSERT INTO SqlOSFgaGrants (Id, PrincipalId, RoleId, ResourceId) VALUES ('g_d10_density_{d}', 'd10_density_user', 'division_manager', 'div_{d}');");
        await ExecuteNonQueryAsync(grantSb.ToString());
        await ExecuteNonQueryAsync("UPDATE STATISTICS SqlOSFgaGrants;");
        Console.Write($"    divisions={divisionCount} (~{divisionCount * 240_000:N0} accessible)...");
        var query = BuildProductQuery("d10_density_user", 20);
        var (median, p95, iqr) = await MeasureQueryAsync(query);
        Console.WriteLine($" median={median:F2}ms, p95={p95:F2}ms, iqr={iqr:F2}ms");
        results.Add(new("D10 Dimensional Grants", $"divisions={divisionCount}", median, p95, iqr, 20, "TVF Cursor"));
    }

    await ExecuteNonQueryAsync("DELETE FROM SqlOSFgaGrants WHERE PrincipalId = 'd10_density_user'");
    return results;
}

// =============================================================================
// Output
// =============================================================================

void PrintResults(List<BenchmarkResult> results)
{
    Console.WriteLine($"{"Suite",-30} {"Config",-35} {"Median (ms)",12} {"P95 (ms)",12} {"IQR (ms)",10} {"Method",-20}");
    Console.WriteLine(new string('-', 122));
    foreach (var r in results)
        Console.WriteLine($"{r.Suite,-30} {r.Config,-35} {r.MedianMs,12:F2} {r.P95Ms,12:F2} {r.IqrMs,10:F2} {r.Method,-20}");
}

void PrintMarkdownTable(List<BenchmarkResult> results)
{
    Console.WriteLine("| Suite | Config | Median (ms) | P95 (ms) | IQR (ms) | Method |");
    Console.WriteLine("|-------|--------|-------------|----------|----------|--------|");
    foreach (var r in results)
        Console.WriteLine($"| {r.Suite} | {r.Config} | {r.MedianMs:F2} | {r.P95Ms:F2} | {r.IqrMs:F2} | {r.Method} |");
}

async Task CleanupAsync()
{
    try
    {
        var masterConn = connectionString.Replace("Database=SqlOSFga_Benchmark", "Database=master");
        await using var conn = new SqlConnection(masterConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            IF EXISTS (SELECT 1 FROM sys.databases WHERE name = 'SqlOSFga_Benchmark')
            BEGIN
                ALTER DATABASE SqlOSFga_Benchmark SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE SqlOSFga_Benchmark;
            END";
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine("Benchmark database cleaned up.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Cleanup warning: {ex.Message}");
    }
}

record BenchmarkResult(string Suite, string Config, double MedianMs, double P95Ms, double IqrMs, int PageSize, string Method);
record D5Principals(string CompanyAdmin, string ChainMgr, string RegionMgr, string StoreMgr);
record D10Principals(string CompanyAdmin, string DivMgr, string RegMgr, string StoreMgr);
