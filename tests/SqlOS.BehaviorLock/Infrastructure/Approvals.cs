using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using VerifyTests;

namespace SqlOS.BehaviorLock.Infrastructure;

/// <summary>
/// Compares rendered output with approved files. Approved files are <c>*.verified.txt</c>; a
/// mismatch writes <c>*.received.txt</c> beside the file it was compared with and fails with a
/// compact diff.
/// <list type="bullet">
/// <item>Source mode compares with the approved file beside the scenario (with Verify), which
/// records the build under test.</item>
/// <item>Package mode (<c>-p:SqlOSUnderTest=package</c>) compares every approval with its frozen
/// baseline (<see cref="Baseline"/>), which records the released package, and skips nothing.
/// Transcripts are compared without their labels (<see cref="Baseline.WithoutLabels"/>).</item>
/// <item><c>BEHAVIOR_LOCK_ACCEPT=1</c> accepts every mismatch locally (never on a build server). In
/// package mode it only records a missing baseline: an existing baseline is never rewritten.</item>
/// <item><c>BEHAVIOR_LOCK_DIFF=1</c> launches the configured diff tool on a source-mode mismatch.</item>
/// </list>
/// </summary>
public static partial class Approvals
{
    public const string AcceptEnvironmentVariable = "BEHAVIOR_LOCK_ACCEPT";
    public const string DiffEnvironmentVariable = "BEHAVIOR_LOCK_DIFF";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [ModuleInitializer]
    public static void Initialize()
    {
        // Rendered transcripts are already scrubbed; Verify must not rewrite them.
        VerifierSettings.DontScrubDateTimes();
        VerifierSettings.DontScrubGuids();
        VerifierSettings.DontScrubProjectDirectory();
        VerifierSettings.DontScrubSolutionDirectory();
        VerifierSettings.DontScrubUserProfile();
        VerifierSettings.DisableDateCounting();
        VerifierSettings.UseUtf8NoBom();
        if (!IsSet(DiffEnvironmentVariable))
        {
            DiffEngine.DiffRunner.Disabled = true;
        }
    }

    /// <summary>Whether this run accepts mismatches: requested locally, never on a build server.</summary>
    public static bool Accepting => IsSet(AcceptEnvironmentVariable) && !DiffEngine.BuildServerDetector.Detected;

    /// <summary>Approves a scenario transcript next to the scenario's source file.</summary>
    public static async Task VerifyTranscriptAsync(ScenarioContext scenario, string text)
    {
        if (scenario.Repeat > 1)
        {
            scenario.RecordRunTranscript(text);
            var first = scenario.RunTranscripts[0];
            if (!string.Equals(first, text, StringComparison.Ordinal))
            {
                throw new AssertFailedException(
                    $"{scenario.Name} is not deterministic: run {scenario.Run} of {scenario.Repeat} differs from run 1.\n" +
                    LineDiff.Unified(first, text, "run 1", $"run {scenario.Run}"));
            }

            if (!scenario.IsFinalRun)
            {
                return;
            }
        }

        var directory = Path.Combine(Path.GetDirectoryName(scenario.SourceFile)!, "Approved");
        await VerifyAsync(directory, scenario.Name, text, transcript: true);
    }

    /// <summary>Approves <paramref name="text"/> as <c>{directory}/{name}.verified.txt</c>.</summary>
    public static Task VerifyAsync(string directory, string name, string text)
        => VerifyAsync(directory, name, text, transcript: false);

