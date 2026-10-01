using SqlOS.Fga.Configuration;

namespace SqlOS.Fga;

/// <summary>
/// Names of the resource lineage: the columns on the resources table that hold each resource's ancestor at
/// every level, its depth, and its reach; the routines and triggers that keep them exact; and the scope
/// columns that mirror them onto application tables when <see cref="SqlOSFgaOptions.ScopeColumns"/> is on.
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

    // Scope columns: the lineage copied onto an application table whose entity carries a ResourceId.

    public const string ScopePrefix = "SqlOSFga";

    public const string ScopeReachColumn = ScopePrefix + "Reach";

    public const string ScopeTypeSeqColumn = ScopePrefix + "TypeSeq";

    /// <summary>The marker annotation on an entity type whose table carries the scope columns.</summary>
    public const string ScopeAnnotation = "SqlOS:Fga:ScopeColumns";

    public static string ScopeAncestorColumn(int level) => ScopePrefix + AncestorColumn(level);

    public static string ScopeIndexName(string table, int level, string? mirrored)
        => mirrored is null
            ? $"IX_{table}_{ScopeAncestorColumn(level)}"
            : $"IX_{table}_{ScopeAncestorColumn(level)}_{mirrored}";

    public static string ScopeResourceIdIndexName(string table) => $"IX_{table}_{ScopePrefix}ResourceId";

    /// <summary>The statement triggers on an application table that copy the lineage onto its rows.</summary>
    public static IReadOnlyList<string> ScopeTriggerNames(string table)
        => [$"TR_{table}_{ScopePrefix}Scope_Insert", $"TR_{table}_{ScopePrefix}Scope_Update"];
}

/// <summary>An application table that carries the scope columns, as the database routines need to address it.</summary>
/// <param name="Schema">The table's schema, or null for the connection's default.</param>
/// <param name="Table">The table name.</param>
/// <param name="ResourceIdColumn">The column holding the resource id.</param>
/// <param name="KeyColumns">The primary key columns, used to join a statement's rows back to the table.</param>
internal sealed record SqlOSFgaScopeTable(string? Schema, string Table, string ResourceIdColumn, IReadOnlyList<string> KeyColumns);
