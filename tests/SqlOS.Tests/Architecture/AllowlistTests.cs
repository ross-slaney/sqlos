using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.Tests.Architecture;

/// <summary>
/// The allowlists may only shrink. Each list's size must equal its committed high-water mark: a
/// list that grows fails until someone raises the mark, a visible one-line change a reviewer must
/// accept, and a list that shrank fails until the mark is lowered with it, so it cannot grow back.
/// </summary>
/// <remarks>
/// A committed mark is used instead of comparing with <c>git show origin/main:&lt;file&gt;</c>:
/// nothing merges to <c>main</c> while the 8.0.0 stack is built, so <c>main</c> has no allowlists to
/// compare with, and the unit-test job checks out a shallow clone without the base history. The
/// mark works offline, in any clone, and on every pull request of the stack.
/// </remarks>
[TestClass]
public sealed class AllowlistTests
{
    [TestMethod]
    public void Every_allowlist_is_sorted_without_duplicates()
    {
        foreach (var file in AllowlistFiles().Append(Allowlist.ProofProducersFile).Append(Allowlist.AggregateMembersFile))
        {
            var lines = Allowlist.Read(file);

            lines.Should().Equal(lines.Order(StringComparer.Ordinal), $"{file} is sorted ordinally");
            lines.Should().OnlyHaveUniqueItems($"{file} lists each line once");
            lines.Where(line => line != line.Trim()).Should().BeEmpty($"{file} has no padded lines");
        }
    }

    [TestMethod]
    public void No_allowlist_grows_past_its_high_water_mark()
    {
        var marks = ReadMarks();

        marks.Keys.Should().BeEquivalentTo(AllowlistFiles(), $"every allowlist has exactly one mark in {Allowlist.HighWaterMarksFile}");
        foreach (var (file, mark) in marks)
        {
            var count = Allowlist.Read(file).Count;
            count.Should().BeLessThanOrEqualTo(mark, $"{file} grew from its high-water mark of {mark}; fix the new violations instead of listing them");
            count.Should().Be(mark, $"{file} shrank to {count}; lower its mark in {Allowlist.HighWaterMarksFile} from {mark} to {count} so it cannot grow back");
        }
    }

    private static IEnumerable<string> AllowlistFiles()
        => Directory.GetFiles(Allowlist.Directory, "*.txt")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => file is not (Allowlist.HighWaterMarksFile or Allowlist.ProofProducersFile or Allowlist.AggregateMembersFile))
            .Order(StringComparer.Ordinal);

    private static Dictionary<string, int> ReadMarks()
        => Allowlist.Read(Allowlist.HighWaterMarksFile)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
}
