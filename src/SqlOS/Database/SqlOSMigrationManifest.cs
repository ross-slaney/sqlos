using System.Reflection;
using System.Text.RegularExpressions;

namespace SqlOS.Database;

internal static class SqlOSMigrationManifest
{
    public sealed record Script(int Version, string Name, string ResourceName);

    public static List<Script> Discover(Assembly assembly, string resourcePrefix)
    {
        var pattern = new Regex(
            "^" + Regex.Escape(resourcePrefix) + @"(\d+)_(.+)\.sql$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var scripts = new List<Script>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var match = pattern.Match(name);
            if (!match.Success)
            {
                continue;
            }

            scripts.Add(new Script(int.Parse(match.Groups[1].Value), match.Groups[2].Value, name));
        }

        return scripts;
    }

    public static void EnsureProviderComplete(Assembly assembly)
    {
        EnsureMatching(
            "AuthServer",
            Discover(assembly, SqlServerDatabaseProvider.Instance.AuthMigrationResourcePrefix),
            Discover(assembly, PostgreSqlDatabaseProvider.Instance.AuthMigrationResourcePrefix));
        EnsureMatching(
            "FGA",
            Discover(assembly, SqlServerDatabaseProvider.Instance.FgaMigrationResourcePrefix),
            Discover(assembly, PostgreSqlDatabaseProvider.Instance.FgaMigrationResourcePrefix));
    }

    private static void EnsureMatching(string area, IReadOnlyCollection<Script> sqlServer, IReadOnlyCollection<Script> postgreSql)
    {
        var sqlServerKeys = sqlServer.Select(Key).ToHashSet(StringComparer.Ordinal);
        var postgreSqlKeys = postgreSql.Select(Key).ToHashSet(StringComparer.Ordinal);
        if (sqlServerKeys.SetEquals(postgreSqlKeys))
        {
            return;
        }

        var missingPostgres = sqlServerKeys.Except(postgreSqlKeys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var missingSqlServer = postgreSqlKeys.Except(sqlServerKeys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        throw new InvalidOperationException(
            $"SqlOS {area} migration manifests are not provider-complete. " +
            $"Missing PostgreSQL scripts: [{string.Join(", ", missingPostgres)}]. " +
            $"Missing SQL Server scripts: [{string.Join(", ", missingSqlServer)}].");
    }

    private static string Key(Script script) => $"{script.Version:000}_{script.Name}";
}
