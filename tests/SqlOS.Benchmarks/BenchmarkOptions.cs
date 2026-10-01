using System.Globalization;

namespace SqlOS.Benchmarks;

internal enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
}

/// <summary>Command-line options. Run with <c>--help</c> for usage.</summary>
internal sealed record BenchmarkOptions
{
    public required DatabaseProvider Provider { get; init; }

    /// <summary>Product counts to measure at, ascending. The dataset grows in place from one to the next.</summary>
    public required IReadOnlyList<long> Scales { get; init; }

    /// <summary>An existing server to use instead of starting a container. The benchmark database on it is dropped and recreated.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Host directory for the database files (CI points this at the runner's large <c>/mnt</c> disk).</summary>
    public string? DataDirectory { get; init; }

    public string? Image { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine("artifacts", "benchmarks");
    public string? SummaryPath { get; init; }
    public string GatesPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "gates.json");
    public bool EnforceGates { get; init; } = true;
    public bool KeepContainer { get; init; }
    public int Seed { get; init; } = 20260930;

    /// <summary>Scenario ids to skip (pull requests skip the 30-second sparse scan).</summary>
    public IReadOnlySet<string> Exclude { get; init; } = new HashSet<string>();

    /// <summary>Memory given to the database engine. The CI runner has 16 GB; the harness itself needs little.</summary>
    public int DatabaseMemoryMegabytes { get; init; } = 8192;

    /// <summary>
    /// The longest a single query may run. A scenario whose first execution exceeds it is reported as
    /// "did not finish" and counted at the budget, so the previous function's sparse scans (hours at 50M)
    /// bound the run instead of ending it.
    /// </summary>
    public int ScenarioBudgetSeconds { get; init; } = 600;

    public const string Usage = """
        SqlOS SHRBAC benchmarks: the shipped FGA schema and fn_IsResourceAccessible on a realistic
        retail hierarchy, measured as the product count grows.

        Usage:
          dotnet run --project tests/SqlOS.Benchmarks -c Release -- --provider <postgresql|sqlserver> [options]

        Options:
          --provider <name>        postgresql (default) or sqlserver
          --scales <list>          product counts, e.g. 100k,1m (default) or 1m,10m,100m (CI)
          --connection <string>    use this server instead of starting a container
                                   (drops and recreates the SqlOSBenchmarks database on it)
          --data-dir <path>        host directory for database files (container mode)
          --image <image>          container image override
          --db-memory-mb <n>       memory for the database engine (default 8192)
          --out <dir>              results directory (default artifacts/benchmarks)
          --summary <file>         append the Markdown summary to this file (CI: $GITHUB_STEP_SUMMARY)
          --gates <file>           gate definitions (default: gates.json next to the binary)
          --no-gates               report gate results without failing the run
          --keep                   leave the container running afterwards
          --seed <n>               dataset seed (default 20260930)
          --exclude <ids>          scenario ids to skip, comma-separated (e.g. list.store.first-page)
          --scenario-budget <s>    the longest a single query may run, in seconds (default 600); a
                                   scenario that exceeds it is reported as not finished, at the budget
        """;

    public static BenchmarkOptions? Parse(string[] args)
    {
        var provider = DatabaseProvider.PostgreSql;
        IReadOnlyList<long> scales = [100_000, 1_000_000];
        string? connection = null, dataDir = null, image = null, summary = null;
        var output = Path.Combine("artifacts", "benchmarks");
        var gates = Path.Combine(AppContext.BaseDirectory, "gates.json");
        var enforce = true;
        var keep = false;
        var seed = 20260930;
        var memory = 8192;
        var exclude = new HashSet<string>(StringComparer.Ordinal);
        var budget = 600;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value.");

            switch (args[i])
            {
                case "--help" or "-h":
                    return null;
                case "--provider":
                    provider = ParseProvider(Next());
                    break;
                case "--scales":
                    scales = Next()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ParseCount)
                        .Distinct()
                        .Order()
                        .ToArray();
                    break;
                case "--connection":
                    connection = Next();
                    break;
                case "--data-dir":
                    dataDir = Next();
                    break;
                case "--image":
                    image = Next();
                    break;
                case "--db-memory-mb":
                    memory = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--out":
                    output = Next();
                    break;
                case "--summary":
                    summary = Next();
                    break;
                case "--gates":
                    gates = Next();
                    break;
                case "--no-gates":
                    enforce = false;
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "--seed":
                    seed = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--exclude":
                    exclude.UnionWith(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--scenario-budget":
                    budget = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        if (scales.Count == 0 || scales[0] < 1_000)
        {
            throw new ArgumentException("--scales needs at least one product count of 1k or more.");
        }

        if (scales[^1] > 999_999_999)
        {
            throw new ArgumentException("Product ids are nine digits; the largest supported scale is 999,999,999.");
        }

        return new BenchmarkOptions
        {
            Provider = provider,
            Scales = scales,
            ConnectionString = connection,
            DataDirectory = dataDir,
            Image = image,
            OutputDirectory = output,
            SummaryPath = summary,
            GatesPath = gates,
            EnforceGates = enforce,
            KeepContainer = keep,
            Seed = seed,
            DatabaseMemoryMegabytes = memory,
            Exclude = exclude,
            ScenarioBudgetSeconds = budget,
        };
    }

    private static DatabaseProvider ParseProvider(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "postgresql" or "postgres" or "pg" or "npgsql" => DatabaseProvider.PostgreSql,
            "sqlserver" or "mssql" or "sql-server" => DatabaseProvider.SqlServer,
            _ => throw new ArgumentException($"Unknown provider '{value}'. Use postgresql or sqlserver."),
        };

    /// <summary>Parses <c>250k</c>, <c>10m</c>, or a plain number (underscores allowed).</summary>
    internal static long ParseCount(string text)
    {
        var value = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var multiplier = 1L;
        if (value.EndsWith('k'))
        {
            multiplier = 1_000;
            value = value[..^1];
        }
        else if (value.EndsWith('m'))
        {
            multiplier = 1_000_000;
            value = value[..^1];
        }

        return (long)(decimal.Parse(value, CultureInfo.InvariantCulture) * multiplier);
    }
}
