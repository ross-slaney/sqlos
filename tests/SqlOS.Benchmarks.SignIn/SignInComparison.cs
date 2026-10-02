using System.Globalization;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// <c>compare</c>: prints runs side by side as Markdown, one table per provider. Columns are
/// the runs in the order given, named by build. Each later build gets a change column against the
/// first build named, each build over all of its runs' samples pooled. <c>Run to run</c> is the
/// widest gap between two runs of the same build, the noise a change must exceed to mean anything.
/// <c>--sql</c> adds, per scenario, the statements whose time per sign-in changed most between
/// those two builds, from the runs' statement profiles.
/// </summary>
internal static partial class SignInComparison
{
    public const string Command = "compare";

    private const int ProfileRows = 12;

    public static int Run(IReadOnlyList<string> args)
    {
        var withSteps = args.Contains("--steps");
        var withSql = args.Contains("--sql");
        var files = args.Where(arg => arg is not ("--steps" or "--sql")).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine(SignInBenchmarkOptions.Usage);
            return 2;
        }

        // Runs compare only with runs on the same provider and the same number of accounts.
        var runs = files.Select(SignInRunResult.Read).ToList();
        foreach (var group in runs.GroupBy(run => (run.Provider, run.BackgroundAccounts)))
        {
            var title = group.Key.BackgroundAccounts == 0
                ? group.Key.Provider
                : $"{group.Key.Provider}, {group.Key.BackgroundAccounts.ToString("N0", CultureInfo.InvariantCulture)} background accounts";
            Print(title, group.ToList(), withSteps);
            if (withSql)
            {
                PrintSqlProfile(group.ToList());
            }
        }

