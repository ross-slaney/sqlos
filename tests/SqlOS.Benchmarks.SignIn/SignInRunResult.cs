using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>Everything one run measured, with every sample, as written to its JSON file.</summary>
internal sealed record SignInRunResult(
    string Benchmark,
    DateTime StartedAt,
    DateTime FinishedAt,
    BuildUnderTest Build,
    string Provider,
    string Server,
    string ServerVersion,
    MachineSnapshot Machine,
    int Warmup,
    int Iterations,
    int BackgroundAccounts,
    GarbageCollections Collections,
    double PasswordCheckMilliseconds,
    IReadOnlyList<ScenarioResult> Scenarios)
{
    public const string Name = "sqlos-sign-in-latency";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> WriteAsync(string? path, CancellationToken cancellationToken)
    {
        path ??= Path.Combine(
            "TestResults",
            "SignInBenchmark",
            $"{StartedAt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{Build.Mode}-{Provider.ToLowerInvariant()}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = File.Create(path);
        await JsonSerializer.SerializeAsync(file, this, Json, cancellationToken);
        return Path.GetFullPath(path);
    }

    public static SignInRunResult Read(string path)
    {
        var result = JsonSerializer.Deserialize<SignInRunResult>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"{path} is empty.");
        return result.Benchmark == Name
            ? result
            : throw new InvalidOperationException($"{path} is not a sign-in benchmark result.");
    }
}

/// <param name="Mode"><c>source</c> or <c>package</c>.</param>
/// <param name="Version">The loaded SqlOS assembly's informational version.</param>
/// <param name="Label">How tables name this build: the package version, or <c>source</c> and its commit.</param>
internal sealed record BuildUnderTest(string Mode, string Version, string Label);

internal sealed record MachineSnapshot(
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    string Runtime,
    string GarbageCollector,
    string? LoadAverageBeforeMeasuring,
    string? LoadAverageAfterMeasuring)
{
    public static MachineSnapshot Capture(string? loadBefore, string? loadAfter)
        => new(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription,
            GCSettings.IsServerGC ? "server" : "workstation",
            loadBefore,
            loadAfter);
}

internal sealed record GarbageCollections(int Gen0, int Gen1, int Gen2);

/// <summary>
/// What one password comparison costs on this machine: both builds hash passwords with ASP.NET
/// Core Identity's <see cref="PasswordHasher{TUser}"/> (PBKDF2), and every password sign-in pays
/// for one comparison, so this part of its latency is the same in both. Measured before warm-up.
/// </summary>
internal static class PasswordCheck
{
    public static double MedianMilliseconds(int samples = 30)
    {
        var hasher = new PasswordHasher<object>();
        var user = new object();
        var hash = hasher.HashPassword(user, BenchmarkHost.Password);
        var timings = new List<double>(samples);
        for (var index = 0; index < samples + 3; index++)
        {
            var started = Stopwatch.GetTimestamp();
            if (hasher.VerifyHashedPassword(user, hash, BenchmarkHost.Password) != PasswordVerificationResult.Success)
            {
                throw new InvalidOperationException("The password hasher did not verify its own hash.");
            }

            // The first comparisons warm the hasher up and are not counted.
            if (index >= 3)
            {
                timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        return LatencySummary.Of(timings).P50;
    }
}

/// <param name="Name">The flow.</param>
/// <param name="Description">Its requests.</param>
/// <param name="Steps">Its requests' names, in order.</param>
/// <param name="Total">Latency of the whole sign-in: the sum of its requests.</param>
/// <param name="StepLatencies">Latency of each request.</param>
/// <param name="SqlCommandsPerSignIn">Mean database commands per sign-in.</param>
/// <param name="SqlCommandsMin">Fewest commands one sign-in sent.</param>
/// <param name="SqlCommandsMax">Most commands one sign-in sent.</param>
/// <param name="DatabaseMillisecondsPerSignIn">Mean time per sign-in spent in those commands.</param>
/// <param name="AllocatedKilobytesPerSignIn">Mean bytes the process allocated while its requests ran, in KB.</param>
/// <param name="SqlTrace">The commands one sign-in sent, in order (the traced sign-in after warm-up).</param>
/// <param name="SqlProfile">Each statement the measured sign-ins ran: executions and time per sign-in, most time first.</param>
/// <param name="Samples">Every measured sign-in.</param>
internal sealed record ScenarioResult(
    string Name,
    string Description,
    IReadOnlyList<string> Steps,
    LatencySummary Total,
    IReadOnlyList<StepLatency> StepLatencies,
    double SqlCommandsPerSignIn,
    long SqlCommandsMin,
    long SqlCommandsMax,
    double DatabaseMillisecondsPerSignIn,
    double AllocatedKilobytesPerSignIn,
    IReadOnlyList<string> SqlTrace,
    IReadOnlyList<StatementProfile>? SqlProfile,
    IReadOnlyList<IterationSample> Samples)
{
    public static ScenarioResult Of(FlowMeasurement measurement)
        => Of(measurement.Flow, measurement.Samples, measurement.SqlTrace, measurement.SqlProfile);

    private static ScenarioResult Of(
        SignInFlow flow,
        IReadOnlyList<IterationSample> samples,
        IReadOnlyList<string> sqlTrace,
        IReadOnlyList<StatementProfile> sqlProfile)
        => new(
            flow.Name,
            flow.Description,
            flow.Steps,
            LatencySummary.Of(samples.Select(sample => sample.TotalMilliseconds).ToList()),
            flow.Steps
                .Select((step, index) => new StepLatency(step, LatencySummary.Of(samples.Select(sample => sample.StepMilliseconds[index]).ToList())))
                .ToList(),
            samples.Average(sample => sample.SqlCommands),
            samples.Min(sample => sample.SqlCommands),
            samples.Max(sample => sample.SqlCommands),
            samples.Average(sample => sample.DatabaseMilliseconds),
            samples.Average(sample => sample.AllocatedBytes) / 1024,
            sqlTrace,
            sqlProfile,
            samples);
}

internal sealed record StepLatency(string Name, LatencySummary Latency);

/// <param name="TotalMilliseconds">The sign-in's latency: its requests' summed latencies.</param>
/// <param name="StepMilliseconds">Each request's latency, in step order.</param>
/// <param name="SqlCommands">Commands its requests sent to the database.</param>
/// <param name="DatabaseMilliseconds">Their summed durations.</param>
/// <param name="AllocatedBytes">Bytes the process allocated while its requests ran.</param>
internal sealed record IterationSample(
    double TotalMilliseconds,
    IReadOnlyList<double> StepMilliseconds,
    long SqlCommands,
    double DatabaseMilliseconds,
    long AllocatedBytes);

/// <summary>The 1, 5 and 15 minute load averages, where the platform reports them.</summary>
internal static class MachineLoad
{
    public static string? Read()
    {
        try
        {
            if (File.Exists("/proc/loadavg"))
            {
                return string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3));
            }

            var averages = new double[3];
            return OperatingSystem.IsMacOS() && GetLoadAverage(averages, averages.Length) == averages.Length
                ? string.Join(' ', averages.Select(average => average.ToString("F2", CultureInfo.InvariantCulture)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // A blittable array is pinned, so the native call fills it in place.
    [DllImport("libc", EntryPoint = "getloadavg")]
    private static extern int GetLoadAverage(double[] averages, int count);
}
