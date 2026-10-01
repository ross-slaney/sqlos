using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// What package mode relies on in the frozen baseline (<see cref="Baseline"/>). Whether the
/// approved files differ from it exactly where the behavior ledger says is the job of
/// <c>scripts/check-behavior-baseline.sh</c>.
/// </summary>
[TestClass]
public sealed class BaselineTests
{
    private const string Recorded = """
        # SqlOS behavior lock transcript
        scenario: TimestampScenarios.Times_lose_their_marker_CurrentBehavior_KnownDefect_325
        profile: hosted

        ## 1. browser: known defect #325: the time has no Z
        > GET /sqlos/admin/auth/api/users/{usr#1}
        < 200 OK
            {"createdAt":"{datetime:unspecified}"}
        # 3 sign-in codes were emailed to the address.

        ## audit: no events
          (none)

        ## audit, sorted by content: still none
          (none)

        ## effects: none either
          (none)

        ## document: the metadata
            <EntityDescriptor />

        ## 2. api
        < 404 Not Found
        """;

    [TestMethod]
    public void The_baseline_records_the_released_package_this_suite_tests()
        => Assert.AreEqual(SqlOSUnderTest.BaselineVersion, Baseline.Release, "Baseline/release.txt and -p:SqlOSBaselineVersion disagree.");

    [TestMethod]
    public void Scenario_names_and_step_captions_are_labels()
    {
        var relabeled = Recorded
            .Replace("Times_lose_their_marker_CurrentBehavior_KnownDefect_325", "Times_keep_their_marker", StringComparison.Ordinal)
            .Replace("known defect #325: the time has no Z", "the time has its Z", StringComparison.Ordinal)
            .Replace("## audit: no events", "## audit: nothing is audited", StringComparison.Ordinal)
            .Replace("## audit, sorted by content: still none", "## audit, sorted by content: nothing", StringComparison.Ordinal)
            .Replace("## effects: none either", "## effects: nothing is sent", StringComparison.Ordinal)
            .Replace("## document: the metadata", "## document: SAML metadata", StringComparison.Ordinal)
            .Replace("## 2. api", "## 2. api: an unknown user", StringComparison.Ordinal);

        Assert.AreNotEqual(Recorded, relabeled);
        Assert.AreEqual(Baseline.WithoutLabels(Recorded), Baseline.WithoutLabels(relabeled));
    }

    [TestMethod]
    [DataRow("{\"createdAt\":\"{datetime:unspecified}\"}", "{\"createdAt\":\"{datetime:utc-z}\"}", DisplayName = "a body")]
    [DataRow("# 3 sign-in codes were emailed to the address.", "# 6 sign-in codes were emailed to the address.", DisplayName = "a note, which may record observed values")]
    [DataRow("profile: hosted", "profile: headless", DisplayName = "the profile")]
    [DataRow("## 1. browser: known defect", "## 1. operator: known defect", DisplayName = "the actor of a step")]
    [DataRow("## audit, sorted by content: still none", "## audit: still none", DisplayName = "the order of an audit section")]
    [DataRow("< 404 Not Found", "< 200 OK", DisplayName = "a status")]
    public void Everything_else_is_behavior(string before, string after)
    {
        Assert.AreEqual(1, Recorded.Split(before).Length - 1, $"The sample must hold '{before}' once.");

        Assert.AreNotEqual(
            Baseline.WithoutLabels(Recorded),
            Baseline.WithoutLabels(Recorded.Replace(before, after, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Baseline_files_mirror_their_approved_files()
    {
        Assert.AreEqual(
            "Scenarios/Hosted/Approved/HostedScenarios.A.verified.txt",
            Baseline.PathUnderBaseline("tests/SqlOS.BehaviorLock/Scenarios/Hosted/Approved/HostedScenarios.A.verified.txt"));
        Assert.AreEqual("Schema/SqlServer.verified.txt", Baseline.PathUnderBaseline("tests/SqlOS.BehaviorLock/Schema/SqlServer.verified.txt"));
        Assert.AreEqual(
            "packages/headless/tests/__snapshots__/public-surface.snap.txt",
            Baseline.PathUnderBaseline("packages/headless/tests/__snapshots__/public-surface.snap.txt"));
        Assert.AreEqual(
            RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "Baseline", "PublicApi", "SqlOS.verified.txt"),
            Baseline.FileFor(RepositoryPaths.Combine("tests", "SqlOS.BehaviorLock", "PublicApi", "SqlOS.verified.txt")));
    }

    [TestMethod]
    public void Every_committed_rename_leads_from_a_baseline_file_to_an_approved_file()
    {
        foreach (var (earlier, later) in Baseline.Renames.Renames)
        {
            var original = Baseline.Renames.OriginalOf(earlier);
            var current = Baseline.Renames.CurrentOf(later);

            Assert.IsTrue(File.Exists(RepositoryPaths.Combine(Baseline.RelativeRoot, Baseline.PathUnderBaseline(original))), $"{original} has no baseline file.");
            Assert.IsTrue(File.Exists(RepositoryPaths.Combine(current)), $"{earlier} is renamed to {current}, which does not exist.");
            Assert.IsFalse(File.Exists(RepositoryPaths.Combine(earlier)), $"{earlier} is renamed but still exists.");
            Assert.AreEqual(
                RepositoryPaths.Combine(Baseline.RelativeRoot, Baseline.PathUnderBaseline(original)),
                Baseline.FileFor(current));
        }
    }

    [TestMethod]
    public void The_rename_log_follows_chains()
    {
        var log = RenameLog.Parse("""
            # A file renamed twice is a chain of two lines.
            a.verified.txt -> b.verified.txt

            b.verified.txt -> c.verified.txt
            """);

        Assert.AreEqual("a.verified.txt", log.OriginalOf("c.verified.txt"));
        Assert.AreEqual("a.verified.txt", log.OriginalOf("b.verified.txt"));
        Assert.AreEqual("c.verified.txt", log.CurrentOf("a.verified.txt"));
        Assert.AreEqual("unrenamed.verified.txt", log.OriginalOf("unrenamed.verified.txt"));
        Assert.AreEqual(2, log.Renames.Count);
    }

    [TestMethod]
    [DataRow("a -> b\na -> c", DisplayName = "one file renamed twice from the same name")]
    [DataRow("a -> c\nb -> c", DisplayName = "two files renamed to one name")]
    [DataRow("a -> a", DisplayName = "a rename to itself")]
    [DataRow("a -> b\nb -> a", DisplayName = "a cycle")]
    [DataRow("a => b", DisplayName = "no arrow")]
    [DataRow("a -> b -> c", DisplayName = "two arrows")]
    [DataRow("a b -> c", DisplayName = "a path with a space")]
    public void The_rename_log_refuses_ambiguity(string text)
    {
        try
        {
            RenameLog.Parse(text);
        }
        catch (FormatException)
        {
            return;
        }

        Assert.Fail($"renames.txt accepted:\n{text}");
    }
}
