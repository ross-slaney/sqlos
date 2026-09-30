using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Diagnostics;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Infrastructure.Scenarios;

/// <summary>
/// Proves a scenario's <c>[Covers]</c> claims: each declared route must have served at least one
/// observed exchange. Endpoint-routed requests match on method and route pattern; requests the
/// dashboard middleware answers by string matching (no endpoint) match the declared template
/// against the raw path.
/// </summary>
internal static class CoverageVerification
{
    public static void Verify(ScenarioContext scenario, IReadOnlyList<TranscriptEntry> entries)
    {
        if (scenario.Covers.Count == 0)
        {
            return;
        }

        var hits = entries
            .Where(entry => entry.Exchange != null)
            .SelectMany(entry => entry.Exchange!.RouteHits)
            .ToList();
        var missing = scenario.Covers
            .Where(cover => !hits.Any(hit => Matches(cover, hit)))
            .Select(cover => cover.Route)
            .ToList();
        if (missing.Count > 0)
        {
            throw new AssertFailedException(
                $"{scenario.Name} declares [Covers] routes that no observed exchange hit:\n  " +
                string.Join("\n  ", missing) +
                "\nObserved routes:\n  " +
                string.Join("\n  ", hits.Select(Describe).Distinct(StringComparer.Ordinal)));
        }
    }

    public static bool Matches(CoversAttribute cover, RouteHit hit)
    {
        if (!string.Equals(cover.Method, hit.Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return hit.RoutePattern != null
            ? string.Equals(cover.Template, hit.RoutePattern, StringComparison.Ordinal)
            : TemplatePattern(cover.Template).IsMatch(hit.Path);
    }

    /// <summary>Turns <c>/a/{id}/b</c> into an anchored path regex; <c>{*rest}</c> matches the remainder.</summary>
    public static Regex TemplatePattern(string template)
    {
        var pattern = Regex.Replace(
            Regex.Escape(template),
            @"\\\{\\\*[^}]+}",
            ".*");
        pattern = Regex.Replace(pattern, @"\\\{[^}]+}", "[^/]+");
        return new Regex("^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string Describe(RouteHit hit)
        => hit.RoutePattern != null ? $"{hit.Method} {hit.RoutePattern}" : $"{hit.Method} {hit.Path} (no endpoint)";
}
