namespace SqlOS.AuthServer.Configuration;

/// <summary>
/// Controls SqlOS's OpenID Connect Provider role for downstream relying-party
/// applications: ID token issuance, the OIDC discovery document, and the
/// UserInfo endpoint. Protocol conformance is an internal secure default, so
/// provider mode ships enabled with an explicit off switch. This is distinct
/// from the upstream relying-party role (social login), which lives on
/// <c>SqlOSOidcConnection</c> and <c>SqlOSOidcAuthService</c>.
/// </summary>
public sealed class SqlOSOpenIdProviderOptions
{
    /// <summary>
    /// Master switch for the OpenID Provider role. When disabled, SqlOS behaves
    /// exactly as an OAuth 2.0 authorization server: no ID tokens are issued,
    /// no OIDC discovery document or UserInfo endpoint is served, and the OAuth
    /// metadata document is byte-identical to an OAuth-only deployment's.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Serves <c>/.well-known/openid-configuration</c> when provider mode is
    /// enabled.
    /// </summary>
    public bool PublishDiscoveryDocument { get; set; } = true;

    /// <summary>
    /// Serves <c>GET/POST {BasePath}/userinfo</c> when provider mode is enabled.
    /// </summary>
    public bool EnableUserInfoEndpoint { get; set; } = true;

    /// <summary>
    /// Lifetime of issued ID tokens. ID tokens are proof of authentication for
    /// the relying party's login ceremony, not API credentials, so the default
    /// is deliberately short.
    /// </summary>
    public TimeSpan IdTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
