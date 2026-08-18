using System.Diagnostics.CodeAnalysis;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Resolves user-controlled local return destinations that may be emitted as a
/// <c>Location</c> header. Accepts only same-origin absolute paths that begin
/// with exactly one <c>/</c>, contain no scheme or authority, and stay under the
/// configured application origin after decoding and normalization.
/// </summary>
internal static class SqlOSLocalRedirectDestination
{
    private const int MaxDecodePasses = 8;

    public static bool TryResolve(
        string? requestedUrl,
        string applicationOrigin,
        [NotNullWhen(true)] out string? localDestination)
    {
        localDestination = null;
        if (string.IsNullOrWhiteSpace(requestedUrl) || string.IsNullOrWhiteSpace(applicationOrigin))
        {
            return false;
        }

        foreach (var character in requestedUrl)
        {
            if (IsUnsafeLocalPathCharacter(character))
            {
                return false;
            }
        }

        var trimmed = requestedUrl.Trim();
        if (!IsSafeLocalAbsolutePath(trimmed)
            || !TryFullyDecode(trimmed, out var decoded)
            || !IsSafeLocalAbsolutePath(decoded)
            || !TryResolveAgainstOrigin(trimmed, applicationOrigin, out var fromOriginal)
            || !TryResolveAgainstOrigin(decoded, applicationOrigin, out var fromDecoded)
            || !string.Equals(fromOriginal, fromDecoded, StringComparison.Ordinal))
        {
            return false;
        }

        localDestination = fromDecoded;
        return true;
    }

    private static bool IsSafeLocalAbsolutePath(string value)
    {
        if (value.Length == 0 || value[0] != '/')
        {
            return false;
        }

        if (value.Length > 1 && (value[1] == '/' || value[1] == '\\'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (IsUnsafeLocalPathCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnsafeLocalPathCharacter(char character)
        => character == '\\'
           || char.IsControl(character)
           || character is '\u2028' or '\u2029';

    private static bool TryFullyDecode(string value, out string decoded)
    {
        decoded = value;
        for (var pass = 0; pass < MaxDecodePasses; pass++)
        {
            string next;
            try
            {
                next = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                return true;
            }

            decoded = next;
        }

        return false;
    }

    private static bool TryResolveAgainstOrigin(
        string candidate,
        string applicationOrigin,
        [NotNullWhen(true)] out string? localDestination)
    {
        localDestination = null;
        if (!Uri.TryCreate(applicationOrigin, UriKind.Absolute, out var originUri)
            || (!string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var originAuthority = originUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        if (!Uri.TryCreate(originAuthority + "/", UriKind.Absolute, out var originBase)
            || !Uri.TryCreate(originBase, candidate, out var resolved)
            || !resolved.IsAbsoluteUri
            || (!string.Equals(resolved.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resolved.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.Equals(
                resolved.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
                originAuthority,
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(resolved.UserInfo)
            || resolved.PathAndQuery.Length == 0
            || resolved.PathAndQuery[0] != '/'
            || (resolved.PathAndQuery.Length > 1
                && (resolved.PathAndQuery[1] == '/' || resolved.PathAndQuery[1] == '\\')))
        {
            return false;
        }

        localDestination = resolved.GetComponents(
            UriComponents.PathAndQuery | UriComponents.Fragment,
            UriFormat.UriEscaped);
        return !string.IsNullOrEmpty(localDestination)
               && IsSafeLocalAbsolutePath(localDestination);
    }
}
