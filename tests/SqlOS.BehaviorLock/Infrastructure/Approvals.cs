using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using VerifyTests;

namespace SqlOS.BehaviorLock.Infrastructure;

/// <summary>
/// Compares rendered output with approved files using Verify. Approved files are
/// <c>*.verified.txt</c>; a mismatch writes <c>*.received.txt</c> beside them and fails with a
/// compact diff.
/// <list type="bullet">
/// <item><c>BEHAVIOR_LOCK_ACCEPT=1</c> accepts every mismatch locally (never on a build server).</item>
/// <item><c>BEHAVIOR_LOCK_DIFF=1</c> launches the configured diff tool on a mismatch.</item>
/// <item>In package mode (<c>-p:SqlOSUnderTest=package</c>) an approved file listed in the
/// behavior ledger is skipped as inconclusive: it describes intended 8.0.0 behavior, not the
/// released package.</item>
/// </list>
/// </summary>
public static partial class Approvals
{
    public const string AcceptEnvironmentVariable = "BEHAVIOR_LOCK_ACCEPT";
    public const string DiffEnvironmentVariable = "BEHAVIOR_LOCK_DIFF";

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
        await VerifyAsync(directory, scenario.Name, text);
    }

    /// <summary>Approves <paramref name="text"/> as <c>{directory}/{name}.verified.txt</c>.</summary>
    public static async Task VerifyAsync(string directory, string name, string text)
    {
        // Approved files live in git; trailing whitespace is never meaningful in rendered output
        // and would fail `git diff --check`.
        text = TrailingWhitespace().Replace(text.ReplaceLineEndings("\n"), string.Empty);
        var approved = Path.Combine(directory, name + ".verified.txt");
        if (SqlOSUnderTest.IsPackage && BehaviorLedger.Load().Lists(approved))
        {
            Assert.Inconclusive(
                $"{RepositoryPaths.Relative(approved)} is listed in the 8.0 behavior ledger: it records intended 8.0.0 behavior, " +
                "so the released package is not held to it.");
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
                await File.WriteAllTextAsync(approved, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