        return 0;
    }

    /// <summary>
    /// The statements whose time per sign-in moved most from the first build to the last, each
    /// build's runs averaged. Statements match by text, with EF Core's parameter names (taken from
    /// the C# variables, so they differ between builds) masked.
    /// </summary>
    private static void PrintSqlProfile(IReadOnlyList<SignInRunResult> runs)
    {
        var builds = runs.Select(run => run.Build.Label).Distinct().ToList();
        if (builds.Count < 2 || runs.Any(run => run.Scenarios.Any(scenario => scenario.SqlProfile == null)))
        {
            Console.WriteLine("(A statement profile needs two builds, and runs that recorded one.)");
            Console.WriteLine();
            return;
        }

        foreach (var scenario in runs[0].Scenarios.Select(scenario => scenario.Name))
        {
            var before = AverageProfile(runs, builds[0], scenario);
            var after = AverageProfile(runs, builds[^1], scenario);
            var rows = before.Keys.Union(after.Keys)
                .Select(statement => (
                    Statement: statement,
                    Before: before.GetValueOrDefault(statement),
                    After: after.GetValueOrDefault(statement)))
                .OrderByDescending(row => Math.Abs(row.After.Milliseconds - row.Before.Milliseconds))
                .Take(ProfileRows)
                .ToList();

            Console.WriteLine($"#### {scenario}: statements whose time per sign-in changed most, {builds[0]} to {builds[^1]}");
            Console.WriteLine();
            Console.WriteLine("| Statement | Runs per sign-in | ms per sign-in | Change |");
            Console.WriteLine("|---|---:|---:|---:|");
            foreach (var row in rows)
            {
                Console.WriteLine(
                    $"| `{Cell(row.Statement)}` | {Count(row.Before.Executions)} to {Count(row.After.Executions)} | "
                    + $"{Count(row.Before.Milliseconds)} to {Count(row.After.Milliseconds)} | "
                    + $"{(row.After.Milliseconds - row.Before.Milliseconds).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} |");
            }

            Console.WriteLine();
        }

        static string Count(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        static string Cell(string statement)
        {
            // A SELECT's column list says little: show what it reads and how it filters.
            var from = statement.IndexOf(" FROM ", StringComparison.Ordinal);
            var text = statement.StartsWith("SELECT ", StringComparison.Ordinal) && from > 0
                ? "SELECT … " + statement[(from + 1)..]
                : statement;
            var shown = text.Length <= 150 ? text : text[..150] + "…";
            return shown.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, (double Executions, double Milliseconds)> AverageProfile(
        IReadOnlyList<SignInRunResult> runs,
        string build,
        string scenario)
    {
        var profiles = runs
            .Where(run => run.Build.Label == build)
            .Select(run => run.Scenarios.Single(candidate => candidate.Name == scenario).SqlProfile!)
            .ToList();
        return profiles
            .SelectMany(profile => profile)
            .GroupBy(statement => StatementKey(statement.Statement))
            .ToDictionary(
                group => group.Key,
                group => (group.Sum(statement => statement.ExecutionsPerSignIn) / profiles.Count,
                    group.Sum(statement => statement.MillisecondsPerSignIn) / profiles.Count));
    }

    private static string StatementKey(string statement)
        => EfParameterName().Replace(Whitespace().Replace(statement, " ").Trim(), "@p");

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();

    [System.Text.RegularExpressions.GeneratedRegex(@"@__\w+")]
    private static partial System.Text.RegularExpressions.Regex EfParameterName();

    private static void Print(string title, IReadOnlyList<SignInRunResult> runs, bool withSteps)
    {
        var builds = runs.Select(run => run.Build.Label).Distinct().ToList();
        var columns = runs
            .Select((run, index) => $"{run.Build.Label} #{runs.Take(index + 1).Count(other => other.Build.Label == run.Build.Label)}")
            .ToList();

        Console.WriteLine($"### {title}");
        Console.WriteLine();
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            Console.WriteLine(
                $"- {columns[index]}: {run.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, {run.Iterations} sign-ins per scenario after {run.Warmup} warm-up, "
                + $"load {run.Machine.LoadAverageBeforeMeasuring ?? "n/a"} before and {run.Machine.LoadAverageAfterMeasuring ?? "n/a"} after, "
                + $"collections {run.Collections.Gen0}/{run.Collections.Gen1}/{run.Collections.Gen2}, "
                + $"password comparison {SignInBenchmark.Number(run.PasswordCheckMilliseconds)} ms");
        }

        Console.WriteLine();
        var changes = builds.Skip(1).Select(build => $"{build} vs {builds[0]}").ToList();
        Console.WriteLine($"| Scenario | Statistic | {string.Join(" | ", columns.Concat(changes))} | Run to run |");
        Console.WriteLine($"|---|---|{string.Concat(columns.Concat(changes).Select(_ => "---:|"))}---:|");
        // Only scenarios every run measured can stand side by side.
        var scenarios = runs[0].Scenarios
            .Select(scenario => scenario.Name)
            .Where(name => runs.All(run => run.Scenarios.Any(scenario => scenario.Name == name)));
        foreach (var scenario in scenarios)
        {
            var results = runs.Select(run => run.Scenarios.Single(candidate => candidate.Name == scenario)).ToList();
            var rows = new List<(string Name, Func<IterationSample, double> Value, Func<LatencySummary, double> Statistic)>
            {
                ("p50 ms", sample => sample.TotalMilliseconds, latency => latency.P50),
                ("p95 ms", sample => sample.TotalMilliseconds, latency => latency.P95),
                ("p99 ms", sample => sample.TotalMilliseconds, latency => latency.P99),
                ("mean ms", sample => sample.TotalMilliseconds, latency => latency.Mean)
            };
            // A single request is the whole sign-in: its rows would repeat the total's.
            if (withSteps && results[0].Steps.Count > 1)
            {
                for (var step = 0; step < results[0].Steps.Count; step++)
                {
                    var index = step;
                    rows.Add(($"{results[0].Steps[step]} p50 ms", sample => sample.StepMilliseconds[index], latency => latency.P50));
                    rows.Add(($"{results[0].Steps[step]} p95 ms", sample => sample.StepMilliseconds[index], latency => latency.P95));
                }
            }

            var first = true;
            foreach (var (name, value, statistic) in rows)
            {
                var perRun = results.Select(result => statistic(LatencySummary.Of(result.Samples.Select(value).ToList()))).ToList();
                var pooled = builds.ToDictionary(
                    build => build,
                    build => statistic(LatencySummary.Of(runs
                        .Select((run, index) => (run, result: results[index]))
                        .Where(pair => pair.run.Build.Label == build)
                        .SelectMany(pair => pair.result.Samples.Select(value))
                        .ToList())));
                var cells = perRun.Select(SignInBenchmark.Number)
                    .Concat(builds.Skip(1).Select(build => Percent(pooled[build] / pooled[builds[0]] - 1)));
                Console.WriteLine(
                    $"| {(first ? scenario : string.Empty)} | {name} | {string.Join(" | ", cells)} | {RunToRun(runs, perRun)} |");
                first = false;
            }

            PrintMeanRow("SQL commands", runs, results, builds, sample => sample.SqlCommands, relative: false);
            PrintMeanRow("SQL ms", runs, results, builds, sample => sample.DatabaseMilliseconds, relative: true);
            PrintMeanRow("KB allocated", runs, results, builds, sample => sample.AllocatedBytes / 1024.0, relative: true);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// A per-sign-in mean (commands, database time, allocation): each run's, then each later build's
    /// change from the first, as a difference (<paramref name="relative"/> false) or a percentage.
    /// </summary>
    private static void PrintMeanRow(
        string name,
        IReadOnlyList<SignInRunResult> runs,
        IReadOnlyList<ScenarioResult> results,
        IReadOnlyList<string> builds,
        Func<IterationSample, double> value,
        bool relative)
    {
        var perRun = results.Select(result => result.Samples.Average(value)).ToList();
        var baseline = MeanOf(runs, results, builds[0], value);
        var changes = builds.Skip(1)
            .Select(build => MeanOf(runs, results, build, value))
            .Select(candidate => relative
                ? Percent(candidate / baseline - 1)
                : (candidate - baseline).ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture));
        Console.WriteLine(
            $"| | {name} | {string.Join(" | ", perRun.Select(mean => mean.ToString("F1", CultureInfo.InvariantCulture)).Concat(changes))} | "
            + $"{(relative ? RunToRun(runs, perRun) : string.Empty)} |");
    }

    private static double MeanOf(IReadOnlyList<SignInRunResult> runs, IReadOnlyList<ScenarioResult> results, string build, Func<IterationSample, double> value)
        => runs.Select((run, index) => (run, result: results[index]))
            .Where(pair => pair.run.Build.Label == build)
            .SelectMany(pair => pair.result.Samples)
            .Average(value);

    /// <summary>The widest relative gap between two runs of one build, over every build with two or more runs.</summary>
    private static string RunToRun(IReadOnlyList<SignInRunResult> runs, IReadOnlyList<double> perRun)
    {
        var gaps = runs
            .Select((run, index) => (run.Build.Label, Value: perRun[index]))
            .GroupBy(entry => entry.Label)
            .Where(build => build.Count() > 1)
            .Select(build => build.Max(entry => entry.Value) / build.Min(entry => entry.Value) - 1)
            .ToList();
        return gaps.Count > 0 ? Percent(gaps.Max()).TrimStart('+') : string.Empty;
    }

    private static string Percent(double ratio)
        => (ratio * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
}
