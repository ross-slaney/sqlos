using System.Text;

namespace SqlOS.BehaviorLock.Infrastructure;

/// <summary>A small unified line diff for failure messages (longest common subsequence, three lines of context).</summary>
public static class LineDiff
{
    private const int Context = 3;
    private const int MaxOutputLines = 120;

    public static string Unified(string expected, string actual, string expectedLabel, string actualLabel)
    {
        var left = expected.Split('\n');
        var right = actual.Split('\n');
        var operations = Diff(left, right);
        var builder = new StringBuilder();
        builder.Append("--- ").Append(expectedLabel).Append('\n');
        builder.Append("+++ ").Append(actualLabel).Append('\n');

        var changed = operations.Select((operation, index) => (operation, index))
            .Where(item => item.operation.Kind != ' ')
            .Select(item => item.index)
            .ToList();
        if (changed.Count == 0)
        {
            return builder.Append("(no line differences; check trailing whitespace or line endings)\n").ToString();
        }

        var written = 0;
        var hunkStart = -1;
        var hunkEnd = -1;
        foreach (var index in changed)
        {
            var start = Math.Max(0, index - Context);
            var end = Math.Min(operations.Count - 1, index + Context);
            if (hunkStart >= 0 && start <= hunkEnd + 1)
            {
                hunkEnd = end;
                continue;
            }

            if (hunkStart >= 0)
            {
                written += WriteHunk(builder, operations, hunkStart, hunkEnd);
            }

            hunkStart = start;
            hunkEnd = end;
            if (written > MaxOutputLines)
            {
                break;
            }
        }

        if (hunkStart >= 0 && written <= MaxOutputLines)
        {
            WriteHunk(builder, operations, hunkStart, hunkEnd);
        }

        if (written > MaxOutputLines)
        {
            builder.Append("… (diff truncated; compare the received and approved files)\n");
        }

        return builder.ToString();
    }

    private static int WriteHunk(StringBuilder builder, IReadOnlyList<(char Kind, string Line, int Left, int Right)> operations, int start, int end)
    {
        var first = operations[start];
        builder.Append("@@ -").Append(first.Left + 1).Append(" +").Append(first.Right + 1).Append(" @@\n");
        for (var index = start; index <= end; index++)
        {
            builder.Append(operations[index].Kind).Append(operations[index].Line).Append('\n');
        }

        return end - start + 2;
    }

    private static List<(char Kind, string Line, int Left, int Right)> Diff(string[] left, string[] right)
    {
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var result = new List<(char, string, int, int)>();
        int l = 0, r = 0;
        while (l < left.Length && r < right.Length)
        {
            if (string.Equals(left[l], right[r], StringComparison.Ordinal))
            {
                result.Add((' ', left[l], l, r));
                l++;
                r++;
            }
            else if (lengths[l + 1, r] >= lengths[l, r + 1])
            {
                result.Add(('-', left[l], l, r));
                l++;
            }
            else
            {
                result.Add(('+', right[r], l, r));
                r++;
            }
        }

        for (; l < left.Length; l++)
        {
            result.Add(('-', left[l], l, r));
        }

        for (; r < right.Length; r++)
        {
            result.Add(('+', right[r], l, r));
        }

        return result;
    }
}
