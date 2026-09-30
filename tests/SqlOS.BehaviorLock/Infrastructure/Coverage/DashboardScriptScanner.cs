using System.Text;
using System.Text.RegularExpressions;

namespace SqlOS.BehaviorLock.Infrastructure.Coverage;

/// <summary>A path a dashboard script calls, with where it was found.</summary>
/// <param name="Path">The path with dynamic segments as <c>{}</c>, query string removed.</param>
/// <param name="Source">The script and line.</param>
public sealed record ScriptPath(string Path, string Source);

/// <summary>
/// Finds the server paths the embedded dashboards call, so the coverage gate can prove each one
/// is a mapped endpoint or a manifest route.
/// <list type="bullet">
/// <item><c>src/SqlOS/Dashboard/wwwroot/app.js</c> builds every URL from base-path constants
/// (<c>const authApiBasePath = `${authDashboardPath}/api`</c>); the scanner resolves those
/// constants and reads every template literal that starts with one.</item>
/// <item><c>src/SqlOS/Fga/Dashboard/wwwroot/app.js</c> calls <c>api('…')</c>, <c>apiPost</c>,
/// <c>apiDelete</c>, <c>fetch(`${basePath}/api/…`)</c>, and remote pickers configured with
/// <c>endpoint: '…'</c>, all under <c>/sqlos/admin/fga/api/</c>.</item>
/// </list>
/// Only server paths count (API, operator session, and auth-server paths), not the SPA's own
/// navigation routes. Interpolations become <c>{}</c>; a ternary between string literals expands
/// into both paths.
/// </summary>
public static partial class DashboardScriptScanner
{
    public const string DashboardBasePath = "/sqlos";
    public const string FgaBasePath = "/sqlos/admin/fga";

    public static string RootScript => RepositoryPaths.Combine("src", "SqlOS", "Dashboard", "wwwroot", "app.js");

    public static string FgaScript => RepositoryPaths.Combine("src", "SqlOS", "Fga", "Dashboard", "wwwroot", "app.js");

    public static IReadOnlyList<ScriptPath> ScanAll()
        => ScanRootDashboard(File.ReadAllText(RootScript), "src/SqlOS/Dashboard/wwwroot/app.js")
            .Concat(ScanFgaDashboard(File.ReadAllText(FgaScript), "src/SqlOS/Fga/Dashboard/wwwroot/app.js"))
            .DistinctBy(path => (path.Path, path.Source))
            .ToList();

    public static IReadOnlyList<ScriptPath> ScanRootDashboard(string script, string sourceName)
    {
        var constants = ResolveConstants(script);
        if (!constants.ContainsKey("authApiBasePath"))
        {
            throw new InvalidOperationException(
                $"{sourceName} no longer defines authApiBasePath from dashboardBasePath; update DashboardScriptScanner to match how it builds URLs.");
        }

        var paths = new List<ScriptPath>();
        foreach (var (literal, line) in TemplateLiterals(script))
        {
            var head = LeadingInterpolation().Match(literal);
            if (!head.Success || !constants.TryGetValue(head.Groups["name"].Value, out var basePath))
            {
                continue;
            }

            foreach (var path in Expand(basePath + literal[head.Length..]))
            {
                // The base-path definitions are template literals too; they are not calls.
                if (IsServerPath(path) && !constants.ContainsValue(path))
                {
                    paths.Add(new ScriptPath(path, $"{sourceName}:{line}"));
                }
            }
        }

        return paths;
    }

