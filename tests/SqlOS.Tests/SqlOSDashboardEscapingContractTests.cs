using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.Tests;

/// <summary>
/// The dashboard renders tenant data (signup emails, organization names, IdP and SCIM values) with
/// HTML templates. The shared list helpers must escape every value they interpolate unless the
/// parameter is HTML by contract (#425).
/// </summary>
[TestClass]
public sealed class SqlOSDashboardEscapingContractTests
{
    private static readonly string[] ListHelpers =
    [
        "renderListRow",
        "renderListRows",
        "renderList",
        "renderListToolbar",
        "renderChip",
        "renderIdChip",
        "renderClientBadge",
        "renderMetadataRows"
    ];

    /// <summary>Parameters and locals that carry HTML or markup the helper itself built.</summary>
    private static readonly HashSet<string> HtmlTypedOperands = new(StringComparer.Ordinal)
    {
        "metaHtml",
        "actionsHtml",
        "pagerHtml",
        "row.html",
        "hrefAttr",
        "tag",
        "cls",
        "formatter(item)",
        "items.map(rowFn).join(STR)"
    };

    private static readonly Regex MapToTemplate = new(
        @"^[\w.]+(\s*\.filter\(.*\))?\s*\.map\(\s*\w+\s*=>\s*TPL\s*\)\s*\.join\(\s*STR\s*\)$",
        RegexOptions.Singleline);

    [TestMethod]
    public void DashboardListHelpers_EscapeEveryInterpolatedValue()
    {
        var source = ReadSource("src", "SqlOS", "Dashboard", "wwwroot", "app.js");
        var unsafeInterpolations = new List<string>();

        foreach (var helper in ListHelpers)
        {
            var body = ExtractFunctionBody(source, helper);
            var interpolations = TemplateScanner.Interpolations(body);
            interpolations.Should().NotBeEmpty("{0} renders HTML through template literals", helper);
            unsafeInterpolations.AddRange(interpolations
                .Where(expression => !IsSafe(expression))
                .Select(expression => $"{helper}: ${{{expression}}}"));
        }

        unsafeInterpolations.Should().BeEmpty(
            "tenant-controlled values must go through esc() unless the parameter is HTML-typed");
    }

    [TestMethod]
    public void ContractCheck_RejectsTheUnescapedSubtitle()
    {
        // The helper as it shipped before #425: the subtitle was interpolated raw.
        const string vulnerable = """
            function renderListRow({ href, title, subtitle, metaHtml, actionsHtml }) {
                return `<div class="list-row-title">${esc(title)}</div>
                    ${subtitle ? `<div class="list-row-sub">${subtitle}</div>` : ""}`;
            }
            """;

        var interpolations = TemplateScanner.Interpolations(ExtractFunctionBody(vulnerable, "renderListRow"));

        interpolations.Where(expression => !IsSafe(expression)).Should().Equal("subtitle");
    }

    [TestMethod]
    public void FgaDashboardEscape_EscapesQuotesAndKeepsFalsyValues()
    {
        var body = ExtractFunctionBody(ReadSource("src", "SqlOS", "Fga", "Dashboard", "wwwroot", "app.js"), "esc");

        body.Should().Contain("String(s ?? '')", "only null and undefined render as empty, so 0 renders as 0");
        body.Should().NotContain("if (!s)");
        body.Should().NotContain("innerHTML", "textContent/innerHTML does not escape quotes for attributes");
        foreach (var (character, entity) in new[] { ("&", "&amp;"), ("<", "&lt;"), (">", "&gt;"), ("\"", "&quot;"), ("'", "&#39;") })
        {
            body.Should().MatchRegex(
                $@"\.replaceAll\(\s*['""]{Regex.Escape(character)}['""]\s*,\s*['""]{Regex.Escape(entity)}['""]\s*\)",
                "esc must escape {0}", character);
        }

        body.IndexOf("'&amp;'", StringComparison.Ordinal).Should().BeLessThan(
            body.IndexOf("'&lt;'", StringComparison.Ordinal),
            "& must be escaped first so later entities are not double-escaped");
    }

    private static bool IsSafe(string expression)
    {
        var reduced = TemplateScanner.Reduce(expression).Trim();
        return IsSafeReduced(reduced);
    }

    private static bool IsSafeReduced(string expression)
    {
        expression = StripWrappingParentheses(expression.Trim());

        if (TrySplitTernary(expression, out var whenTrue, out var whenFalse))
        {
            // The condition is never rendered; only the branches are.
            return IsSafeReduced(whenTrue) && IsSafeReduced(whenFalse);
        }

        var operands = SplitTopLevel(expression, "||", "??");
        if (operands.Count > 1)
        {
            return operands.All(IsSafeReduced);
        }

        return expression is "TPL" or "STR"
            || IsWholeEscCall(expression)
            || HtmlTypedOperands.Contains(expression)
            || MapToTemplate.IsMatch(expression);
    }

    private static bool IsWholeEscCall(string expression)
    {
        if (!expression.StartsWith("esc(", StringComparison.Ordinal))
        {
            return false;
        }

        var depth = 0;
        for (var index = 3; index < expression.Length; index++)
        {
            depth += expression[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return index == expression.Length - 1;
            }
        }

        return false;
    }

