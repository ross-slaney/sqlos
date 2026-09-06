namespace SqlOS.Database;

internal static class SqlOSModelSql
{
    public static bool IsPostgreSql(string? providerName)
        => SqlOSDatabase.IsPostgreSql(providerName);

    public static string IsNotNull(string? providerName, string column)
        => IsPostgreSql(providerName)
            ? $"\"{column}\" IS NOT NULL"
            : $"[{column}] IS NOT NULL";

    public static string IsNull(string? providerName, string column)
        => IsPostgreSql(providerName)
            ? $"\"{column}\" IS NULL"
            : $"[{column}] IS NULL";

    public static string EqualsTrue(string? providerName, string column)
        => IsPostgreSql(providerName)
            ? $"\"{column}\" = TRUE"
            : $"[{column}] = 1";
}