    private static async Task VerifyAsync(string directory, string name, string text, bool transcript)
    {
        // Approved files live in git; trailing whitespace is never meaningful in rendered output
        // and would fail `git diff --check`.
        text = TrailingWhitespace().Replace(text.ReplaceLineEndings("\n"), string.Empty);
        var approved = Path.Combine(directory, name + ".verified.txt");
        if (SqlOSUnderTest.IsPackage)
        {
            await VerifyBaselineAsync(approved, text, transcript);
            return;
        }

        var previous = File.Exists(approved) ? await File.ReadAllTextAsync(approved) : null;
        var settings = new VerifySettings();
        if (!IsSet(DiffEnvironmentVariable))
        {
            settings.DisableDiff();
        }

        try
        {
            using var verifier = new InnerVerifier(directory, name, settings);
            await verifier.VerifyString(text, "txt");
        }
        catch (Exception exception) when (exception.GetType().Name == "VerifyException")
        {
            var received = Path.Combine(directory, name + ".received.txt");
            if (Accepting)
            {
                // Accept by promoting exactly the text Verify compared.
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(approved, text, Utf8NoBom);
                File.Delete(received);
                return;
            }

            var message = new StringBuilder();
            if (previous == null)
            {
                message.Append("No approved transcript yet for ").Append(RepositoryPaths.Relative(approved)).Append(".\n");
            }
            else
            {
                message.Append("Behavior changed: ").Append(RepositoryPaths.Relative(approved)).Append('\n');
                message.Append(LineDiff.Unified(Normalize(previous), text, "approved", "received"));
            }

            message.Append("\nReview ").Append(RepositoryPaths.Relative(received)).Append(
                ". Accept intended changes with BEHAVIOR_LOCK_ACCEPT=1 (locally), and record them in " +
                "docs/architecture/8.0-behavior-ledger.md. See tests/SqlOS.BehaviorLock/README.md.");
            throw new AssertFailedException(message.ToString());
        }
    }

    /// <summary>
    /// Package mode: compares the released package's output with the frozen baseline of
    /// <paramref name="approved"/>, transcripts without their labels. The package never changes, so a
    /// mismatch means the scenario or the harness now observes something else.
    /// </summary>
    private static async Task VerifyBaselineAsync(string approved, string text, bool transcript)
    {
        if (!string.Equals(Baseline.Release, SqlOSUnderTest.BaselineVersion, StringComparison.Ordinal))
        {
            throw new AssertFailedException(
                $"The baseline records SqlOS {Baseline.Release}, but this run tests the {SqlOSUnderTest.BaselineVersion} package.");
        }

        var baseline = Baseline.FileFor(approved);
        var received = baseline[..^".verified.txt".Length] + ".received.txt";
        if (!File.Exists(baseline))
        {
            if (Accepting)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
                await File.WriteAllTextAsync(baseline, text, Utf8NoBom);
                return;
            }

            throw new AssertFailedException(
                $"No baseline for {RepositoryPaths.Relative(approved)}. New coverage records what SqlOS {Baseline.Release} does as " +
                $"{RepositoryPaths.Relative(baseline)}: run it in package mode with BEHAVIOR_LOCK_ACCEPT=1 (locally) and commit that file. " +
                "See tests/SqlOS.BehaviorLock/README.md.");
        }

        var recorded = Normalize(await File.ReadAllTextAsync(baseline));
        var (expected, actual) = transcript ? (Baseline.WithoutLabels(recorded), Baseline.WithoutLabels(text)) : (recorded, text);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            File.Delete(received);
            return;
        }

        await File.WriteAllTextAsync(received, text, Utf8NoBom);
        var subject = RepositoryPaths.Relative(baseline);
        var current = RepositoryPaths.Relative(approved);
        if (!string.Equals(Baseline.Renames.OriginalOf(current), current, StringComparison.Ordinal))
        {
            subject += $", the baseline of {current}";
        }

        throw new AssertFailedException(
            $"The released package no longer matches its baseline: {subject}" +
            (transcript ? " (compared without the scenario name and step captions)\n" : "\n") +
            LineDiff.Unified(expected, actual, "baseline", "received") +
            $"\nReview {RepositoryPaths.Relative(received)}. Baseline files never change: restore what the scenario or the harness " +
            "observes, or lock the new observation with a new scenario. See tests/SqlOS.BehaviorLock/README.md.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"[ \t]+(?=\n|$)")]
    private static partial System.Text.RegularExpressions.Regex TrailingWhitespace();

    private static bool IsSet(string variable)
        => Environment.GetEnvironmentVariable(variable) is { } value
           && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where the repository is, so failure messages and gates can use repository-relative paths.</summary>
public static class RepositoryPaths
{
    public static string Root { get; } = typeof(RepositoryPaths).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "RepositoryRoot")
        .Value!;

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}
