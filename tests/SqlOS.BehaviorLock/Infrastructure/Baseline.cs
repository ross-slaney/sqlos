namespace SqlOS.BehaviorLock.Infrastructure;

/// <summary>
/// The frozen baseline (<c>tests/SqlOS.BehaviorLock/Baseline</c>): every approved file exactly as it
/// was when the behavior lock matched the released SqlOS 7.2.1, so it records what 7.2.1 does. The
/// approved files beside the scenarios record what the build under test does, and the behavior
/// ledger names exactly the files where the two differ (<c>scripts/check-behavior-baseline.sh</c>).
/// </summary>
/// <remarks>
/// A baseline file sits at its approved file's path under <c>Baseline/</c>: relative to this suite
/// for the suite's approvals, relative to the repository for the headless snapshot. An approved file
/// renamed since the baseline finds its baseline under its earlier name through the rename log.
/// Package mode compares every approval with its baseline and never rewrites one.
/// </remarks>
public static class Baseline
{
    public const string RelativeRoot = "tests/SqlOS.BehaviorLock/Baseline";
    private const string SuitePrefix = "tests/SqlOS.BehaviorLock/";

    private static readonly Lazy<RenameLog> LoadedRenames = new(
        () => RenameLog.Parse(File.ReadAllText(RepositoryPaths.Combine(RelativeRoot, "renames.txt"))));

    private static readonly Lazy<string> LoadedRelease = new(
        () => ReadRelease(File.ReadAllText(RepositoryPaths.Combine(RelativeRoot, "release.txt"))));

    /// <summary>The SqlOS release the baseline records (<c>release.txt</c>).</summary>
    public static string Release => LoadedRelease.Value;

    /// <summary>The approved files renamed since the baseline (<c>renames.txt</c>).</summary>
    public static RenameLog Renames => LoadedRenames.Value;

    /// <summary>The baseline file of an approved file, given by an absolute or a repository-relative path.</summary>
    public static string FileFor(string approvedFile)
    {
        var relative = Path.IsPathRooted(approvedFile) ? RepositoryPaths.Relative(approvedFile) : approvedFile;
        return RepositoryPaths.Combine(RelativeRoot, PathUnderBaseline(Renames.OriginalOf(relative)));
    }

    /// <summary>Where the baseline of the repository-relative <paramref name="approvedPath"/> sits under <c>Baseline/</c>.</summary>
    public static string PathUnderBaseline(string approvedPath)
        => approvedPath.StartsWith(SuitePrefix, StringComparison.Ordinal) ? approvedPath[SuitePrefix.Length..] : approvedPath;

    /// <summary>
    /// <paramref name="transcript"/> without its labels: the scenario name and the step captions,
    /// which may change after the baseline (a renamed scenario, a clearer caption) while the
    /// behavior stays. Everything else is kept, notes included, because many notes record observed
    /// values.
    /// </summary>
    public static string WithoutLabels(string transcript)
    {
        var lines = transcript.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("scenario: ", StringComparison.Ordinal))
            {
                lines[index] = "scenario:";
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal) && line.IndexOf(": ", StringComparison.Ordinal) is > 0 and var caption)
            {
                lines[index] = line[..caption];
            }
        }

        return string.Join('\n', lines);
    }

    internal static string ReadRelease(string text)
    {
        var releases = text.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("release ", StringComparison.Ordinal))
            .Select(line => line["release ".Length..].Trim())
            .ToList();
        return releases is [var release] && release.Length > 0
            ? release
            : throw new FormatException($"{RelativeRoot}/release.txt must name exactly one release.");
    }
}

/// <summary>
/// <c>Baseline/renames.txt</c>: the approved files renamed since the baseline, one
/// <c>earlier path -&gt; later path</c> line per rename, appended and never edited, so a file renamed
/// twice is a chain of two lines.
/// </summary>
public sealed class RenameLog
{
    private readonly Dictionary<string, string> _laterOf;
    private readonly Dictionary<string, string> _earlierOf;

    private RenameLog(IReadOnlyList<(string Earlier, string Later)> renames)
    {
        Renames = renames;
        _laterOf = new Dictionary<string, string>(StringComparer.Ordinal);
        _earlierOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (earlier, later) in renames)
        {
            if (earlier == later || !_laterOf.TryAdd(earlier, later))
            {
                throw new FormatException($"renames.txt renames {earlier} more than once, or to itself.");
            }

            if (!_earlierOf.TryAdd(later, earlier))
            {
                throw new FormatException($"renames.txt renames two files to {later}.");
            }
        }

        foreach (var (earlier, _) in renames)
        {
            var path = earlier;
            for (var steps = 0; _laterOf.TryGetValue(path, out var later); steps++)
            {
                if (steps == renames.Count)
                {
                    throw new FormatException($"renames.txt renames {earlier} in a cycle.");
                }

                path = later;
            }
        }
    }

    public IReadOnlyList<(string Earlier, string Later)> Renames { get; }

    public static RenameLog Parse(string text)
    {
        var renames = new List<(string Earlier, string Later)>();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var paths = line.Split(" -> ");
            if (paths is not [var earlier, var later] || earlier.Length == 0 || later.Length == 0 || line.Count(char.IsWhiteSpace) != 2)
            {
                throw new FormatException($"Expected '<earlier path> -> <later path>' in renames.txt, got: {line}");
            }

            renames.Add((earlier, later));
        }

        return new RenameLog(renames);
    }

    /// <summary>The name <paramref name="approvedPath"/> had at the baseline.</summary>
    public string OriginalOf(string approvedPath)
    {
        var path = approvedPath;
        while (_earlierOf.TryGetValue(path, out var earlier))
        {
            path = earlier;
        }

        return path;
    }

    /// <summary>The name the file called <paramref name="path"/> carries today.</summary>
    public string CurrentOf(string path)
    {
        while (_laterOf.TryGetValue(path, out var later))
        {
            path = later;
        }

        return path;
    }
}
