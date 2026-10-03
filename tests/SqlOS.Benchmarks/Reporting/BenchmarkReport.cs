using SqlOS.Benchmarks.Scenarios;

namespace SqlOS.Benchmarks.Reporting;

internal sealed class BenchmarkReport
{
    public required string Provider { get; init; }
    public required string Engine { get; init; }
    public required string Server { get; init; }
    public required RunEnvironment Environment { get; init; }
    public required DatasetShape Dataset { get; init; }
    public List<ScaleStep> Steps { get; } = [];
    public List<GateResult> Gates { get; set; } = [];
    public double DurationSeconds { get; set; }
}

/// <param name="Cpu">
/// The processor model. Hosted CI runners come from a mixed pool, and identical plans have run 2.5x apart on
/// different CPUs, so absolute numbers are only comparable between runs on the same model.
/// </param>
internal sealed record RunEnvironment(
    string OperatingSystem,
    string Cpu,
    int ProcessorCount,
    long MemoryBytes,
    string Runtime,
    string? Commit,
    string? Ref)
{
    public static string DetectCpu()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                var model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (model is not null)
                {
                    return model.Split(':', 2)[1].Trim();
                }
            }
        }
        catch (IOException)
        {
        }

        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    }
}

internal sealed record DatasetShape(
    int Chains,
    int Stores,
    int LeafNodes,
    int OrganizationalNodes,
    int MaxDepth,
    int Seed,
    string Description);

internal sealed record ScaleStep(
    long Products,
    long Resources,
    double LoadRowsSeconds,
    double LoadIndexesSeconds,
    double LoadMaintenanceSeconds,
    long DatabaseBytes,
    IReadOnlyList<ScenarioResult> Scenarios)
{
    /// <summary>The grant-density pass (first scale only): the same scenarios with others' grants on the root.</summary>
    public IReadOnlyList<ScenarioResult> Density { get; init; } = [];

    /// <summary>The lineage maintenance pass (first scale only).</summary>
    public IReadOnlyList<MaintenanceResult> Maintenance { get; init; } = [];

    /// <summary>The lineage verification (first scale only).</summary>
    public LineageCheck? LineageCheck { get; init; }
}

/// <summary>
/// Three views of the lineage and the scope columns that must agree: as the loader generated them from the
/// dataset, after the maintenance pass put every row back, and as SqlOS rebuilds them from the resource tree
/// alone. Each is a row count and an order-independent hash over every column, so equal counts and hashes
/// mean the same values in every row.
/// </summary>
internal sealed record LineageCheck(
    string Loaded,
    string Restored,
    string Rebuilt,
    bool Agrees,
    double RebuildSeconds);

internal sealed record GateResult(string Gate, string Subject, bool Passed, string Detail);
