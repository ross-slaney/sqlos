namespace SqlOS.AuthServer.Services;

/// <summary>
/// Shared OAuth scope normalization and grant policy.
/// Authorize, device, and client_credentials all apply the same silent intersection
/// (RFC 6749 §3.3): granted = requested ∩ client allow-list. An empty allow-list
/// intersects to an empty grant. Unknown requested scopes are dropped, not rejected.
/// </summary>
internal static class SqlOSScopePolicy
{
    public static List<string> Split(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return [];
        }

        return scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static List<string> Intersect(IEnumerable<string> requested, IReadOnlyCollection<string> allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        return requested
            .Where(allowedSet.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static List<string> Grant(string? requestedScope, string? allowedScopesJson)
        => Intersect(Split(requestedScope), SqlOSAdminService.DeserializeJsonList(allowedScopesJson));

    public static string Join(IEnumerable<string> scopes)
        => string.Join(' ', scopes);
}
