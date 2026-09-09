using Microsoft.AspNetCore.Authentication;

namespace SqlOS.AuthServer.Authentication;

/// <summary>
/// Options for a same-process SqlOS JWT bearer scheme. Same role as
/// <c>JwtBearerOptions.Audience</c>: one scheme, one required audience.
/// </summary>
public sealed class SqlOSJwtOptions : AuthenticationSchemeOptions
{
    /// <summary>Gets or sets the required token <c>aud</c>, the same role as <c>JwtBearerOptions.Audience</c>.</summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>Gets or sets scopes the token's granted scope must include when the collection is non-empty.</summary>
    public IReadOnlyCollection<string> RequiredScopes { get; set; } = [];

    /// <summary>Gets or sets the realm emitted in a failed request's Bearer challenge.</summary>
    public string Realm { get; set; } = "SqlOS API";

    /// <summary>Gets or sets the RFC 9728 metadata URL emitted in the Bearer challenge.</summary>
    public string? ResourceMetadataUrl { get; set; }
}
