using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Renders JSON canonically: object members sorted by name (member order carries no meaning in
/// JSON, and sorting keeps refactors that reorder DTO properties from producing noise), arrays in
/// the order the API returned them, two-space indentation, and minimal string escaping. While
/// rendering it registers role-bearing values (see <see cref="ValueRoles"/>) with the scrubber and
/// collects JWTs for decoding. Integers in the Unix-epoch range render as <c>{epoch}</c>, and so do
/// the epoch claims a host echoes as <c>{ "type": "exp", "value": "1790800611" }</c> objects (ASP.NET
/// materializes every claim value as a string); the list's lifetimes (<c>exp-iat=10m</c>) are
/// reported to the sink so the transcript keeps them, as it does for a decoded JWT.
/// </summary>
public static partial class CanonicalJson
{
    /// <summary>
    /// Fields holding "seconds from now", computed from the clock during the request, so they can
    /// read 599 or 600 for a ten-minute lifetime. They render as <c>~600</c>.
    /// </summary>
    private static readonly HashSet<string> RemainingSecondsFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "expires_in", "expiresIn", "refresh_token_expires_in", "expiresInSeconds",
        "retry_after_seconds", "retryAfterSeconds", "retryAfter"
    };

    /// <summary>
    /// Arrays whose order SqlOS does not define, so the order is not behavior. Each entry names
    /// the source of the nondeterminism; the determinism check (BEHAVIOR_LOCK_REPEAT) finds new
    /// ones. These arrays render sorted. Never add an array whose order clients can rely on.
    /// </summary>
    private static readonly Dictionary<string, string> UnorderedArrayFields = new(StringComparer.Ordinal)
    {
        // SqlOSPasswordLoginAbuseService builds it from an EF Include with no ORDER BY.
        ["resetScopes"] = "password-login bucket scopes",
        // SqlOSPasswordLoginAbuseService.RecordFailureAsync lists the buckets a failure locked in the
        // order the same Include loaded them (password.login.failed audit metadata).
        ["lockedScopes"] = "password-login bucket scopes",
        // SqlOSAdminService.GetUserOrganizationsAsync selects memberships with no ORDER BY, so rows
        // come back in (random) organization-ID order. The headless view model's organization picker.
        ["organizationSelection"] = "a user's organizations"
    };

    /// <summary>JWT claims that hold a Unix time (RFC 7519 §4.1 and OpenID Connect Core §2).</summary>
    private static readonly HashSet<string> EpochClaims = new(StringComparer.Ordinal)
    {
        "iat", "nbf", "exp", "auth_time"
    };

    private static readonly JsonSerializerOptions StringEscaping = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Renders <paramref name="json"/>, or returns null when it is not valid JSON.</summary>
    public static string? TryRender(string json, TranscriptValueSink sink)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false });
            return Render(document.RootElement, sink);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Render(JsonElement element, TranscriptValueSink sink)
    {
        var builder = new StringBuilder();
        Write(builder, element, sink, indent: 0, propertyName: null);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonElement element, TranscriptValueSink sink, int indent, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToList();
                if (properties.Count == 0)
                {
                    builder.Append("{}");
                    return;
                }

                var echoedEpoch = EchoedEpochClaim(element, out _);
                builder.Append('{');
                for (var index = 0; index < properties.Count; index++)
                {
                    var property = properties[index];
                    builder.Append('\n').Append(' ', (indent + 1) * 2);
                    builder.Append(Quote(property.Name)).Append(": ");
                    if (echoedEpoch && property.Name == "value")
                    {
                        builder.Append(property.Value.ValueKind == JsonValueKind.String ? "\"{epoch}\"" : "{epoch}");
                    }
                    else
                    {
                        Write(builder, property.Value, sink, indent + 1, property.Name);
                    }

                    if (index < properties.Count - 1)
                    {
                        builder.Append(',');
                    }
                }

                builder.Append('\n').Append(' ', indent * 2).Append('}');
                return;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    builder.Append("[]");
                    return;
                }

                if (propertyName != null && UnorderedArrayFields.ContainsKey(propertyName))
                {
                    // Generated IDs are random per run, so they must not decide the order.
                    items = items.OrderBy(item => GeneratedId().Replace(item.GetRawText(), "id"), StringComparer.Ordinal).ToList();
                }

                ReportClaimLifetimes(items, sink);
                var elementKind = propertyName == null ? null : ValueRoles.ElementKindFor(propertyName);
                builder.Append('[');
                for (var index = 0; index < items.Count; index++)
                {
                    builder.Append('\n').Append(' ', (indent + 1) * 2);
                    if (elementKind != null && items[index].ValueKind == JsonValueKind.String)
                    {
                        sink.Register(items[index].GetString(), elementKind);
                    }

                    Write(builder, items[index], sink, indent + 1, propertyName: null);
                    if (index < items.Count - 1)
                    {
                        builder.Append(',');
                    }
                }

                builder.Append('\n').Append(' ', indent * 2).Append(']');
                return;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    // Same rule as HTML: data URIs can embed per-run secrets (a TOTP QR code).
                    builder.Append(Quote(DataUri(value)));
                    return;
                }

                if (propertyName != null)
                {
                    sink.RegisterRole(propertyName, value);
                }

                sink.Inspect(value);
                builder.Append(Quote(value));
                return;

            case JsonValueKind.Number:
                builder.Append(propertyName != null && RemainingSecondsFields.Contains(propertyName)
                    ? ApproximateSeconds(element)
                    : RenderNumber(element));
                return;

            default:
                builder.Append(element.GetRawText());
                return;
        }
    }

    /// <summary>
    /// Whether <paramref name="element"/> is a claim a host echoed as <c>{ "type", "value" }</c>
    /// whose type holds a Unix time and whose value, a string or a number, is one.
    /// </summary>
    private static bool EchoedEpochClaim(JsonElement element, out long seconds)
    {
        seconds = 0;
        if (!element.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !EpochClaims.Contains(type.GetString()!)
            || !element.TryGetProperty("value", out var value))
        {
            return false;
        }

        var parsed = value.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds),
            JsonValueKind.Number => value.TryGetInt64(out seconds),
            _ => false
        };
        return parsed && seconds is >= 1_000_000_000 and <= 9_999_999_999;
    }

    /// <summary>
    /// Reports the relative lifetimes of a list of echoed claims, such as the <c>claims</c> a
    /// resource route returns for its caller, the way a decoded JWT shows them.
    /// </summary>
    private static void ReportClaimLifetimes(IReadOnlyList<JsonElement> items, TranscriptValueSink sink)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object && EchoedEpochClaim(item, out var seconds))
            {
                values.TryAdd(item.GetProperty("type").GetString()!, seconds);
            }
        }

        if (values.Count > 0)
        {
            sink.AddClaimLifetimes(JwtRendering.DescribeLifetimes(values));
        }
    }

    /// <summary>Epoch-range integers are timestamps; everything else renders as sent.</summary>
    internal static string RenderNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            if (integer is >= 1_000_000_000 and <= 9_999_999_999)
            {
                return "{epoch}";
            }

            if (integer is >= 1_000_000_000_000 and <= 9_999_999_999_999)
            {
                return "{epoch-ms}";
            }
        }

        return element.GetRawText();
    }

    /// <summary>
    /// Rounds a clock-derived number of seconds: to the minute at two minutes and above,
    /// otherwise to ten seconds. A value one second short of a configured lifetime reads the same
    /// as the lifetime itself.
    /// </summary>
    internal static string ApproximateSeconds(JsonElement element)
        => element.TryGetInt64(out var seconds) ? ApproximateSeconds(seconds) : element.GetRawText();

    internal static string ApproximateSeconds(long seconds)
    {
        var rounded = Math.Abs(seconds) >= 120
            ? (long)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero) * 60
            : (long)Math.Round(seconds / 10.0, MidpointRounding.AwayFromZero) * 10;
        return "~" + rounded.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>data:image/png;base64,…</c> becomes <c>{data-uri:image/png}</c>.</summary>
    internal static string DataUri(string value)
    {
        var mime = value[5..].Split(';', ',')[0];
        return $"{{data-uri:{(string.IsNullOrWhiteSpace(mime) ? "unknown" : mime.ToLowerInvariant())}}}";
    }

    internal static string Quote(string value)
        => JsonSerializer.Serialize(value, StringEscaping);

    /// <summary>Formats a number of seconds as the largest exact unit: <c>600</c> becomes <c>10m</c>.</summary>
    internal static string FormatSeconds(long seconds)
    {
        var sign = seconds < 0 ? "-" : string.Empty;
        var magnitude = Math.Abs(seconds);
        if (magnitude == 0)
        {
            return "0s";
        }

        if (magnitude % 86_400 == 0)
        {
            return $"{sign}{(magnitude / 86_400).ToString(CultureInfo.InvariantCulture)}d";
        }

        if (magnitude % 3_600 == 0)
        {
            return $"{sign}{(magnitude / 3_600).ToString(CultureInfo.InvariantCulture)}h";
        }

        if (magnitude % 60 == 0)
        {
            return $"{sign}{(magnitude / 60).ToString(CultureInfo.InvariantCulture)}m";
        }

        return $"{sign}{magnitude.ToString(CultureInfo.InvariantCulture)}s";
    }

    /// <summary>A SqlOS-generated ID such as <c>org_…</c> (the prefixes the scrubber detects).</summary>
    [System.Text.RegularExpressions.GeneratedRegex("[a-z][a-z0-9]{0,11}_[0-9a-f]{16,32}")]
    private static partial System.Text.RegularExpressions.Regex GeneratedId();
}
