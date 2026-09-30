using SqlOS.BehaviorLock.Host;

namespace SqlOS.BehaviorLock.UpgradeSeed;

/// <summary>Command-line arguments of the upgrade seed.</summary>
internal sealed record SeedArguments(
    string ConnectionString,
    DatabaseProvider Provider,
    string DataProtectionKeysDirectory,
    string ManifestPath)
{
    /// <summary>
    /// Carries the connection string when <c>--connection-string</c> is not given, so the gate
    /// can keep database credentials out of the process list.
    /// </summary>
    public const string ConnectionStringVariable = "SQLOS_UPGRADE_SEED_CONNECTION_STRING";

    public const string Usage = """
        Usage: SqlOS.BehaviorLock.UpgradeSeed --provider <SqlServer|PostgreSql> --data-protection-keys <directory> --manifest <file> [--connection-string <value>]

        Seeds an empty database with the upgrade gate's dataset using the SqlOS package this
        program was built against, then writes the manifest the gate reads. The connection string
        may instead come from the SQLOS_UPGRADE_SEED_CONNECTION_STRING environment variable.
        """;

    public static SeedArguments Parse(IReadOnlyList<string> args, string? connectionStringFromEnvironment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            if (name is not ("--connection-string" or "--provider" or "--data-protection-keys" or "--manifest"))
            {
                throw new ArgumentException($"Unknown argument '{name}'.");
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"{name} needs a value.");
            }

            values[name] = args[++index];
        }

        var connectionString = values.GetValueOrDefault("--connection-string") ?? connectionStringFromEnvironment;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException($"Pass --connection-string or set {ConnectionStringVariable}.");
        }

        var provider = values.GetValueOrDefault("--provider") switch
        {
            "SqlServer" => DatabaseProvider.SqlServer,
            "PostgreSql" => DatabaseProvider.PostgreSql,
            var other => throw new ArgumentException($"--provider must be SqlServer or PostgreSql, not '{other}'.")
        };

        return new SeedArguments(
            connectionString,
            provider,
            Path.GetFullPath(values.GetValueOrDefault("--data-protection-keys") ?? throw new ArgumentException("--data-protection-keys is required.")),
            Path.GetFullPath(values.GetValueOrDefault("--manifest") ?? throw new ArgumentException("--manifest is required.")));
    }
}
