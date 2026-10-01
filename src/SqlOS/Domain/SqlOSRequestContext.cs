namespace SqlOS.Domain;

/// <summary>
/// The request a unit of work serves: where it came from and through which surface. Processes and
/// the audit projection read it for audit rows and admission; they never see the HTTP request.
/// </summary>
/// <remarks>
/// Adapters capture it from the HTTP request (<c>SqlOSHttpRequestContext</c>) and pass it to the
/// process they call, or set it on the scope's <see cref="SqlOSRequestContextAccessor"/>. Values are
/// kept as captured; whoever records them normalizes them as 7.x did (for example an absent user
/// agent is an empty string, which an audit row stores as no user agent). Work outside a request,
/// such as startup reconciliation and hosted services, runs with <see cref="System"/>.
/// <see cref="Route"/> is the request's path (path base and path), which the direct-login refusal
/// records as 7.x did, or null when the request has none.
/// </remarks>
internal sealed record SqlOSRequestContext(
    SqlOSRequestSurface Surface,
    string? IpAddress,
    string? UserAgent,
    string? RequestId,
    string? CorrelationId,
    string? Route = null)
{
    /// <summary>No request: startup, background work and code that runs outside an HTTP request.</summary>
    public static SqlOSRequestContext System { get; } = new(SqlOSRequestSurface.System, null, null, null, null);
}

/// <summary>The surface a request arrived on.</summary>
internal enum SqlOSRequestSurface
{
    /// <summary>No request (startup, hosted services, host code outside a request).</summary>
    System = 0,

    /// <summary>The hosted AuthPage.</summary>
    Hosted = 1,

    /// <summary>The headless API behind an application's own sign-in UI.</summary>
    Headless = 2,

    /// <summary>The JSON public account API.</summary>
    PublicApi = 3,

    /// <summary>OAuth 2.0 and OpenID Connect protocol endpoints.</summary>
    Protocol = 4,

    /// <summary>The admin API and the dashboard, used by an operator.</summary>
    Admin = 5,

    /// <summary>SAML, upstream OIDC and SCIM: an identity provider or directory calling in.</summary>
    Federation = 6,

    /// <summary>The SSO setup portal.</summary>
    SsoPortal = 7
}
