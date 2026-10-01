using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Shared OAuth scope normalization and grant policy.
/// Authorize, device, and client_credentials all apply the same silent intersection
/// (RFC 6749 §3.3): granted = requested ∩ client allow-list. An empty allow-list
/// intersects to an empty grant. Unknown requested scopes are dropped, not rejected.
/// The normalization rules live in <see cref="ScopeSet"/>.
/// </summary>
internal static class SqlOSScopePolicy
{
    public static List<string> Split(string? scope) => [.. ScopeSet.Parse(scope)];

    public static List<string> Intersect(IEnumerable<string> requested, IReadOnlyCollection<string> allowed)
        => [.. ScopeSet.Of(requested).IntersectWith(allowed)];

    public static List<string> Grant(string? requestedScope, string? allowedScopesJson)
        => [.. ScopeSet.Parse(requestedScope).IntersectWith(SqlOSAdminService.DeserializeJsonList(allowedScopesJson))];

    public static string Join(IEnumerable<string> scopes)
        => string.Join(' ', scopes);
}
