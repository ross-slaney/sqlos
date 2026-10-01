using SqlOS.Fga.Configuration;

namespace SqlOS.Fga;

/// <summary>
/// Names of the ancestor closure's objects, derived from the resources table name. One definition, used by
/// the model configuration (so EF Core knows the triggers exist) and by both database providers (which
/// create them).
/// </summary>
internal static class SqlOSFgaResourceClosure
{
    /// <summary>The closure table: one row per (proper ancestor, descendant) pair on a fully active path.</summary>
    public static string TableName(SqlOSFgaOptions options) => TableName(options.TableNames.Resources);

    public static string TableName(string resourcesTable) => resourcesTable + "Closure";

    /// <summary>The statement triggers on the resources table that keep the closure exact.</summary>
    public static IReadOnlyList<string> TriggerNames(string resourcesTable)
    {
        var closure = TableName(resourcesTable);
        return [$"TR_{closure}_Insert", $"TR_{closure}_Update", $"TR_{closure}_Delete"];
    }
}
