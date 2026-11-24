using SqlOS.Email.Contracts;

namespace SqlOS.Email.Services;

/// <summary>
/// Builds RFC 2369 <c>List-Unsubscribe</c> and RFC 8058 <c>List-Unsubscribe-Post</c> header values.
/// </summary>
internal static class SqlOSListUnsubscribeHeaders
{
    internal const string ListUnsubscribe = "List-Unsubscribe";
    internal const string ListUnsubscribePost = "List-Unsubscribe-Post";
    internal const string OneClickPostValue = "List-Unsubscribe=One-Click";

    internal static IReadOnlyDictionary<string, string>? Build(SqlOSEmailListUnsubscribe? listUnsubscribe)
    {
        if (listUnsubscribe == null)
        {
            return null;
        }

        if (listUnsubscribe.OneClickUrl == null && listUnsubscribe.MailTo == null)
        {
            throw new InvalidOperationException(
                "List-Unsubscribe requires a one-click HTTPS URL, a mailto URI, or both.");
        }

        var targets = new List<string>(2);
        if (listUnsubscribe.OneClickUrl != null)
        {
            targets.Add(FormatTarget(listUnsubscribe.OneClickUrl, Uri.UriSchemeHttps, "one-click URL"));
        }

        if (listUnsubscribe.MailTo != null)
        {
            targets.Add(FormatTarget(listUnsubscribe.MailTo, Uri.UriSchemeMailto, "mailto URI"));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ListUnsubscribe] = string.Join(", ", targets)
        };

        if (listUnsubscribe.OneClickUrl != null)
        {
            headers[ListUnsubscribePost] = OneClickPostValue;
        }

        return headers;
    }

    private static string FormatTarget(Uri uri, string scheme, string description)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException(
                $"List-Unsubscribe {description} must be an absolute {scheme} URI.");
        }

        if (scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"List-Unsubscribe {description} must not contain user info.");
        }

        var value = uri.AbsoluteUri;
        if (value.Any(static c => c is < '!' or > '~' or '<' or '>'))
        {
            throw new InvalidOperationException(
                $"List-Unsubscribe {description} must be printable ASCII. Use the punycode form of internationalized host names.");
        }

        return $"<{value}>";
    }
}
