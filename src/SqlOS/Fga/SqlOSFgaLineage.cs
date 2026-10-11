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

    /// <summary>
    /// The grants' copy of their resource's <see cref="SeqColumn"/> (schema v16). The point check seeks a
    /// subject's grants on an ancestor by (ancestor's Seq, subject id), so it reads only the caller's grants
    /// there, however many other people hold grants on the same resource. A resource's Seq never changes.
    /// </summary>
    public const string GrantResourceSeqColumn = "ResourceSeq";

    /// <summary>The index the point check seeks: (<see cref="GrantResourceSeqColumn"/>, SubjectId).</summary>
    public static string GrantLookupIndexName(string grantsTable) => $"IX_{grantsTable}_ResourceSeq_SubjectId";

    /// <summary>The trigger that fills <see cref="GrantResourceSeqColumn"/> when a grant is written.</summary>
    public static string GrantTriggerName(string grantsTable) => $"TR_{grantsTable}_ResourceSeq";

    public static string RefreshRoutineName(string resourcesTable) => $"{resourcesTable}_LineageRefresh";

    public static string RebuildRoutineName(string resourcesTable) => $"{resourcesTable}_LineageRebuild";

    /// <summary>
    /// The index on an application table's resource id, which a filtered query joins the visible resources
    /// to. Part of the application's EF model (so of its migrations), added when the application declared
    /// none.
    /// </summary>
    public static string ResourceIdIndexName(string table) => $"IX_{table}_SqlOSFgaResourceId";

    // The crossover between the two filters (fn_ListFirst). A page of k rows costs, with the caller's rows
    // listed first, about one index read per row the caller sees (V) and a sort; with the table read in its
    // order and each row checked, about k·N/V checks, N the table's rows, each check a few index reads. The
    // first grows with V, the second shrinks with it, and they are equal at V = √(c·k·N), where c is the cost of
    // a check over the cost of a listed row. For pages of 20 to 25 rows, c measures about 3 on both engines, so
    // the crossover is near 8·√N. Below it a page costs less listed first; at or above it, checked. Either way
    // a page costs at most about the crossover's worth of index reads, and the count that finds the side costs
    // at most the crossover too. Nothing in it is tuned: change k or c and the formula moves with them.

    /// <summary>The crossover's factor, √(c·k) for k = 20 and c = 3: a caller who sees fewer than <c>8·√N</c> rows of an N-row table is listed first.</summary>
    public const int CrossoverFactor = 8;

    /// <summary>The crossover's floor: below a thousand rows either filter costs milliseconds, so small or never-measured tables list first.</summary>
    public const int CrossoverMinimum = 1_000;

    /// <summary>The crossover for a table of <paramref name="rows"/> rows (an expression), in SQL both engines read the same way.</summary>
    public static string CrossoverSql(string rows, string sqrt)
    {
        var factor = CrossoverFactor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var minimum = CrossoverMinimum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"CASE WHEN {factor} * {sqrt}({rows}) > {minimum} THEN {factor} * {sqrt}({rows}) ELSE {minimum} END";
    }
}
