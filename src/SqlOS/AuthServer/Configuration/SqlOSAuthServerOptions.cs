using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlOS.AuthServer.Contracts;
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
    /// <summary>
    /// Protects SqlOS JWT signing private keys with ASP.NET Core Data Protection before storing
    /// them in the application database. Enable this only when the host application's Data
    /// Protection key ring is persisted and shared by every application instance and revision;
    /// container-local key rings make protected signing keys unreadable after replacement or
    /// scale-out and can prevent token issuance.
    /// </summary>
    public bool ProtectSigningKeysWithDataProtection { get; set; }
    public int DefaultSigningKeyRotationIntervalDays { get; set; } = 90;
    public int DefaultSigningKeyGraceWindowDays { get; set; } = 7;
    public int DefaultSigningKeyRetiredCleanupDays { get; set; } = 30;
    public SqlOSEmailOtpOptions EmailOtp { get; } = new();
    public SqlOSPhoneOtpOptions PhoneOtp { get; } = new();
    public SqlOSMfaOptions Mfa { get; } = new();
    public SqlOSPasswordResetOptions PasswordReset { get; } = new();
    public SqlOSPasswordLoginAbuseOptions PasswordLogin { get; } = new();
    public SqlOSInvitationOptions Invitations { get; } = new();
    public SqlOSSsoPortalOptions SsoPortal { get; } = new();
    public SqlOSDeviceAuthorizationOptions DeviceAuthorization { get; } = new();
    public SqlOSClientRegistrationOptions ClientRegistration { get; } = new();
    public SqlOSResourceIndicatorOptions ResourceIndicators { get; } = new();
    public SqlOSDashboardOptions Dashboard { get; set; } = new();
    public SqlOSHeadlessAuthOptions Headless { get; } = new();
    public SqlOSAuthPageSeedOptions? AuthPageSeed { get; private set; }
    public SqlOSAuthEmailSeedOptions? AuthEmailSeed { get; private set; }
    public SqlOSMfaSeedOptions? MfaSeed { get; private set; }
    public SqlOSSingleApplicationOptions? SingleApplication { get; private set; }
    public List<SqlOSClientSeedOptions> ClientSeeds { get; } = [];
    public List<SqlOSOidcConnectionSeedOptions> OidcConnectionSeeds { get; } = [];

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

    public SqlOSAuthServerOptions SeedAuthEmails(Action<SqlOSAuthEmailSeedOptions> configure)
    {
        var seed = AuthEmailSeed ?? new SqlOSAuthEmailSeedOptions();
        configure(seed);
        AuthEmailSeed = seed;
        return this;
    }

    public SqlOSAuthServerOptions SeedMfaPolicy(Action<SqlOSMfaSeedOptions> configure)
    {
        var seed = MfaSeed ?? new SqlOSMfaSeedOptions();
        configure(seed);
        MfaSeed = seed;
        return this;
    }

    public SqlOSAuthServerOptions SeedClient(Action<SqlOSClientSeedOptions> configure)
    {
        var seed = new SqlOSClientSeedOptions();
        configure(seed);
        ClientSeeds.Add(seed);
        return this;
    }

    /// <summary>
    /// Seed a social/OIDC login connection (Google, Microsoft, Apple, or custom). The connection is
    /// reconciled into the database on startup, matched by provider type (and display name for custom
    /// providers). Callback URIs may include the <c>{connectionId}</c> placeholder.
    /// </summary>
    public SqlOSAuthServerOptions SeedOidcConnection(Action<SqlOSOidcConnectionSeedOptions> configure)
    {
        var seed = new SqlOSOidcConnectionSeedOptions();
        configure(seed);
        OidcConnectionSeeds.Add(seed);
        return this;
    }

    /// <summary>
    /// Seed a "Continue with Microsoft" (Microsoft Entra) social login connection. When no callback URIs
    /// are supplied, the SqlOS-owned callback URI (<c>{connectionId}</c> placeholder) is used so the
    /// connection works against the host's own origin.
    /// </summary>
    public SqlOSAuthServerOptions SeedMicrosoftConnection(
        string clientId,
        string clientSecret,
        string? tenant = null,
        params string[] allowedCallbackUris)
        => SeedOidcConnection(oidc =>
        {
            oidc.ProviderType = SqlOSOidcProviderType.Microsoft;
            oidc.DisplayName = "Microsoft";
            oidc.ClientId = clientId;
            oidc.ClientSecret = clientSecret;
            oidc.MicrosoftTenant = tenant;
            oidc.AllowedCallbackUris = allowedCallbackUris
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Select(static uri => uri.Trim())
                .ToList();
        });

    /// <summary>
    /// Seed a "Continue with Google" social login connection.
    /// </summary>
    public SqlOSAuthServerOptions SeedGoogleConnection(
        string clientId,
        string clientSecret,
        params string[] allowedCallbackUris)
        => SeedOidcConnection(oidc =>
        {
            oidc.ProviderType = SqlOSOidcProviderType.Google;
            oidc.DisplayName = "Google";
            oidc.ClientId = clientId;
            oidc.ClientSecret = clientSecret;
            oidc.AllowedCallbackUris = allowedCallbackUris
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Select(static uri => uri.Trim())
                .ToList();
        });

    /// <summary>
    /// Seed a "Continue with GitHub" social login connection. GitHub user sign-in is OAuth 2.0
    /// with provider profile/email lookups, not OIDC, but it uses the same persisted social
    /// provider configuration and browser/headless login surface as OIDC providers.
    /// </summary>
    public SqlOSAuthServerOptions SeedGitHubConnection(
        string clientId,
        string clientSecret,
        params string[] allowedCallbackUris)
        => SeedOidcConnection(oidc =>
        {
            oidc.ProviderType = SqlOSOidcProviderType.GitHub;
            oidc.DisplayName = "GitHub";
            oidc.ClientId = clientId;
            oidc.ClientSecret = clientSecret;
            oidc.AllowedCallbackUris = allowedCallbackUris
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Select(static uri => uri.Trim())
                .ToList();
        });

    public SqlOSAuthServerOptions UseSingleApplication(string name, Action<SqlOSSingleApplicationOptions>? configure = null)
    {
        var application = new SqlOSSingleApplicationOptions { Name = name };
        configure?.Invoke(application);
        return UseSingleApplication(application);
    }

    public SqlOSAuthServerOptions UseSingleApplication(SqlOSSingleApplicationOptions application)
    {
        if (string.IsNullOrWhiteSpace(application.Name))
        {
            throw new InvalidOperationException("Single-application mode requires an application name.");
        }

        SingleApplication = application;
        ClientRegistration.Cimd.Enabled = false;
        ResourceIndicators.Enabled = false;
        ApplySingleApplicationBranding(application);
        return this;
    }

    public SqlOSAuthServerOptions UseSingleApplication(IConfiguration configuration, string sectionName = "SqlOS:Application")
    {
        var section = configuration.GetSection(sectionName);
        if (!section.Exists())
        {
            throw new InvalidOperationException($"Configuration section '{sectionName}' was not found.");
        }

        var application = new SqlOSSingleApplicationOptions
        {
            Name = section["Name"] ?? string.Empty,
            Origin = section["Origin"],
            ClientId = section["ClientId"],
            Audience = section["Audience"],
            RedirectPath = section["RedirectPath"] ?? "/auth/callback",
            EnablePasswordSignup = ReadBool(section, "EnablePasswordSignup", true),
            ConfigureAuthPageBranding = ReadBool(section, "ConfigureAuthPageBranding", true),
            ConfigureEmailBranding = ReadBool(section, "ConfigureEmailBranding", true)
        };

        var redirectUris = section.GetSection("RedirectUris").GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
        application.RedirectUris.AddRange(redirectUris);

        var allowedScopes = section.GetSection("AllowedScopes").GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
        if (allowedScopes.Count > 0)
        {
            application.AllowedScopes = allowedScopes;
        }

        var credentialTypes = section.GetSection("EnabledCredentialTypes").GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
        if (credentialTypes.Count > 0)
        {
            application.EnabledCredentialTypes = credentialTypes;
        }

        return UseSingleApplication(application);
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

    public SqlOSAuthServerOptions ConfigureEmailOtp(Action<SqlOSEmailOtpOptions> configure)
    {
        configure(EmailOtp);
        return this;
    }

    public SqlOSAuthServerOptions ConfigurePhoneOtp(Action<SqlOSPhoneOtpOptions> configure)
    {
        configure(PhoneOtp);
        return this;
    }

    public SqlOSAuthServerOptions ConfigureMfa(Action<SqlOSMfaOptions> configure)
    {
        configure(Mfa);
        return this;
    }

    public SqlOSAuthServerOptions ConfigurePasswordReset(Action<SqlOSPasswordResetOptions> configure)
    {
        configure(PasswordReset);
        return this;
    }

    public SqlOSAuthServerOptions ConfigurePasswordLoginAbuse(Action<SqlOSPasswordLoginAbuseOptions> configure)
    {
        configure(PasswordLogin);
        return this;
    }

    public SqlOSAuthServerOptions ConfigureInvitations(Action<SqlOSInvitationOptions> configure)
    {
        configure(Invitations);
        return this;
    }

    public SqlOSAuthServerOptions ConfigureSsoPortal(Action<SqlOSSsoPortalOptions> configure)
    {
        configure(SsoPortal);
        return this;
    }

    public SqlOSAuthServerOptions ConfigureDeviceAuthorization(Action<SqlOSDeviceAuthorizationOptions> configure)
    {
        configure(DeviceAuthorization);
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

    public SqlOSAuthServerOptions SeedOwnedNativeApp(string clientId, string name, bool allowNativeHeadlessAuth = false, params string[] redirectUris)
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
            client.AllowNativeHeadlessAuth = allowNativeHeadlessAuth;
        });

    public SqlOSAuthServerOptions SeedCliClient(
        string clientId,
        string name,
        string? audience = null,
        params string[] allowedScopes)
        => SeedClient(client =>
        {
            client.ClientId = clientId;
            client.Name = name;
            client.Audience = audience;
            client.ClientType = "public_cli";
            client.RequirePkce = true;
            client.IsFirstParty = true;
            client.AllowDeviceAuthorization = true;
            client.AllowedScopes = allowedScopes
                .Where(static scope => !string.IsNullOrWhiteSpace(scope))
                .Select(static scope => scope.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        });

    public SqlOSAuthServerOptions SeedMcpStackClient(
        string clientId,
        string name,
        string? audience = null,
        params string[] allowedScopes)
        => SeedCliClient(clientId, name, audience, allowedScopes);

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

    private void ApplySingleApplicationBranding(SqlOSSingleApplicationOptions application)
    {
        if (application.ConfigureAuthPageBranding && AuthPageSeed == null)
        {
            SeedAuthPage(page =>
            {
                page.PageTitle = $"Sign in to {application.Name.Trim()}";
                page.EnablePasswordSignup = application.EnablePasswordSignup;
                page.EnabledCredentialTypes = application.EnabledCredentialTypes
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        if (application.ConfigureEmailBranding && AuthEmailSeed == null)
        {
            SeedAuthEmails(email => email.ApplicationName = application.Name.Trim());
        }
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool defaultValue)
        => bool.TryParse(section[key], out var parsed) ? parsed : defaultValue;
}

public sealed class SqlOSPasswordLoginAbuseOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxFailedAttemptsPerAccount { get; set; } = 5;
    public int MaxFailedAttemptsPerIp { get; set; } = 20;
    public int MaxFailedAttemptsPerClient { get; set; } = 50;
    public int MaxFailedAttemptsPerDevice { get; set; } = 20;
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class SqlOSSsoPortalOptions
{
    public TimeSpan DefaultLinkLifetime { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(2);
    public string CookieName { get; set; } = "sqlos_sso_portal";
    public bool EnableApi { get; set; } = true;
    public bool UseHostedPortal { get; set; } = true;
    public bool RequireVerifiedDomainForActivation { get; set; } = true;
    public bool AllowLocalhostDomainVerification { get; set; }
    public string? HeadlessApiBasePath { get; set; }
    public Func<SqlOSSsoSetupUiRouteContext, string>? BuildUiUrl { get; set; }
    public string DomainVerificationRecordPrefix { get; set; } = "_sqlos-verify";
    public string DomainVerificationRecordValuePrefix { get; set; } = "sqlos-domain-verification";
    public List<string> ReservedDomainRoots { get; } = [];

    public string ResolveHeadlessApiBasePath(string adminBasePath)
    {
        if (string.IsNullOrWhiteSpace(HeadlessApiBasePath))
        {
            return $"{adminBasePath.TrimEnd('/')}/sso-portal/api/setup";
        }

        var normalized = HeadlessApiBasePath.Trim();
        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized.TrimEnd('/') : $"/{normalized.TrimEnd('/')}";
    }
}

public sealed record SqlOSSsoSetupUiRouteContext(
    HttpContext HttpContext,
    string SessionId,
    string OrganizationId,
    string View);
