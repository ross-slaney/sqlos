using System.Globalization;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>The command line of <c>sign-in</c>.</summary>
internal sealed class SignInBenchmarkOptions
{
    public const string Usage = """
        Usage: SqlOS.Benchmarks.SignIn [options]
               SqlOS.Benchmarks.SignIn compare [--steps] [--sql] <result.json>...

        Measures sign-in latency over HTTP against the behavior-lock host on a fresh database and
        writes every sample, and the SQL of one sign-in per scenario, as JSON.
        scripts/sign-in-benchmark.sh builds the source or package flavor and runs this; `compare`
        prints runs side by side as Markdown tables (--steps adds each request, --sql the statements
        whose time per sign-in changed most between the first build named and the last).

        Options:
          --provider sqlserver|postgresql  Database provider (default: SQLOS_TEST_PROVIDER, else sqlserver).
          --iterations N                   Measured sign-ins per scenario (default 400).
          --warmup N                       Unmeasured sign-ins per scenario before measuring (default 100).
          --scenarios a,b,...              Any of password, email-code, email-code-returning, hosted
                                           (default: all four).
          --accounts N                     Accounts the database holds before the run (default 0): a user,
                                           a verified email and a password each, written in SQL so the
                                           tables a sign-in reads have a deployment's size.
          --server "<connection string>"   Use this server instead of starting one with Aspire. The run
                                           creates its own database there and drops it afterwards.
          --output <file>                  JSON results (default: TestResults/SignInBenchmark/<utc>-<build>-<provider>.json).
          --label <text>                   How results name this build (default: the package version, or
                                           "source" and the commit it was built from).
        """;

    public DatabaseProvider Provider { get; private set; } = ProviderFromEnvironment();

    public int Iterations { get; private set; } = 400;

    public int Warmup { get; private set; } = 100;

    public IReadOnlyList<string> Scenarios { get; private set; } = SignInFlow.Names;

    public int Accounts { get; private set; }

    public string? Server { get; private set; }

    public string? Output { get; private set; }

    public string? Label { get; private set; }

    public bool ShowHelp { get; private set; }

    public static SignInBenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SignInBenchmarkOptions();
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            string Value() => index + 1 < args.Count
                ? args[++index]
                : throw new ArgumentException($"{name} needs a value.");

            switch (name)
            {
                case "--provider":
                    options.Provider = ParseProvider(Value());
                    break;
                case "--iterations":
                    options.Iterations = ParseCount(name, Value(), minimum: 1);
                    break;
                case "--warmup":
                    options.Warmup = ParseCount(name, Value(), minimum: 0);
                    break;
                case "--scenarios":
                    options.Scenarios = ParseScenarios(Value());
                    break;
                case "--accounts":
                    options.Accounts = ParseCount(name, Value(), minimum: 0);
                    break;
                case "--server":
                    options.Server = Value();
                    break;
                case "--output":
                    options.Output = Value();
                    break;
                case "--label":
                    options.Label = Value();
                    break;
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {name}");
            }
        }

        return options;
    }

    private static DatabaseProvider ProviderFromEnvironment()
        => Environment.GetEnvironmentVariable("SQLOS_TEST_PROVIDER") is { Length: > 0 } value
            ? ParseProvider(value)
            : DatabaseProvider.SqlServer;

    private static DatabaseProvider ParseProvider(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => DatabaseProvider.SqlServer,
            "postgresql" or "postgres" or "npgsql" => DatabaseProvider.PostgreSql,
            _ => throw new ArgumentException($"--provider must be sqlserver or postgresql (was '{value}').")
        };

    private static int ParseCount(string name, string value, int minimum)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= minimum
            ? count
            : throw new ArgumentException($"{name} must be a whole number of at least {minimum} (was '{value}').");

    private static IReadOnlyList<string> ParseScenarios(string value)
    {
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = names.Where(name => !SignInFlow.Names.Contains(name, StringComparer.Ordinal)).ToList();
        if (names.Length == 0 || unknown.Count > 0)
        {
            throw new ArgumentException($"--scenarios takes a comma-separated list of {string.Join(", ", SignInFlow.Names)} (unknown: {string.Join(", ", unknown)}).");
        }

        // Run order is fixed (the order of SignInFlow.Names), so a subset interleaves the same way.
        return SignInFlow.Names.Where(names.Contains).ToList();
    }
}
