using SqlOS.Fga.Configuration;

namespace SqlOS.Fga;

/// <summary>
/// Names of the resource lineage: the columns on the resources table that hold each resource's ancestor at
/// every level, its depth, and its reach; the routines and triggers that keep them exact; and the scope
/// column that carries them onto every application table whose entity has a resource id.
/// One definition, used by the model configuration (so EF Core knows the columns and triggers exist), by
/// the filter builder, and by both database providers (which create them).
/// </summary>
internal static class SqlOSFgaLineage
{
    /// <summary>The compact key of a resource, held by the ancestor columns.</summary>
    public const string SeqColumn = "Seq";

    /// <summary>The resource's level in the tree: 0 for a resource without a parent. NULL when malformed.</summary>
    public const string DepthColumn = "Depth";

    /// <summary>
    /// The highest level from which access flows down to the resource: every resource on its path from that
    /// level down, itself included, is active. NULL when the resource is inactive or malformed, which denies it.
    /// </summary>
    public const string ReachColumn = "Reach";

    /// <summary>A caller holding more roots than this is checked row by row by <c>fn_IsResourceAccessible</c> instead of by a listed predicate.</summary>
    public const int MaxListedRoots = 1_000;

    /// <summary>
    /// Rows per transaction when the lineage is rebuilt over the whole table: every write of the rebuild is
    /// a range of the resources table in key order, committed on its own, so the transaction log stays
    /// small however large the table is, and a failed rebuild is resumed by the next startup.
    /// </summary>
    public const int RebuildRangeRows = 500_000;

    /// <summary>The ancestor column for a level: <c>Ancestor0</c> is the top of the resource's tree.</summary>
    public static string AncestorColumn(int level) => "Ancestor" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The number of levels, and so of ancestor columns: the configured depth plus the root level.</summary>
    public static int Levels(SqlOSFgaOptions options) => Levels(options.MaxResourceHierarchyDepth);

    public static int Levels(int maxDepth) => Math.Max(1, maxDepth) + 1;

    /// <summary>The deepest level a resource may sit at.</summary>
    public static int MaxLevel(SqlOSFgaOptions options) => Levels(options) - 1;

    public static string AncestorIndexName(string resourcesTable, int level) => $"IX_{resourcesTable}_{AncestorColumn(level)}";

    /// <summary>The statement triggers on the resources table that keep the lineage exact.</summary>
    public static IReadOnlyList<string> TriggerNames(string resourcesTable)
        => [$"TR_{resourcesTable}_Lineage_Insert", $"TR_{resourcesTable}_Lineage_Update", $"TR_{resourcesTable}_Lineage_Delete"];

    public static string RefreshRoutineName(string resourcesTable) => $"{resourcesTable}_LineageRefresh";

    public static string RebuildRoutineName(string resourcesTable) => $"{resourcesTable}_LineageRebuild";

    // The scope column: the lineage of a row's resource, held on the application table itself. One column,
    // SqlOSFgaScope, on every table whose entity carries a ResourceId. On PostgreSQL it is a bigint array:
    // element 1 the resource type's compact key, element l + 2 the ancestor at level l (NULL where access does
    // not flow from that level). On SQL Server it is a varbinary: byte 1 the depth, bytes 2-5 the type, then
    // eight bytes per level (zero where access does not flow). SqlOS reads each piece through an expression
    // the database can index: an element of the array, or a SUBSTRING of the bytes (a computed column on SQL
    // Server, which the optimizer matches by expression).

    public const string ScopePrefix = "SqlOSFga";

    public const string ScopeColumn = ScopePrefix + "Scope";

    /// <summary>The marker annotation on an entity type whose table carries the scope column.</summary>
    public const string ScopeAnnotation = "SqlOS:Fga:Scope";

    /// <summary>The SQL Server column type: room for 63 levels, so a changed depth never changes the column.</summary>
    public const string ScopeBinaryType = "varbinary(512)";

    public const int ScopeMaxLevels = 63;

    /// <summary>PostgreSQL: the (1-based) element of the array holding the resource type's compact key.</summary>
    public const int ScopeTypeElement = 1;

    /// <summary>PostgreSQL: the (1-based) element holding the ancestor at a level.</summary>
    public static int ScopeAncestorElement(int level) => level + 2;

    /// <summary>SQL Server: the (1-based) offset of the depth byte.</summary>
    public const int ScopeDepthOffset = 1;

    /// <summary>SQL Server: the (1-based) offset of the four type bytes.</summary>
    public const int ScopeTypeOffset = 2;

    /// <summary>SQL Server: the (1-based) offset of the eight ancestor bytes of a level.</summary>
    public static int ScopeAncestorOffset(int level) => 6 + 8 * level;

    /// <summary>SQL Server: the bytes a scope value of the given number of levels takes.</summary>
    public static int ScopeBinaryLength(int levels) => 5 + 8 * levels;

    /// <summary>SQL Server: the computed column reading a level's ancestor out of the scope column, which the level's indexes are built on.</summary>
    public static string ScopeLevelColumn(int level) => ScopeColumn + level.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// SQL Server: the computed column reading the resource type out of the scope column. Never indexed; it
    /// exists so the optimizer has statistics on the type test (which the query spells out as the same
    /// expression) and knows it keeps nearly every row, instead of guessing it is selective and scanning the
    /// table in place of walking a level's index.
    /// </summary>
    public const string ScopeTypeColumn = ScopeColumn + "Type";

    /// <summary>The statistics on the type expression (both engines), for the same reason.</summary>
    public static string ScopeTypeStatisticsName(string table) => $"ST_{table}_{ScopeTypeColumn}";

    public static string ScopeIndexName(string table, int level, string? mirrored)
        => mirrored is null
            ? $"IX_{table}_{ScopeLevelColumn(level)}"
            : $"IX_{table}_{ScopeLevelColumn(level)}_{mirrored}";

    /// <summary>The prefix every per-level index on a table shares, by which stale ones are found.</summary>
    public static string ScopeIndexPrefix(string table) => $"IX_{table}_{ScopeColumn}";

    public static string ScopeResourceIdIndexName(string table) => $"IX_{table}_{ScopePrefix}ResourceId";

    /// <summary>The statement triggers on an application table that copy the lineage onto its rows.</summary>
    public static IReadOnlyList<string> ScopeTriggerNames(string table)
        => [$"TR_{table}_{ScopePrefix}Scope_Insert", $"TR_{table}_{ScopePrefix}Scope_Update"];
}

/// <summary>An application table that carries the scope column, as the database routines need to address it.</summary>
/// <param name="Schema">The table's schema, or null for the connection's default.</param>
/// <param name="Table">The table name.</param>
/// <param name="ResourceIdColumn">The column holding the resource id.</param>
/// <param name="KeyColumns">The primary key columns, used to join a statement's rows back to the table.</param>
/// <param name="Orders">The orders the application declared indexes for, each mirrored per level.</param>
internal sealed record SqlOSFgaScopeTable(string? Schema, string Table, string ResourceIdColumn, IReadOnlyList<string> KeyColumns, IReadOnlyList<SqlOSFgaScopeOrder> Orders)
{
    public SqlOSFgaScopeTable(string? schema, string table, string resourceIdColumn, IReadOnlyList<string> keyColumns)
        : this(schema, table, resourceIdColumn, keyColumns, [])
    {
    }
}

/// <summary>An index the application declared on a table: the suffix that names its mirrors, and its columns followed by the key.</summary>
internal sealed record SqlOSFgaScopeOrder(string Suffix, IReadOnlyList<string> Columns);
