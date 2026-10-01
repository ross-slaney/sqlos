using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// The checked-in allowlists of the architecture rules: the violations that existed when a rule
/// was introduced. A list only shrinks (see <see cref="AllowlistTests"/>), and every list is empty
/// by the end of the 8.0.0 refactor.
/// </summary>
internal static class Allowlist
{
    public const string HighWaterMarksFile = "high-water-marks.txt";

    /// <summary>The proof producers: the specification of rule 5, not an allowlist.</summary>
    public const string ProofProducersFile = "proof-producers.txt";

    /// <summary>The aggregates' members and their roots: the specification of rule 8, not an allowlist.</summary>
    public const string AggregateMembersFile = "aggregate-members.txt";

    public static string Directory { get; } = Path.Combine(FindRepositoryRoot(), "tests", "SqlOS.Tests", "Architecture", "Allowlists");

    /// <summary>The lines of a list file, without comments and blank lines, in file order.</summary>
    public static IReadOnlyList<string> Read(string fileName)
        => File.ReadAllLines(Path.Combine(Directory, fileName))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

    /// <summary>
    /// Fails unless the rule's violations are exactly the allowlist: a new violation must be fixed,
    /// and a fixed one must leave the list. The violations are written to the test output
    /// (<c>architecture-actual/</c>) to make updating the list a copy.
    /// </summary>
    public static void AssertMatches(string fileName, IReadOnlyList<string> violations)
    {
        var actualDirectory = Path.Combine(AppContext.BaseDirectory, "architecture-actual");
        System.IO.Directory.CreateDirectory(actualDirectory);
        var actualPath = Path.Combine(actualDirectory, fileName);
        File.WriteAllLines(actualPath, violations);

        var allowed = Read(fileName).ToHashSet(StringComparer.Ordinal);
        var added = violations.Where(violation => !allowed.Contains(violation)).ToList();
        var fixedLines = allowed.Except(violations, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (added.Count == 0 && fixedLines.Count == 0)
        {
            return;
        }

        var message = new List<string>();
        if (added.Count > 0)
        {
            message.Add($"{added.Count} new violation(s) of {fileName}. Fix them; the allowlist never grows:");
            message.AddRange(added.Select(line => "  + " + line));
        }

        if (fixedLines.Count > 0)
        {
            message.Add($"{fixedLines.Count} line(s) of {fileName} no longer occur. Delete them and lower the mark in {HighWaterMarksFile}:");
            message.AddRange(fixedLines.Select(line => "  - " + line));
        }

        message.Add($"Current violations: {actualPath}");
        Assert.Fail(string.Join(Environment.NewLine, message));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SqlOS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (SqlOS.sln).");
    }
}
