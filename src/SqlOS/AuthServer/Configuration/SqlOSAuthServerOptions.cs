using Microsoft.AspNetCore.Http;
using SqlOS.Configuration;

namespace SqlOS.AuthServer.Configuration;

public class SqlOSAuthServerOptions
{
    public string Schema { get; set; } = "dbo";
    public string BasePath { get; set; } = "/sqlos/auth";
    public string Issuer { get; set; } = "https://localhost/sqlos/auth";
    public string? PublicOrigin { get; set; }
    public string DefaultAudience { get; set; } = "sqlos";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan TemporaryTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan SessionAbsoluteLifetime { get; set; } = TimeSpan.FromDays(30);
    /// <summary>
    /// Grace window after a refresh token has been rotated during which the
    /// previous refresh token can still be exchanged. Concurrent and near-
    /// concurrent calls within the window receive the SAME new token pair
    /// that was issued at rotation time, instead of triggering replay
    /// detection. This prevents legitimate concurrent refresh requests
    /// (multiple tabs, parallel SSR calls, mobile retries, multi-instance
    /// load-balanced deployments) from being false-flagged as token theft.
    /// Default 30 seconds matches Okta's default. Set to 0 to disable the
    /// grace window for high-security clients (immediate replay detection
    /// on second use).
    /// </summary>
    public int RefreshTokenGraceWindowSeconds { get; set; } = 30;
    public bool RequireVerifiedEmailForPasswordLogin { get; set; }
    public bool EnableLocalPasswordAuth { get; set; } = true;
    public bool EnableSaml { get; set; } = true;
    public int DefaultSigningKeyRotationIntervalDays { get; set; } = 90;
    public int DefaultSigningKeyGraceWindowDays { get; set; } = 7;
    public int DefaultSigningKeyRetiredCleanupDays { get; set; } = 30;
    public SqlOSClientRegistrationOptions ClientRegistration { get; } = new();
    public SqlOSResourceIndicatorOptions ResourceIndicators { get; } = new();
    public SqlOSDashboardOptions Dashboard { get; set; } = new();
    public SqlOSHeadlessAuthOptions Headless { get; } = new();
    public SqlOSAuthPageSeedOptions? AuthPageSeed { get; private set; }
    public List<SqlOSClientSeedOptions> ClientSeeds { get; } = [];

    public SqlOSAuthServerOptions UseHeadlessAuthPage(Action<SqlOSHeadlessAuthOptions> configure)
    {
        configure(Headless);
        return this;
    }

    public SqlOSAuthServerOptions SeedAuthPage(Action<SqlOSAuthPageSeedOptions> configure)
    {
        var seed = AuthPageSeed ?? new SqlOSAuthPageSeedOptions();
        configure(seed);
        AuthPageSeed = seed;
        return this;
    }

    public SqlOSAuthServerOptions SeedClient(Action<SqlOSClientSeedOptions> configure)
    {
        var seed = new SqlOSClientSeedOptions();
        configure(seed);
        ClientSeeds.Add(seed);
        return this;
    }

    public SqlOSAuthServerOptions ConfigureClientRegistration(Action<SqlOSClientRegistrationOptions> configure)
    {
        configure(ClientRegistration);
        return this;
    }

    public SqlOSAuthServerOptions ConfigureResourceIndicators(Action<SqlOSResourceIndicatorOptions> configure)
    {
        configure(ResourceIndicators);
        return this;
    }

    public SqlOSAuthServerOptions SeedBrowserClient(string clientId, string name, params string[] redirectUris)
    {
        SeedClient(client =>
        {
            client.ClientId = clientId;
            client.Name = name;
            client.RedirectUris = redirectUris
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Select(static uri => uri.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            client.ClientType = "public_pkce";
            client.RequirePkce = true;
            client.IsFirstParty = true;
        });

        return this;
    }

    public SqlOSAuthServerOptions SeedOwnedWebApp(string clientId, string name, params string[] redirectUris)
        => SeedBrowserClient(clientId, name, redirectUris);

    public SqlOSAuthServerOptions SeedOwnedNativeApp(string clientId, string name, params string[] redirectUris)
        => SeedClient(client =>
        {
            client.ClientId = clientId;
            client.Name = name;
            client.RedirectUris = redirectUris
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Select(static uri => uri.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            client.ClientType = "public_pkce";
            client.RequirePkce = true;
            client.IsFirstParty = true;
        });

    public SqlOSAuthServerOptions EnablePortableMcpClients(Action<SqlOSClientRegistrationOptions>? configure = null)
    {
        ClientRegistration.Cimd.Enabled = true;
        ResourceIndicators.Enabled = true;
        ClientRegistration.Dcr.Enabled = false;
        configure?.Invoke(ClientRegistration);
        return this;
    }

    public SqlOSAuthServerOptions EnableChatGptCompatibility(Action<SqlOSDynamicClientRegistrationOptions>? configure = null)
    {
        ClientRegistration.Dcr.Enabled = true;
        ResourceIndicators.Enabled = true;
        configure?.Invoke(ClientRegistration.Dcr);
        return this;
    }

    public SqlOSAuthServerOptions EnableVsCodeCompatibility(Action<SqlOSDynamicClientRegistrationOptions>? configure = null)
    {
        ClientRegistration.Dcr.Enabled = true;
        ClientRegistration.Dcr.AllowLoopbackRedirectUris = true;
        ResourceIndicators.Enabled = true;
        configure?.Invoke(ClientRegistration.Dcr);
        return this;
    }
}
