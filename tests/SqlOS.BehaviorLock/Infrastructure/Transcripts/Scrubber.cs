using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Turns a rendered transcript into deterministic text. It is the single authority for
/// scrubbing: nothing else in the pipeline replaces values.
/// <para>
/// Values become stable per-transcript placeholders: the same value always maps to the same
/// placeholder, and placeholders are numbered per kind in order of first appearance, such as
/// <c>{usr#1}</c> and <c>{code#2}</c>. Two sources feed it:
/// </para>
/// <list type="number">
/// <item><b>Registered values.</b> The renderer registers values whose role it knows (an
/// authorization code from a redirect, a refresh token from a token response, a CSRF field, a
/// harness-generated email) with a kind, or with a fixed name such as <c>{email:alice}</c>. Every
/// occurrence is replaced, including URL-encoded occurrences.</item>
/// <item><b>Detected values.</b> Patterns catch what nobody registered: JWTs, SqlOS prefixed IDs
/// (<c>usr_…</c>, <c>org_…</c>, derived from the ID generators in the source), GUIDs, long hex
/// digests, and high-entropy base64url tokens.</item>
/// </list>
/// Timestamps are scrubbed by format class rather than value, so a change of format stays visible:
/// <c>{datetime:utc-z}</c> (ISO 8601 with <c>Z</c>), <c>{datetime:offset}</c> (ISO 8601 with a
/// numeric offset), <c>{datetime:unspecified}</c> (ISO 8601 without a zone), and
/// <c>{datetime:rfc1123}</c>; date-only values become <c>{date}</c>. Existing <c>{…}</c>
/// placeholders are never rescrubbed.
/// </summary>
public sealed partial class Scrubber
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private Regex? _registeredPattern;

    /// <summary>Registers a value whose role is known, numbered in order of appearance: <c>{kind#n}</c>.</summary>
    public void Register(string? value, string kind)
        => RegisterCore(value, kind, name: null);

    /// <summary>Registers a value with a fixed, readable placeholder: <c>{kind:name}</c>.</summary>
    public void RegisterNamed(string? value, string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RegisterCore(value, kind, name);
    }

    /// <summary>Whether <paramref name="value"/> has been registered.</summary>
    public bool IsRegistered(string value) => _registrations.ContainsKey(value);

    /// <summary>
    /// Scrubs <paramref name="text"/> in one left-to-right pass. Placeholder numbering continues
    /// across calls, so callers scrub a transcript once, in reading order.
    /// </summary>
    public string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var registered = _registeredPattern ??= BuildRegisteredPattern();
        var afterRegistered = registered == null
            ? text
            : registered.Replace(text, match => Placeholder(_registrations[match.Value]));
        return DetectedPattern().Replace(afterRegistered, ReplaceDetected);
    }

    /// <summary>The placeholder a value maps to, allocating one if needed.</summary>
    public string PlaceholderFor(string value, string kind)
        => Placeholder(new Registration(value, kind, Name: null));

    private void RegisterCore(string? value, string kind, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (string.IsNullOrEmpty(value) || value.Length < MinimumRegisteredLength(kind) || IsPlaceholder(value))
        {
            return;
        }

        var registration = new Registration(value, kind, name);
        var canonical = JwtRendering.LooksLikeJwt(value) ? JwtRendering.Identity(value) : value;
        foreach (var variant in Variants(value))
        {
            // First registration wins: a value keeps the role it was first seen in.
            if (_registrations.TryAdd(variant, registration with { Canonical = canonical }))
            {
                _registeredPattern = null;
            }
        }
    }

    private static int MinimumRegisteredLength(string kind)
        => kind switch
        {
            // Short registered values (for example six-digit OTP codes) are only safe when their
            // kind says exactly what they are.
            "otp" or "user-code" or "sms-code" => 4,
            _ => 6
        };

    private static IEnumerable<string> Variants(string value)
    {
        yield return value;
        var escaped = Uri.EscapeDataString(value);
        if (!string.Equals(escaped, value, StringComparison.Ordinal))
        {
            yield return escaped;
        }

        var formEncoded = WebUtility.UrlEncode(value);
        if (!string.Equals(formEncoded, value, StringComparison.Ordinal)
            && !string.Equals(formEncoded, escaped, StringComparison.Ordinal))
        {
            yield return formEncoded;
        }

        var htmlEncoded = WebUtility.HtmlEncode(value);
        if (!string.Equals(htmlEncoded, value, StringComparison.Ordinal))
        {
            yield return htmlEncoded;
        }
    }

    private Regex? BuildRegisteredPattern()
    {
        if (_registrations.Count == 0)
        {
            return null;
        }

        // Longest first, so a value never matches inside a longer registered value. A registered
        // value must not continue a longer alphanumeric run on either side, but may sit next to
        // punctuation, including the '-' and '_' that join it into larger identifiers such as
        // 'nonce-{value}' in a CSP header or a cookie name suffix.
        var alternatives = _registrations.Keys
            .OrderByDescending(key => key.Length)
            .ThenBy(key => key, StringComparer.Ordinal)
            .Select(Regex.Escape);
        return new Regex(
            "(?<![A-Za-z0-9])(?:" + string.Join('|', alternatives) + ")(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant);
    }

    private string Placeholder(Registration registration)
    {
        var canonical = registration.Canonical ?? registration.Value;
        if (_placeholders.TryGetValue(canonical, out var existing))
        {
            return existing;
        }

        string placeholder;
        if (registration.Name != null)
        {
            placeholder = $"{{{registration.Kind}:{registration.Name}}}";
        }
        else
        {
            var next = _counters.GetValueOrDefault(registration.Kind) + 1;
            _counters[registration.Kind] = next;
            placeholder = $"{{{registration.Kind}#{next.ToString(CultureInfo.InvariantCulture)}}}";
        }

        _placeholders[canonical] = placeholder;
        return placeholder;
    }

    private string ReplaceDetected(Match match)
    {
        if (match.Groups["placeholder"].Success)
        {
            return match.Value;
        }

        if (match.Groups["jwt"].Success)
        {
            return Placeholder(new Registration(match.Value, "jwt", null) { Canonical = JwtRendering.Identity(match.Value) });
        }

        if (match.Groups["sqlosid"].Success)
        {
            return Placeholder(new Registration(match.Value, match.Groups["prefix"].Value, null));
        }

        if (match.Groups["guid"].Success)
        {
            return Placeholder(new Registration(match.Value, "guid", null));
        }

        if (match.Groups["isodate"].Success)
        {
            var zone = match.Groups["zone"].Value;
            return zone switch
            {
                "" => "{datetime:unspecified}",
                "Z" or "z" => "{datetime:utc-z}",
                _ => "{datetime:offset}"
            };
        }

        if (match.Groups["rfc1123"].Success)
        {
            return "{datetime:rfc1123}";
        }

        if (match.Groups["date"].Success)
        {
            return "{date}";
        }

        if (match.Groups["compound"].Success)
        {
            // A readable identifier with a random suffix, such as a derived cookie name:
            // keep the words, scrub the suffix.
            return match.Groups["words"].Value + Placeholder(new Registration(match.Groups["suffix"].Value, "hex", null));
        }

        if (match.Groups["hex"].Success)
        {
            return IsHighEntropyHex(match.Value)
                ? Placeholder(new Registration(match.Value, "hex", null))
                : match.Value;
        }

        if (match.Groups["token"].Success)
        {
            return IsHighEntropyToken(match.Value)
                ? Placeholder(new Registration(match.Value, "token", null))
                : match.Value;
        }

        return match.Value;
    }

    private static bool IsHighEntropyHex(string value)
        => value.Any(char.IsDigit) && value.Any(character => character is >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// A random base64url token: long enough, with digits and letters, and either mixed case or
    /// a <c>-</c>/<c>_</c>. Identifiers such as <c>sqlos-auth-page-primary</c> (no digits) and
    /// words stay readable.
    /// </summary>
    internal static bool IsHighEntropyToken(string value)
    {
        if (value.Length < 20)
        {
            return false;
        }

        var digits = value.Count(char.IsDigit);
        var upper = value.Count(char.IsUpper);
        var lower = value.Count(char.IsLower);
        if (digits == 0 || upper + lower == 0)
        {
            return false;
        }

        // Separator-joined words with a version or count ("sqlos-dashboard-v2") are not random:
        // every segment of a random token is long.
        var segments = value.Split('-', '_');
        if (segments.Length > 1 && segments.All(segment => segment.Length <= 12) && upper == 0)
        {
            return false;
        }

        return (upper > 0 && lower > 0) || digits >= 4;
    }

    private static bool IsPlaceholder(string value)
        => value.Length > 2 && value[0] == '{' && value[^1] == '}';

    // Order matters: the first alternative that matches at a position wins.
    [GeneratedRegex(
        """
        (?<placeholder>\{[a-z][a-z0-9\-]*(?:[#:][^{}\s]+)?\})
        |(?<jwt>\beyJ[A-Za-z0-9_\-]{4,}\.eyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]*)
        |(?<sqlosid>\b(?<prefix>[a-z][a-z0-9]{0,11})_[0-9a-f]{16,32}\b)
        |(?<guid>\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b)
        |(?<isodate>\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,9})?)?(?<zone>Z|z|[+\-]\d{2}:?\d{2})?(?![\d:.]))
        |(?<rfc1123>\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun),\x20\d{2}\x20(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\x20\d{4}\x20\d{2}:\d{2}:\d{2}\x20GMT\b)
        |(?<date>(?<!\d)\d{4}-\d{2}-\d{2}(?![\d\-T:]))
        |(?<compound>(?<![A-Za-z0-9_\-])(?<words>(?:[a-z]+[_\-])+)(?<suffix>[0-9a-f]{16,})(?![A-Za-z0-9_\-]))
        |(?<hex>(?<![A-Za-z0-9\-])(?:[0-9a-f]{16,}|[0-9A-F]{16,})(?![A-Za-z0-9_\-]))
        |(?<token>(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{20,}(?![A-Za-z0-9_\-]))
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex DetectedPattern();

    private sealed record Registration(string Value, string Kind, string? Name)
    {
        public string? Canonical { get; init; }
    }
}
