using SqlOS.Fga.Configuration;

namespace SqlOS.Fga;

/// <summary>
/// Names of the resource lineage: the columns on the resources table that hold each resource's ancestor at
/// every level, its depth, and its reach (the tree's closure, one row per resource); the index per level;
/// and the routines and triggers that keep them exact. One definition, used by the model configuration (so
/// EF Core knows the triggers exist) and by both database providers (which create them).
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

    /// <summary>
    /// Rows per transaction when the lineage is rebuilt over the whole table: every write of the rebuild is
    /// a range of the resources table in key order, committed on its own, so the transaction log stays
    /// small however large the table is, and a failed rebuild is resumed by the next startup.
    /// </summary>
    public const int RebuildRangeRows = 500_000;

    /// <summary>
    /// The lock that serializes changes to the resource tree. Every trigger that maintains the lineage takes it
    /// before reading anything: inserts take it shared, so they run side by side; moves, activity changes, and
    /// the rebuild take it exclusively. A change therefore never computes from another transaction's
    /// uncommitted state: it waits for that transaction to commit, then reads what was committed. Without it,
    /// two moves could together commit a cycle.
    /// </summary>
    public static string LineageLockName(SqlOSFgaOptions options) => $"SqlOS:FgaLineage:{options.Schema}.{options.TableNames.Resources}";

    /// <summary>The same lock as a PostgreSQL advisory lock key: the first eight bytes of the name's SHA-256.</summary>
    public static long LineageLockKey(SqlOSFgaOptions options)
        => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LineageLockName(options))));

    /// <summary>The ancestor column for a level: <c>Ancestor0</c> is the top of the resource's tree.</summary>
    public static string AncestorColumn(int level) => "Ancestor" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The index on a level's ancestor column: the rows beneath a root at that level, in one seek.</summary>
    public static string AncestorIndexName(string resourcesTable, int level) => $"IX_{resourcesTable}_{AncestorColumn(level)}";

    /// <summary>The index of every level the configured depth calls for.</summary>
    public static IReadOnlyList<string> AncestorIndexNames(SqlOSFgaOptions options)
        => Enumerable.Range(0, Levels(options)).Select(level => AncestorIndexName(options.TableNames.Resources, level)).ToList();

    /// <summary>The number of levels, and so of ancestor columns: the configured depth plus the root level.</summary>
    public static int Levels(SqlOSFgaOptions options) => Levels(options.MaxResourceHierarchyDepth);

    public static int Levels(int maxDepth) => Math.Max(1, maxDepth) + 1;

    /// <summary>The deepest level a resource may sit at.</summary>
    public static int MaxLevel(SqlOSFgaOptions options) => Levels(options) - 1;

    /// <summary>The statement triggers on the resources table that keep the lineage exact: after insert, after update.</summary>
    public static IReadOnlyList<string> TriggerNames(string resourcesTable)
        => [$"TR_{resourcesTable}_Lineage_Insert", $"TR_{resourcesTable}_Lineage_Update"];

    public static string RefreshRoutineName(string resourcesTable) => $"{resourcesTable}_LineageRefresh";

    public static string RebuildRoutineName(string resourcesTable) => $"{resourcesTable}_LineageRebuild";

    /// <summary>
    /// The index on an application table's resource id, which a filtered query joins the visible resources
    /// to. Part of the application's EF model (so of its migrations), added when the application declared
    /// none.
    /// </summary>
    public static string ResourceIdIndexName(string table) => $"IX_{table}_SqlOSFgaResourceId";

    // How a list filter is chosen (fn_ListFirst). A page of k rows costs, listed first, about one index read
    // per row the caller sees (V), then a sort; read in the table's order and checked row by row, about k·N/V
    // row checks, N the table's rows, each check a few index reads. The two meet near V = √(c·k·N), c the
    // ratio of their unit costs: measured at about 3 on both engines for pages of 20 to 25 rows, which puts
    // the meeting point near 8·√N. Below it the caller's rows are listed first; at or above it they are
    // checked in order. Either way a page costs at most about the cap's worth of index reads, plus the count.

    /// <summary>The cap's factor: a caller who sees fewer than <c>8·√N</c> rows of an N-row table has them listed first.</summary>
    public const int ListFirstFactor = 8;

    /// <summary>The smallest cap, for small or never-measured tables: listing 1,000 rows first costs milliseconds.</summary>
    public const int ListFirstMinimum = 1_000;

    /// <summary>The cap for a table of <paramref name="rows"/> rows (an expression), in SQL both engines read the same way.</summary>
    public static string ListFirstCapSql(string rows, string sqrt)
    {
        var factor = ListFirstFactor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var minimum = ListFirstMinimum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"CASE WHEN {factor} * {sqrt}({rows}) > {minimum} THEN {factor} * {sqrt}({rows}) ELSE {minimum} END";
    }
}
