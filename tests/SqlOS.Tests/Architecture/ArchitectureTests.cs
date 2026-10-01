using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// The architecture rules of <c>docs/architecture/domain-model.md</c> §11 on SqlOS. Each rule that
/// starts from an allowlist fails on a new violation and on a fixed one that is still listed.
/// </summary>
[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void Entities_have_no_public_setters()
        => Allowlist.AssertMatches(
            "entity-public-setters.txt",
            ArchitectureRules.PublicSetters(SqlOSCode.Entities));

    [TestMethod]
    public void Only_the_owning_entity_writes_lifecycle_columns()
        => Allowlist.AssertMatches(
            "lifecycle-writes.txt",
            ArchitectureRules.LifecycleWrites(IlScanner.SqlOS, SqlOSCode.EntityNames));

    [TestMethod]
    public void Endpoints_renderers_and_middleware_never_touch_the_database()
        => Allowlist.AssertMatches(
            "adapter-database-access.txt",
            ArchitectureRules.DatabaseAccess(IlScanner.SqlOS, SqlOSCode.IsAdapter));

    [TestMethod]
    public void Only_processes_save()
        => Allowlist.AssertMatches(
            "save-call-sites.txt",
            ArchitectureRules.SaveCalls(IlScanner.SqlOS, SqlOSCode.IsProcess));

    [TestMethod]
    public void Proofs_are_constructed_only_by_their_producers()
    {
        var violations = ArchitectureRules.ProofViolations(
            IlScanner.SqlOS,
            ProofProducers.Load(),
            static _ => true);

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void Domain_and_process_code_reads_no_clock()
        => Allowlist.AssertMatches(
            "domain-clock-reads.txt",
            ArchitectureRules.DomainClockReads(IlScanner.SqlOS, SqlOSCode.IsDomain, SqlOSCode.IsProcess));

    [TestMethod]
    public void Domain_and_process_code_has_no_http_dependency()
        => Allowlist.AssertMatches(
            "domain-http-dependencies.txt",
            ArchitectureRules.DomainHttpDependencies(IlScanner.SqlOS, type => SqlOSCode.IsDomain(type) || SqlOSCode.IsProcess(type)));
}

/// <summary>Reads <c>proof-producers.txt</c>: lines of <c>ProofType &lt;- ProducerType</c>.</summary>
internal static class ProofProducers
{
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Load()
        => Parse(Allowlist.Read(Allowlist.ProofProducersFile));

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Parse(IEnumerable<string> lines)
        => lines
            .Select(line => line.Split(" <- ", 2, StringSplitOptions.TrimEntries))
            .Select(parts => parts.Length == 2
                ? (Proof: parts[0], Producer: parts[1])
                : throw new FormatException($"Expected 'ProofType <- ProducerType' in {Allowlist.ProofProducersFile}."))
            .GroupBy(pair => pair.Proof, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.Select(pair => pair.Producer).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
}