    private static string StripWrappingParentheses(string expression)
    {
        while (expression.StartsWith('(') && expression.EndsWith(')') && ClosingParenthesis(expression, 0) == expression.Length - 1)
        {
            expression = expression[1..^1].Trim();
        }

        return expression;
    }

    private static int ClosingParenthesis(string expression, int open)
    {
        var depth = 0;
        for (var index = open; index < expression.Length; index++)
        {
            depth += expression[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TrySplitTernary(string expression, out string whenTrue, out string whenFalse)
    {
        whenTrue = whenFalse = string.Empty;
        var depth = 0;
        var question = -1;
        var nested = 0;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            if (character is '(' or '[' or '{')
            {
                depth++;
            }
            else if (character is ')' or ']' or '}')
            {
                depth--;
            }
            else if (depth == 0 && character == '?' && !IsNullishOperator(expression, index))
            {
                if (question < 0)
                {
                    question = index;
                }
                else
                {
                    nested++;
                }
            }
            else if (depth == 0 && character == ':' && question >= 0)
            {
                if (nested > 0)
                {
                    nested--;
                    continue;
                }

                whenTrue = expression[(question + 1)..index];
                whenFalse = expression[(index + 1)..];
                return true;
            }
        }

        return false;
    }

    private static bool IsNullishOperator(string expression, int index)
        => (index + 1 < expression.Length && expression[index + 1] is '?' or '.')
           || (index > 0 && expression[index - 1] == '?');

    private static List<string> SplitTopLevel(string expression, params string[] separators)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            if (character is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (character is ')' or ']' or '}')
            {
                depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            var separator = separators.FirstOrDefault(candidate =>
                string.CompareOrdinal(expression, index, candidate, 0, candidate.Length) == 0);
            if (separator != null)
            {
                parts.Add(expression[start..index]);
                index += separator.Length - 1;
                start = index + 1;
            }
        }

        parts.Add(expression[start..]);
        return parts;
    }

    private static string ExtractFunctionBody(string source, string name)
    {
        var match = Regex.Match(source, $@"\bfunction\s+{Regex.Escape(name)}\s*\(");
        match.Success.Should().BeTrue("function {0} must exist", name);
        var parametersEnd = ClosingParenthesis(source, match.Index + match.Length - 1);
        var open = source.IndexOf('{', parametersEnd);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[(open + 1)..index];
            }
        }

        throw new InvalidOperationException($"Function {name} has no closing brace.");
    }

    private static string ReadSource(params string[] relative)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. relative]));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlOS.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    /// <summary>
    /// A small JavaScript template-literal scanner: finds every <c>${...}</c> at any nesting depth
    /// and reduces an expression to its shape by replacing nested templates with <c>TPL</c> and
    /// quoted strings with <c>STR</c>.
    /// </summary>
    private static class TemplateScanner
    {
        public static List<string> Interpolations(string code)
        {
            var found = new List<string>();
            for (var index = 0; index < code.Length; index++)
            {
                var character = code[index];
                if (character is '"' or '\'')
                {
                    index = SkipString(code, index);
                }
                else if (character == '`')
                {
                    index = ScanTemplate(code, index, found);
                }
            }

            return found;
        }

        public static string Reduce(string expression)
        {
            var reduced = new StringBuilder();
            for (var index = 0; index < expression.Length; index++)
            {
                var character = expression[index];
                if (character is '"' or '\'')
                {
                    index = SkipString(expression, index);
                    reduced.Append("STR");
                }
                else if (character == '`')
                {
                    index = ScanTemplate(expression, index, []);
                    reduced.Append("TPL");
                }
                else
                {
                    reduced.Append(character);
                }
            }

            return reduced.ToString();
        }

        /// <summary>Scans a template starting at its opening backtick; returns its closing backtick.</summary>
        private static int ScanTemplate(string code, int open, List<string> found)
        {
            for (var index = open + 1; index < code.Length; index++)
            {
                var character = code[index];
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '`')
                {
                    return index;
                }
                else if (character == '$' && index + 1 < code.Length && code[index + 1] == '{')
                {
                    var start = index + 2;
                    var end = ScanExpression(code, start, found);
                    found.Add(code[start..end].Trim());
                    index = end;
                }
            }

            throw new InvalidOperationException("Unterminated template literal.");
        }

        /// <summary>Scans an interpolated expression; returns the index of its closing brace.</summary>
        private static int ScanExpression(string code, int start, List<string> found)
        {
            var depth = 0;
            for (var index = start; index < code.Length; index++)
            {
                var character = code[index];
                if (character is '"' or '\'')
                {
                    index = SkipString(code, index);
                }
                else if (character == '`')
                {
                    index = ScanTemplate(code, index, found);
                }
                else if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    if (depth == 0)
                    {
                        return index;
                    }

                    depth--;
                }
            }

            throw new InvalidOperationException("Unterminated template interpolation.");
        }

        private static int SkipString(string code, int open)
        {
            var quote = code[open];
            for (var index = open + 1; index < code.Length; index++)
            {
                if (code[index] == '\\')
                {
                    index++;
                }
                else if (code[index] == quote)
                {
                    return index;
                }
            }

            throw new InvalidOperationException("Unterminated string literal.");
        }
    }
}
