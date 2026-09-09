using SqlOS.Fga.Configuration;
using SqlOS.Hosting;

namespace SqlOS.AuthServer.Configuration;

/// <summary>
/// Describes application hosting independently of OAuth client registration: protected surfaces,
/// supported scopes, branding, and the authorization model.
/// </summary>
public class SqlOSApplicationOptions
{
    /// <summary>Gets or sets the application display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute application origin used to derive the default redirect URI and,
    /// when <see cref="Api"/> or <see cref="Mcp"/> is set, the protected-resource identifiers.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>
    /// Gets or sets the application-relative REST API path, for example <c>/api</c>. When set,
    /// SqlOS uses <c>{Origin}{Api}</c> as the default JWT audience and first-party client audience
    /// and serves the matching RFC 9728 protected-resource document at
    /// <c>/.well-known/oauth-protected-resource</c>. Application routes under this path are not
    /// locked until they call <c>RequireAuthorization()</c>.
    /// </summary>
    public string? Api { get; set; }

    /// <summary>
    /// Gets or sets the application-relative MCP path, for example <c>/mcp</c>. When set, SqlOS
    /// uses <c>{Origin}{Mcp}</c> as that surface's audience, serves the protected-resource
    /// document at <c>/.well-known/oauth-protected-resource{Mcp}</c>, registers scheme/policy
    /// <c>SqlOS.Mcp</c>, and enables client ID metadata documents and resource indicators so
    /// portable MCP clients can connect. The host maps Microsoft's MCP SDK on this path and calls
    /// <c>RequireAuthorization("SqlOS.Mcp")</c>. Dynamic client registration is not enabled by this
    /// property.
    /// </summary>
    public string? Mcp { get; set; }

    /// <summary>
    /// Gets the host extensions registered on this application description.
    /// SqlOS runs <see cref="ISqlOSHostExtension.ConfigureServices"/> during <c>AddSqlOS</c> and
    /// <see cref="ISqlOSHostExtension.MapEndpoints"/> when it maps its own endpoints at startup.
    /// </summary>
    public IList<ISqlOSHostExtension> HostExtensions { get; } = [];

    internal List<Action<SqlOSAuthPageSeedOptions>> BrandConfigurations { get; } = [];

    internal List<Action<SqlOSFgaSeedBuilder>> AuthorizationConfigurations { get; } = [];

    internal List<Action<SqlOSHeadlessAuthOptions>> HeadlessConfigurations { get; } = [];

    /// <summary>
    /// Uses your own sign-in UI instead of the hosted SqlOS pages (headless mode). SqlOS redirects
    /// browser interaction from <c>/sqlos/auth/authorize</c> to <c>{Origin}{uiPath}</c> and forwards
    /// the standard headless parameters (<c>request</c>, <c>view</c>, <c>error</c>, <c>email</c>,
    /// <c>displayName</c>, <c>pendingToken</c>, <c>mfaToken</c>, <c>consentToken</c>,
    /// <c>ui_context</c>), which the <c>@sqlos/headless</c> package reads. Equivalent to
    /// <see cref="SqlOSAuthServerOptions.UseHeadlessAuthPage"/> with a generated <c>BuildUiUrl</c>.
    /// </summary>
    /// <param name="uiPath">The absolute path of your sign-in UI under <see cref="Origin"/>, for example <c>/auth/authorize</c>.</param>
    /// <param name="configure">Optional further headless configuration (API base path, signup hook).</param>
    public SqlOSApplicationOptions Headless(string uiPath, Action<SqlOSHeadlessAuthOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uiPath);
        var normalizedPath = uiPath.Trim();
        if (!normalizedPath.StartsWith('/'))
        {
            throw new ArgumentException("The headless UI path must be absolute and start with '/'.", nameof(uiPath));
        }

        HeadlessConfigurations.Add(headless =>
        {
            var origin = (Origin ?? throw new InvalidOperationException(
                "app.Headless(uiPath) requires app.Origin so SqlOS can build the UI URL.")).TrimEnd('/');
            headless.BuildUiUrl = context => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                origin + normalizedPath,
                new Dictionary<string, string?>
                {
                    ["request"] = context.RequestId,
                    ["view"] = context.View,
                    ["error"] = context.Error,
                    ["email"] = context.Email,
                    ["displayName"] = context.DisplayName,
                    ["pendingToken"] = context.PendingToken,
                    ["mfaToken"] = context.MfaToken,
                    ["consentToken"] = context.ConsentToken,
                    ["ui_context"] = context.UiContext?.ToJsonString()
                });
            configure?.Invoke(headless);
        });
        return this;
    }

    /// <summary>
    /// Uses your own sign-in UI instead of the hosted SqlOS pages with full control over the
    /// redirect. Equivalent to <see cref="SqlOSAuthServerOptions.UseHeadlessAuthPage"/>; set
    /// <see cref="SqlOSHeadlessAuthOptions.BuildUiUrl"/> in <paramref name="configure"/>.
    /// </summary>
    public SqlOSApplicationOptions Headless(Action<SqlOSHeadlessAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        HeadlessConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Brands the hosted sign-in page for this application. Equivalent to
    /// <see cref="SqlOSAuthServerOptions.SeedAuthPage"/>, applied on top of the single-application
    /// defaults (page title, password signup, and credential types).
    /// </summary>
    public SqlOSApplicationOptions Brand(Action<SqlOSAuthPageSeedOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        BrandConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Declares the application's authorization model. Equivalent to
    /// <see cref="SqlOSFgaOptions.Seed"/>; SqlOS reconciles the seed on startup.
    /// </summary>
    public SqlOSApplicationOptions Authorization(Action<SqlOSFgaSeedBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        AuthorizationConfigurations.Add(configure);
        return this;
    }

    /// <summary>Gets or sets the scopes advertised by the protected resources. In single-application mode these also seed the first-party client allowlist.</summary>
    public List<string> AllowedScopes { get; set; } = ["openid", "profile", "email", "offline_access"];

    /// <summary>Gets or sets whether the hosted sign-in page allows password sign-up.</summary>
    public bool EnablePasswordSignup { get; set; } = true;

    /// <summary>Gets or sets the credential types enabled on the hosted sign-in page.</summary>
    public List<string> EnabledCredentialTypes { get; set; } = ["password"];

    /// <summary>Gets or sets whether the application name and credential settings configure the hosted sign-in page.</summary>
    public bool ConfigureAuthPageBranding { get; set; } = true;

    /// <summary>Gets or sets whether the application name configures transactional email branding.</summary>
    public bool ConfigureEmailBranding { get; set; } = true;
}