    public static IReadOnlyList<ScriptPath> ScanFgaDashboard(string script, string sourceName)
    {
        var paths = new List<ScriptPath>();
        foreach (Match call in FgaApiCall().Matches(script))
        {
            var argument = call.Groups["argument"].Value;
            var line = LineOf(script, call.Index);
            var literal = argument.Length > 1 && argument[0] is '\'' or '"' or '`' ? argument[1..^1] : null;
            if (literal == null)
            {
                continue;
            }

            if (literal.StartsWith("${config.endpoint}", StringComparison.Ordinal))
            {
                // Remote pickers: the endpoints are listed separately (endpoint: '...').
                continue;
            }

            foreach (var path in Expand($"{FgaBasePath}/api/{literal}"))
            {
                paths.Add(new ScriptPath(path, $"{sourceName}:{line}"));
            }
        }

        foreach (Match endpoint in FgaPickerEndpoint().Matches(script))
        {
            paths.Add(new ScriptPath($"{FgaBasePath}/api/{endpoint.Groups["endpoint"].Value}", $"{sourceName}:{LineOf(script, endpoint.Index)}"));
        }

        foreach (var (literal, line) in TemplateLiterals(script))
        {
            if (literal.StartsWith("${basePath}/api/", StringComparison.Ordinal) && !literal.StartsWith("${basePath}/api/${endpoint}", StringComparison.Ordinal))
            {
                foreach (var path in Expand(FgaBasePath + literal["${basePath}".Length..]))
                {
                    paths.Add(new ScriptPath(path, $"{sourceName}:{line}"));
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// Matches a script path against route templates segment by segment: <c>{}</c> in the path and
    /// <c>{name}</c> in the template match any one segment; <c>{*name}</c> matches the rest.
    /// </summary>
    public static bool Matches(string path, string template)
    {
        var pathSegments = path.Trim('/').Split('/');
        var templateSegments = template.Trim('/').Split('/');
        for (var index = 0; index < templateSegments.Length; index++)
        {
            var segment = templateSegments[index];
            if (segment.StartsWith("{*", StringComparison.Ordinal))
            {
                return pathSegments.Length >= index;
            }

            if (index >= pathSegments.Length)
            {
                return false;
            }

            var candidate = pathSegments[index];
            if (IsParameter(segment) || candidate.Contains("{}", StringComparison.Ordinal))
            {
                if (candidate.Contains("{}", StringComparison.Ordinal) && !IsParameter(segment))
                {
                    // A partly dynamic segment such as "${action}" or "${id}.xml" matches a literal
                    // segment only when the literal fits its fixed parts.
                    var pattern = "^" + Regex.Escape(candidate).Replace(@"\{}", ".+", StringComparison.Ordinal) + "$";
                    if (!Regex.IsMatch(segment, pattern))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (!string.Equals(segment, candidate, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return pathSegments.Length == templateSegments.Length;
    }

    private static bool IsParameter(string segment)
        => segment.StartsWith('{') && segment.EndsWith('}') && !segment.StartsWith("{*", StringComparison.Ordinal);

    private static bool IsServerPath(string path)
        => path.Contains("/api/", StringComparison.Ordinal)
           || path.EndsWith("/api", StringComparison.Ordinal)
           || path.StartsWith($"{DashboardBasePath}/dashboard-auth/", StringComparison.Ordinal)
           || path.StartsWith($"{DashboardBasePath}/auth/", StringComparison.Ordinal);

    /// <summary>Resolves <c>const name = `${other}/suffix`</c> chains rooted at the dashboard base path.</summary>
    private static Dictionary<string, string> ResolveConstants(string script)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal) { ["dashboardBasePath"] = DashboardBasePath };
        // Function-local constants can reuse a name; the base paths are the file's first definitions.
        var definitions = ConstantDefinition().Matches(script)
            .DistinctBy(match => match.Groups["name"].Value)
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
        bool progressed;
        do
        {
            progressed = false;
            foreach (var (name, value) in definitions)
            {
                if (resolved.ContainsKey(name))
                {
                    continue;
                }

                var head = LeadingInterpolation().Match(value);
                if (head.Success && resolved.TryGetValue(head.Groups["name"].Value, out var basePath) && !value[head.Length..].Contains("${", StringComparison.Ordinal))
                {
                    resolved[name] = basePath + value[head.Length..];
                    progressed = true;
                }
            }
        }
        while (progressed);

        return resolved;
    }

    /// <summary>Expands interpolations: string-literal ternaries into each branch, anything else into <c>{}</c>.</summary>
    private static IEnumerable<string> Expand(string template)
    {
        var withoutQuery = CutQuery(template);
        var results = new List<string> { string.Empty };
        var index = 0;
        while (index < withoutQuery.Length)
        {
            var start = withoutQuery.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                results = results.Select(prefix => prefix + withoutQuery[index..]).ToList();
                break;
            }

            var end = MatchingBrace(withoutQuery, start + 1);
            var expression = withoutQuery[(start + 2)..end];
            var literal = withoutQuery[index..start];
            var ternary = StringTernary().Match(expression);
            var alternatives = ternary.Success
                ? new[] { ternary.Groups["a"].Value, ternary.Groups["b"].Value }
                : ["{}"];
            results = results.SelectMany(prefix => alternatives.Select(alternative => prefix + literal + alternative)).ToList();
            index = end + 1;
        }

        return results.Select(path => path.Length > 1 ? path.TrimEnd('/') : path);
    }

    private static string CutQuery(string template)
    {
        var depth = 0;
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '$' && index + 1 < template.Length && template[index + 1] == '{')
            {
                depth++;
                index++;
            }
            else if (template[index] == '}' && depth > 0)
            {
                depth--;
            }
            else if ((template[index] == '?' || template[index] == '#') && depth == 0)
            {
                return template[..index];
            }
        }

        return template;
    }

    private static int MatchingBrace(string text, int openBrace)
    {
        var depth = 0;
        for (var index = openBrace; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}' && --depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Unbalanced interpolation in '{text}'.");
    }

    /// <summary>Top-level template literals with their line numbers, skipping strings and comments.</summary>
    private static IEnumerable<(string Literal, int Line)> TemplateLiterals(string script)
    {
        var index = 0;
        while (index < script.Length)
        {
            var character = script[index];
            if (character == '/' && index + 1 < script.Length && script[index + 1] == '/')
            {
                index = script.IndexOf('\n', index) is var newline && newline < 0 ? script.Length : newline;
                continue;
            }

            if (character == '/' && index + 1 < script.Length && script[index + 1] == '*')
            {
                var close = script.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = close < 0 ? script.Length : close + 2;
                continue;
            }

            if (character is '\'' or '"')
            {
                index = SkipQuoted(script, index);
                continue;
            }

            if (character == '`')
            {
                var end = SkipTemplate(script, index);
                yield return (script[(index + 1)..(end - 1)], LineOf(script, index));
                index = end;
                continue;
            }

            index++;
        }
    }

    private static int SkipQuoted(string script, int start)
    {
        var quote = script[start];
        for (var index = start + 1; index < script.Length; index++)
        {
            if (script[index] == '\\')
            {
                index++;
            }
            else if (script[index] == quote || script[index] == '\n')
            {
                return index + 1;
            }
        }

        return script.Length;
    }

    private static int SkipTemplate(string script, int start)
    {
        for (var index = start + 1; index < script.Length; index++)
        {
            if (script[index] == '\\')
            {
                index++;
            }
            else if (script[index] == '`')
            {
                return index + 1;
            }
            else if (script[index] == '$' && index + 1 < script.Length && script[index + 1] == '{')
            {
                var depth = 0;
                for (index++; index < script.Length; index++)
                {
                    if (script[index] == '`')
                    {
                        index = SkipTemplate(script, index) - 1;
                    }
                    else if (script[index] is '\'' or '"')
                    {
                        index = SkipQuoted(script, index) - 1;
                    }
                    else if (script[index] == '{')
                    {
                        depth++;
                    }
                    else if (script[index] == '}' && --depth == 0)
                    {
                        break;
                    }
                }
            }
        }

        return script.Length;
    }

    private static int LineOf(string script, int index) => script.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(@"^\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex LeadingInterpolation();

    [GeneratedRegex(@"const\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*`(?<value>[^`]*)`")]
    private static partial Regex ConstantDefinition();

    [GeneratedRegex(@"^[^?]+\?\s*(?<q>['""])(?<a>[^'""]*)\k<q>\s*:\s*(?<r>['""])(?<b>[^'""]*)\k<r>\s*$")]
    private static partial Regex StringTernary();

    [GeneratedRegex(@"\b(?:api|apiPost|apiPut|apiDelete)\(\s*(?<argument>'[^']*'|""[^""]*""|`(?:[^`\\]|\\.)*`)")]
    private static partial Regex FgaApiCall();

    [GeneratedRegex(@"endpoint:\s*'(?<endpoint>[^']+)'")]
    private static partial Regex FgaPickerEndpoint();
}
