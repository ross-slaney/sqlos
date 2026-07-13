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

    /// <summary>Gets or sets the realm emitted in a failed request's Bearer challenge.</summary>
    public string Realm { get; set; } = "SqlOS API";

    /// <summary>
    /// Gets or sets an optional protected-resource metadata URL emitted in the Bearer challenge.
    /// </summary>
    public string? ResourceMetadataUrl { get; set; }
}
