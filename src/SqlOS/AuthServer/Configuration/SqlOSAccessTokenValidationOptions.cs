using Microsoft.AspNetCore.Http;

namespace SqlOS.AuthServer.Configuration;

/// <summary>
/// Configures bearer access-token validation for a SqlOS-protected ASP.NET Core pipeline or route group.
/// </summary>
public sealed class SqlOSAccessTokenValidationOptions
{
    /// <summary>
    /// Gets or sets the exact audience that a validated access token must contain.
    /// </summary>
    public string ExpectedAudience { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional predicate that decides whether the current request should be validated.
    /// </summary>
    /// <remarks>When the predicate returns <see langword="false"/>, validation is skipped for that request.</remarks>
    public Func<HttpContext, bool>? ShouldValidate { get; set; }

    /// <summary>
    /// Gets or sets scopes that the validated token's granted scope must include.
    /// Enforcement runs only when the collection is non-empty.
    /// </summary>
    /// <remarks>
    /// The scope claim records the ceiling of what the client application was granted, so
    /// this check bounds what a delegated (especially third-party) client may do with a
    /// user's token; per-user, per-resource authorization remains FGA's job — effective
    /// permission is the intersection of both. Tokens without a scope claim (sessions from
    /// before scope tracking, or direct non-OAuth logins) fail closed. Failures answer
    /// HTTP 403 with an RFC 6750 §3.1 <c>insufficient_scope</c> Bearer challenge.
    /// </remarks>
    public IReadOnlyCollection<string> RequiredScopes { get; set; } = [];

    /// <summary>Gets or sets the realm emitted in a failed request's Bearer challenge.</summary>
    public string Realm { get; set; } = "SqlOS API";

    /// <summary>
    /// Gets or sets an optional protected-resource metadata URL emitted in the Bearer challenge.
    /// </summary>
    public string? ResourceMetadataUrl { get; set; }
}
