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

/// <param name="ClosureRows">Rows in the closure at this scale: one per (proper ancestor, resource) pair.</param>
internal sealed record ScaleStep(
    long Products,
    long Resources,
    long ClosureRows,
    double LoadRowsSeconds,
    double LoadClosureSeconds,
    double LoadIndexesSeconds,
    double LoadMaintenanceSeconds,
    long DatabaseBytes,
    IReadOnlyList<ScenarioResult> Scenarios)
{
    /// <summary>The grant-density pass (first scale only): the same scenarios with others' grants on the root.</summary>
    public IReadOnlyList<ScenarioResult> Density { get; init; } = [];

    /// <summary>The closure maintenance pass (first scale only).</summary>
    public IReadOnlyList<MaintenanceResult> Maintenance { get; init; } = [];

    /// <summary>The closure verification (first scale only).</summary>
    public ClosureCheck? ClosureCheck { get; init; }
}

/// <summary>
/// Three views of the closure that must agree: as the loader generated it from the dataset, after the
/// maintenance pass put every row back, and as SqlOS rebuilds it from the resources alone. The hash is
/// order-independent (a sum over the rows' keys), so equal counts and hashes mean the same rows.
/// </summary>
internal sealed record ClosureCheck(
    long LoadedRows,
    string LoadedHash,
    long RestoredRows,
    string RestoredHash,
    long RebuiltRows,
    string RebuiltHash,
    double RebuildSeconds)
{
    public bool Agrees
        => LoadedRows == RestoredRows && RestoredRows == RebuiltRows
           && LoadedHash == RestoredHash && RestoredHash == RebuiltHash;
}

internal sealed record GateResult(string Gate, string Subject, bool Passed, string Detail);
