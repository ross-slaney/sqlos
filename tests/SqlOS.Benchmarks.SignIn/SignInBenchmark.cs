using System.Globalization;
using SqlOS.BehaviorLock.Infrastructure;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// The sign-in latency benchmark. It drives the behavior-lock host over HTTP, in-process, against
/// a real database: a direct password sign-in, an email-code sign-in and the hosted authorize,
/// password and token round trip. The host builds against SqlOS as source or as the released
/// package (<c>-p:SqlOSUnderTest</c>), so the same code measures both. See
/// <c>docs/architecture/8.0-baseline-metrics.md</c> for the method and the recorded numbers.
/// </summary>
internal static class SignInBenchmark
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == SignInComparison.Command)
        {
            return SignInComparison.Run(args[1..]);
        }

        SignInBenchmarkOptions options;
        try
        {
            options = SignInBenchmarkOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(SignInBenchmarkOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(SignInBenchmarkOptions.Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        // Fails before anything runs when the build mixes the source and package assemblies.
        Console.WriteLine(SqlOSUnderTest.VerifyLoadedAssembly());
        var build = DescribeBuild(options.Label);
        var startedAt = DateTime.UtcNow;
        await using var server = await BenchmarkDatabaseServer.StartAsync(options.Provider, options.Server, cancellation.Token);
        var serverVersion = await server.ReadVersionAsync(cancellation.Token);
        Console.WriteLine($"Database: {options.Provider}, {server.Source}: {serverVersion}");

        var passwordCheck = PasswordCheck.MedianMilliseconds();
        SignInRunResult result;
        var connectionString = await server.CreateDatabaseAsync(cancellation.Token);
        try
        {
            using var meter = new SqlCommandMeter(options.Provider, server.DatabaseName(connectionString));
            await using var host = await BenchmarkHost.StartAsync(options.Provider, connectionString, cancellation.Token);
            await BackgroundAccounts.SeedAsync(server, connectionString, host.Schema, options.Accounts, cancellation.Token);
            var flows = options.Scenarios.Select(name => SignInFlow.Create(name, host)).ToList();
            var measurement = await new SignInRunner(host, meter, flows, options.Warmup, options.Iterations).RunAsync(cancellation.Token);
            result = new SignInRunResult(
                SignInRunResult.Name,
                startedAt,
                DateTime.UtcNow,
                build,
                options.Provider.ToString(),
                server.Source,
                serverVersion,
                MachineSnapshot.Capture(measurement.LoadBefore, measurement.LoadAfter),
                options.Warmup,
                options.Iterations,
                options.Accounts,
                measurement.Collections,
                passwordCheck,
                measurement.Flows.Select(ScenarioResult.Of).ToList());
        }
        finally
        {
            await server.DropDatabaseAsync(connectionString);
        }

        Print(result);
        Console.WriteLine($"Results: {await result.WriteAsync(options.Output, CancellationToken.None)}");
        return 0;
    }

    private static BuildUnderTest DescribeBuild(string? label)
    {
        var version = SqlOSUnderTest.InformationalVersion;
        if (SqlOSUnderTest.IsPackage)
        {
            return new BuildUnderTest(SqlOSUnderTest.Mode, version, label ?? SqlOSUnderTest.BaselineVersion);
        }

        // Source builds carry the commit after '+' (SourceLink), but not uncommitted changes: two
        // builds of one commit need a --label each to stay apart.
        var commit = version.Split('+') is [_, var revision, ..] && revision.Length >= 7 ? revision[..7] : null;
        return new BuildUnderTest(SqlOSUnderTest.Mode, version, label ?? (commit == null ? "source" : $"source {commit}"));
    }

    private static void Print(SignInRunResult result)
    {
        var machine = result.Machine;
        Console.WriteLine();
        Console.WriteLine($"=== Sign-in latency: SqlOS {result.Build.Label} on {result.Provider}, {result.Iterations} sign-ins per scenario after {result.Warmup} warm-up, {result.BackgroundAccounts:N0} background accounts ===");
        Console.WriteLine($"{machine.OperatingSystem} {machine.Architecture}, {machine.ProcessorCount} CPUs, {machine.Runtime}, {machine.GarbageCollector} GC");
        Console.WriteLine($"Load average before / after measuring: {machine.LoadAverageBeforeMeasuring ?? "n/a"} / {machine.LoadAverageAfterMeasuring ?? "n/a"}; collections gen0 {result.Collections.Gen0}, gen1 {result.Collections.Gen1}, gen2 {result.Collections.Gen2}");
        Console.WriteLine($"One password comparison (PBKDF2, the same in every build): {Number(result.PasswordCheckMilliseconds)} ms");
        Console.WriteLine();
        Console.WriteLine($"{"Scenario",-12} {"Request",-10} {"n",5} {"mean",8} {"stdev",7} {"min",7} {"p50",7} {"p95",7} {"p99",7} {"max",8} {"far out",8}  (ms)");
        foreach (var scenario in result.Scenarios)
        {
            PrintRow(scenario.Name, "total", scenario.Total);
            if (scenario.StepLatencies.Count > 1)
            {
                foreach (var step in scenario.StepLatencies)
                {
                    PrintRow(string.Empty, step.Name, step.Latency);
                }
            }

            Console.WriteLine(
                $"{string.Empty,-12} SQL: {Number(scenario.SqlCommandsPerSignIn)} commands per sign-in ({scenario.SqlCommandsMin}-{scenario.SqlCommandsMax}), "
                + $"{Number(scenario.DatabaseMillisecondsPerSignIn)} ms in them; {scenario.AllocatedKilobytesPerSignIn.ToString("F0", CultureInfo.InvariantCulture)} KB allocated");
        }

        Console.WriteLine();
    }

    private static void PrintRow(string scenario, string step, LatencySummary latency)
        => Console.WriteLine(
            $"{scenario,-12} {step,-10} {latency.Count,5} {Number(latency.Mean),8} {Number(latency.StandardDeviation),7} {Number(latency.Min),7} "
            + $"{Number(latency.P50),7} {Number(latency.P95),7} {Number(latency.P99),7} {Number(latency.Max),8} {latency.FarOutliers,8}");

    internal static string Number(double value)
        => value.ToString(Math.Abs(value) < 10 ? "F2" : "F1", CultureInfo.InvariantCulture);
}
