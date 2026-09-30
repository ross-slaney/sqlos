using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Decodes a JWT for the transcript: canonical header and claims, epoch claims as
/// <c>{epoch}</c>, and the token's lifetimes as exact relative seconds (<c>exp-iat=10m</c>) so a
/// lifetime change stays visible although the timestamps themselves are scrubbed. The signature
/// is not shown; it changes with every token.
/// </summary>
public static partial class JwtRendering
{
    private static readonly string[] LifetimeClaims = ["iat", "nbf", "exp"];

    /// <summary>
    /// Claims that only record when a token was minted, or hash a sibling token minted at the
    /// same moment. They are left out of a token's identity.
    /// </summary>
    private static readonly HashSet<string> TimingClaims = new(StringComparer.Ordinal)
    {
        "iat", "nbf", "exp", "auth_time", "at_hash", "c_hash", "s_hash"
    };

    public static bool LooksLikeJwt(string value) => WholeJwt().IsMatch(value);

    /// <summary>
    /// A JWT's identity for placeholder purposes: its header and claims without timing claims.
    /// RS256 signatures are deterministic and SqlOS access tokens carry no <c>jti</c>, so two
    /// tokens with the same claims are byte-identical when minted in the same second and differ
    /// when minted a second apart. Keying placeholders on identity keeps transcripts independent
    /// of that timing: tokens that differ only in when they were minted share a placeholder.
    /// </summary>
    public static string Identity(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2 || !TryDecode(parts[0], out var header) || !TryDecode(parts[1], out var claims))
        {
            return jwt;
        }

        using (header)
        using (claims)
        {
            var identity = new StringBuilder("jwt|").Append(header.RootElement.GetRawText()).Append('|');
            if (claims.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var claim in claims.RootElement.EnumerateObject()
                             .Where(claim => !TimingClaims.Contains(claim.Name))
                             .OrderBy(claim => claim.Name, StringComparer.Ordinal))
                {
                    identity.Append(claim.Name).Append('=').Append(claim.Value.GetRawText()).Append(';');
                }
            }

            return identity.ToString();
        }
    }

    public static string Render(string jwt, TranscriptValueSink sink, string indent)
    {
        var parts = jwt.Split('.');
        var builder = new StringBuilder();
        builder.Append(indent).Append("jwt ").Append(jwt).Append('\n');
        if (!TryDecode(parts[0], out var header) || !TryDecode(parts[1], out var claims))
        {
            builder.Append(indent).Append("  (not decodable)\n");
            return builder.ToString();
        }

        using (header)
        using (claims)
        {
            builder.Append(indent).Append("  header: ")
                .Append(Indent(CanonicalJson.Render(header.RootElement, sink), indent + "  "))
                .Append('\n');
            builder.Append(indent).Append("  claims: ")
                .Append(Indent(CanonicalJson.Render(claims.RootElement, sink), indent + "  "))
                .Append('\n');
            var lifetimes = DescribeLifetimes(claims.RootElement);
            if (lifetimes.Length > 0)
            {
                builder.Append(indent).Append("  lifetimes: ").Append(lifetimes).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Relative lifetimes between the epoch claims present, for example <c>exp-iat=10m exp-nbf=10m</c>.</summary>
    internal static string DescribeLifetimes(JsonElement claims)
    {
        if (claims.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var claim in LifetimeClaims.Append("auth_time"))
        {
            if (claims.TryGetProperty(claim, out var value) && value.TryGetInt64(out var seconds))
            {
                values[claim] = seconds;
            }
        }

        var parts = new List<string>();
        if (values.TryGetValue("exp", out var exp))
        {
            if (values.TryGetValue("iat", out var iat))
            {
                parts.Add($"exp-iat={CanonicalJson.FormatSeconds(exp - iat)}");
            }

            if (values.TryGetValue("nbf", out var nbf))
            {
                parts.Add($"exp-nbf={CanonicalJson.FormatSeconds(exp - nbf)}");
            }
        }

        if (values.TryGetValue("nbf", out var notBefore) && values.TryGetValue("iat", out var issuedAt))
        {
            parts.Add($"nbf-iat={CanonicalJson.FormatSeconds(notBefore - issuedAt)}");
        }

        return string.Join(' ', parts);
    }

    private static bool TryDecode(string segment, out JsonDocument document)
    {
        document = null!;
        try
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            document = JsonDocument.Parse(Convert.FromBase64String(padded));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string Indent(string text, string indent)
        => text.Replace("\n", "\n" + indent, StringComparison.Ordinal);

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_\-]+\.eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeJwt();
}

/// <summary>
/// Renders one <c>Set-Cookie</c> header: the name, the value registered as a <c>{cookie#n}</c>
/// placeholder, and every attribute in the order and spelling the server sent. An
/// <c>expires</c> date is recorded as a duration relative to the response (<c>expires=+15m</c>)
/// when it is a lifetime; the fixed past date used to delete a cookie renders as
/// <c>{unix-epoch}</c> (or <c>{past}</c> for any other past date).
/// </summary>
public static class SetCookieRendering
{
    public static string Render(string header, DateTimeOffset requestStarted, DateTimeOffset responseCompleted, TranscriptValueSink sink)
    {
        var segments = header.Split(';');
        var nameValue = segments[0];
        var separator = nameValue.IndexOf('=', StringComparison.Ordinal);
        var name = separator < 0 ? nameValue.Trim() : nameValue[..separator].Trim();
        var value = separator < 0 ? string.Empty : nameValue[(separator + 1)..].Trim();
        if (value.Length > 0)
        {
            sink.Register(value, "cookie");
        }

        var builder = new StringBuilder().Append(name).Append('=').Append(value);
        foreach (var segment in segments.Skip(1))
        {
            var attribute = segment.Trim();
            builder.Append("; ");
            var equals = attribute.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0
                && attribute[..equals].Trim().Equals("expires", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParseExact(
                    attribute[(equals + 1)..].Trim(),
                    "r",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var expires))
            {
                builder.Append(attribute[..equals]).Append('=').Append(DescribeExpiry(expires, requestStarted, responseCompleted, attribute[(equals + 1)..].Trim()));
            }
            else
            {
                builder.Append(attribute);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A cookie expiry is <c>now + lifetime</c> at some instant during the request, truncated to
    /// the second. Measuring from the middle of the request and rounding to the minute (the
    /// nearest 10 seconds below two minutes) recovers the configured lifetime deterministically.
    /// </summary>
    internal static string DescribeExpiry(DateTimeOffset expires, DateTimeOffset requestStarted, DateTimeOffset responseCompleted, string literal)
    {
        var middle = requestStarted + (responseCompleted - requestStarted) / 2;
        var seconds = (expires.AddSeconds(0.5) - middle).TotalSeconds;
        if (seconds < -86_400)
        {
            // A fixed date in the past deletes the cookie; say which one, without a timestamp.
            return expires == DateTimeOffset.UnixEpoch ? "{unix-epoch}" : "{past}";
        }

        var rounded = Math.Abs(seconds) >= 120
            ? (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero) * 60
            : (long)Math.Round(seconds / 10, MidpointRounding.AwayFromZero) * 10;
        return (rounded >= 0 ? "+" : string.Empty) + CanonicalJson.FormatSeconds(rounded);
    }
}
