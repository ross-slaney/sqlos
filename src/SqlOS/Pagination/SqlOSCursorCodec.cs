using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlOS.Pagination;

/// <summary>
/// Versioned opaque cursor codec shared by admin list endpoints.
/// Payloads bind to a sort identity and filter fingerprint so a cursor cannot
/// be reused across a different query context.
/// </summary>
public static class SqlOSCursorCodec
{
    public const int CurrentVersion = 1;
    public const int MaxEncodedLength = 4096;

    /// <summary>
    /// The longest <c>ToPageAsync</c> cursor. Its values are a row's order values, and text can be
    /// long; order columns that page orders index are bounded by the engine's index key size.
    /// </summary>
    internal const int MaxPageCursorLength = 65536;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Page cursors keep non-ASCII text unescaped (one to four bytes a character instead of six). The
    // JSON is base64url-encoded, so it is never read as HTML or script.
    private static readonly JsonSerializerOptions PageJsonOptions = new(JsonOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Encode(string sortKey, string filterFingerprint, IReadOnlyList<string> keyValues)
        => Encode(sortKey, filterFingerprint, keyValues, JsonOptions);

    /// <summary>Encodes a <c>ToPageAsync</c> cursor, which <see cref="TryReadPage"/> reads.</summary>
    internal static string EncodePage(string sortKey, string filterFingerprint, IReadOnlyList<string> keyValues)
        => Encode(sortKey, filterFingerprint, keyValues, PageJsonOptions);

    private static string Encode(
        string sortKey,
        string filterFingerprint,
        IReadOnlyList<string> keyValues,
        JsonSerializerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sortKey);
        ArgumentNullException.ThrowIfNull(filterFingerprint);
        ArgumentNullException.ThrowIfNull(keyValues);
        if (keyValues.Count == 0)
        {
            throw new ArgumentException("Cursor key values are required.", nameof(keyValues));
        }

        var payload = new CursorPayload
        {
            V = CurrentVersion,
            S = sortKey,
            F = filterFingerprint,
            K = keyValues.ToArray()
        };
        var json = JsonSerializer.Serialize(payload, options);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public static IReadOnlyList<string> Decode(
        string cursor,
        string expectedSortKey,
        string expectedFilterFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSortKey);
        ArgumentNullException.ThrowIfNull(expectedFilterFingerprint);

        var payload = ReadPayload(cursor, MaxEncodedLength, out var error)
            ?? throw new SqlOSCursorException(error!);

        if (!string.Equals(payload.S, expectedSortKey, StringComparison.Ordinal))
        {
            throw new SqlOSCursorException("The cursor does not match this list.");
        }

        if (!string.Equals(payload.F, expectedFilterFingerprint, StringComparison.Ordinal))
        {
            throw new SqlOSCursorException("The cursor does not match the current filters.");
        }

        if (payload.K == null || payload.K.Length == 0)
        {
            throw new SqlOSCursorException("The cursor is invalid.");
        }

        return payload.K;
    }

    /// <summary>
    /// Reads a <c>ToPageAsync</c> cursor without checking which list it belongs to, so a caller can
    /// tell a cursor from another list apart from one that is not a cursor at all.
    /// </summary>
    internal static bool TryReadPage(
        string cursor,
        out string sortKey,
        out string filterFingerprint,
        out IReadOnlyList<string> keyValues)
    {
        var payload = ReadPayload(cursor, MaxPageCursorLength, out _);
        sortKey = payload?.S ?? string.Empty;
        filterFingerprint = payload?.F ?? string.Empty;
        keyValues = payload?.K ?? [];
        return payload is { K.Length: > 0 };
    }

    private static CursorPayload? ReadPayload(string cursor, int maxEncodedLength, out string? error)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            error = "The cursor is missing.";
            return null;
        }

        if (cursor.Length > maxEncodedLength)
        {
            error = "The cursor is invalid.";
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            error = "The cursor is invalid.";
            return null;
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            error = "The cursor is invalid.";
            return null;
        }

        if (payload == null || payload.V != CurrentVersion)
        {
            error = "The cursor is not supported.";
            return null;
        }

        error = null;
        return payload;
    }

    public static string Fingerprint(params string?[] parts)
    {
        var canonical = string.Join('\u001f', parts.Select(static part => part ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 1:
                throw new FormatException();
        }

        return Convert.FromBase64String(padded);
    }

    private sealed class CursorPayload
    {
        public int V { get; set; }
        public string S { get; set; } = string.Empty;
        public string F { get; set; } = string.Empty;
        public string[] K { get; set; } = [];
    }
}
