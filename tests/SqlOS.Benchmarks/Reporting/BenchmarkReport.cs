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

internal sealed record RunEnvironment(
    string OperatingSystem,
    int ProcessorCount,
    long MemoryBytes,
    string Runtime,
    string? Commit,
    string? Ref);

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
}

internal sealed record GateResult(string Gate, string Subject, bool Passed, string Detail);
