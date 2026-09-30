using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Authentication;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.Configuration;
using SqlOS.Fga.Configuration;
using static SqlOS.BehaviorLock.Host.BehaviorLockConstants;

namespace SqlOS.BehaviorLock.Host.Profiles;

/// <summary>
/// The deployment models SqlOS supports today, one profile each. Option names are the public
/// SqlOS API; each profile cites the documentation that describes its model. Profiles are cheap:
/// the coverage gate requires each route to be covered in at least one profile that exposes it,
/// not in every profile.
/// </summary>
public static class HostProfiles
{
    public const string Hosted = "hosted";
    public const string Headless = "headless";
    public const string MultiApp = "multi-app";
    public const string Mcp = "mcp";
    public const string Dcr = "dcr";
    public const string DashboardPassword = "dashboard-password";
    public const string DashboardCallback = "dashboard-callback";
    public const string DashboardDevelopment = "dashboard-dev";
    public const string DashboardOff = "dashboard-off";
    public const string Enterprise = "enterprise";
    public const string EnterpriseScimPath = "enterprise-scim-path";
    public const string Modules = "modules";
    public const string OAuthOnly = "oauth-only";
    public const string LegacyHost = "legacy-host";

    private static readonly IReadOnlyList<HostProfile> Profiles =
    [
        new HostProfile
        {
            Name = Hosted,
            DeploymentModel = "Single application with the hosted AuthPage and every first factor: password, email OTP, magic link, SMS OTP, and social/custom OIDC.",
            Documentation = ["web/content/docs/authserver/single-application.mdx", "web/content/docs/guides/configuration.mdx", "web/content/docs/authserver/hosted-vs-headless.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", EnabledCredentialTypes = [password, email_otp, magic_link, phone_otp], Brand(...), Authorization(workspace model) })",
                "AuthServer.PhoneOtp: Enabled = true with Twilio Verify settings (fake delivery channel)",
                "AuthServer.SeedGoogleConnection / SeedMicrosoftConnection / SeedGitHubConnection / SeedOidcConnection(\"custom\")",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                options.UseSingleApplication(ApplicationName, app =>
                {
                    app.Origin = PublicOrigin;
                    app.ClientId = AppClientId;
                    app.Api = ApiPath;
                    app.EnabledCredentialTypes = ["password", "email_otp", "magic_link", "phone_otp"];
                    app.Brand(page =>
                    {
                        page.PageSubtitle = "Behavior lock hosted sign-in.";
                        page.PrimaryColor = "#1d4ed8";
                        page.AccentColor = "#0f172a";
                    });
                    app.Authorization(SeedWorkspaceModel);
                });
                ConfigurePhoneOtp(options.AuthServer);
                SeedSocialConnections(options.AuthServer);
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = Headless,
            DeploymentModel = "Single application whose own UI drives sign-in through the headless API (app.Headless), including native headless clients.",
            Documentation = ["web/content/docs/guides/custom-login-ui.mdx", "web/content/docs/authserver/hosted-vs-headless.mdx", "web/content/docs/reference/headless-js.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Headless(\"/auth/authorize\"), AllowNativeHeadlessAuth = true, EnabledCredentialTypes = [password, email_otp, magic_link, phone_otp], Authorization(workspace model) })",
                "AuthServer.PhoneOtp: Enabled = true with Twilio Verify settings (fake delivery channel)",
                "AuthServer.SeedGoogleConnection / SeedOidcConnection(\"custom\")",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                options.UseSingleApplication(ApplicationName, app =>
                {
                    app.Origin = PublicOrigin;
                    app.ClientId = AppClientId;
                    app.Api = ApiPath;
                    app.AllowNativeHeadlessAuth = true;
                    app.EnabledCredentialTypes = ["password", "email_otp", "magic_link", "phone_otp"];
                    app.Headless("/auth/authorize");
                    app.Authorization(SeedWorkspaceModel);
                });
                ConfigurePhoneOtp(options.AuthServer);
                options.AuthServer.SeedGoogleConnection("google-client-id", "google-client-secret", SocialCallbackUri);
                SeedCustomOidcConnection(options.AuthServer);
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = MultiApp,
            DeploymentModel = "Standalone identity server for several applications: ConfigureApplication plus explicit client seeds with application access modes, a device-flow CLI, a native app, a partner (consent) client, a machine client, and separate resource APIs.",
            Documentation = ["web/content/docs/guides/standalone-identity-server.mdx", "web/content/docs/guides/multi-app-access.mdx", "web/content/docs/authserver/clients.mdx"],
            OptionsSummary =
            [
                "ConfigureApplication(\"Atlas Identity\", app => { Origin; Authorization(workspace model) })",
                "AuthServer.PublicOrigin, AuthServer.Issuer, AuthServer.DefaultAudience = https://api.example.test",
                "SeedClient atlas-portal (first party, AccessMode all_organizations), atlas-ops-console (first party, selected_users_groups_roles), atlas-partner (third party, consent), atlas-confidential (confidential, client_secret_post)",
                "SeedOwnedNativeApp(\"atlas-mobile\", allowNativeHeadlessAuth: true), SeedCliClient(\"atlas-cli\"), SeedMachineClient(\"atlas-worker\")",
                "SeedScopeDisplayName(\"workspace.read\", ...)",
                "Host: AddJwtBearer(\"ResourceApi\") against the issuer JWKS; AddSqlOSJwt(\"Billing\") for a second same-process audience",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                options.ConfigureApplication("Atlas Identity", app =>
                {
                    app.Origin = PublicOrigin;
                    app.Authorization(SeedWorkspaceModel);
                });
                var auth = options.AuthServer;
                auth.PublicOrigin = PublicOrigin;
                auth.Issuer = Issuer;
                auth.DefaultAudience = ResourceApiAudience;
                string[] scopes = ["openid", "profile", "email", "offline_access", BehaviorLockAuthorization.ReadPermission];
                auth.SeedClient(client =>
                {
                    client.ClientId = "atlas-portal";
                    client.Name = "Atlas Portal";
                    client.Audience = ResourceApiAudience;
                    client.RedirectUris = ["https://portal.example.test/auth/callback"];
                    client.AllowedScopes = [.. scopes];
                    client.IsFirstParty = true;
                    client.AccessMode = SqlOSApplicationAccessModes.AllOrganizations;
                });
                auth.SeedClient(client =>
                {
                    client.ClientId = "atlas-ops-console";
                    client.Name = "Atlas Ops Console";
                    client.Audience = ResourceApiAudience;
                    client.RedirectUris = ["https://ops.example.test/auth/callback"];
                    client.AllowedScopes = [.. scopes];
                    client.IsFirstParty = true;
                    client.AccessMode = SqlOSApplicationAccessModes.SelectedUsersGroupsRoles;
                });
                auth.SeedClient(client =>
                {
                    client.ClientId = "atlas-partner";
                    client.Name = "Atlas Partner";
                    client.Audience = ResourceApiAudience;
                    client.RedirectUris = ["https://partner.example.test/callback"];
                    client.AllowedScopes = [.. scopes];
                    client.IsFirstParty = false;
                });
                auth.SeedClient(client =>
                {
                    client.ClientId = "atlas-confidential";
                    client.Name = "Atlas Confidential Web";
                    client.Audience = ResourceApiAudience;
                    client.ClientType = "confidential";
                    client.TokenEndpointAuthMethod = "client_secret_post";
                    client.RequirePkce = true;
                    client.RedirectUris = ["https://web.example.test/signin-oidc"];
                    client.AllowedScopes = [.. scopes];
                    client.IsFirstParty = true;
                    client.ClientSecretResolver = () => "atlas-confidential-secret";
                });
                auth.SeedOwnedNativeApp("atlas-mobile", "Atlas Mobile", allowNativeHeadlessAuth: true, "com.example.atlas:/callback");
                auth.SeedCliClient("atlas-cli", "Atlas CLI", ResourceApiAudience, "openid", "profile", "email", "offline_access");
                auth.SeedMachineClient("atlas-worker", (client, machine) =>
                {
                    client.Name = "Atlas Worker";
                    client.Audience = ResourceApiAudience;
                    client.AllowedScopes = [BehaviorLockAuthorization.ReadPermission];
                    machine.SecretResolver = () => "atlas-worker-secret";
                });
                auth.SeedScopeDisplayName(BehaviorLockAuthorization.ReadPermission, "Read workspaces", "View workspaces you can access.");
            },
            ConfigureHost = AddResourceApiSchemes,
            MapApplication = (app, context) =>
            {
                MapResourceApis(app, context);
            }
        },
        new HostProfile
        {
            Name = Mcp,
            DeploymentModel = "Single application that declares a same-host MCP resource (app.Mcp): protected-resource metadata, CIMD, resource indicators, and the SqlOS.Mcp policy on the host's MCP route.",
            Documentation = ["web/content/docs/authserver/mcp-server.mdx", "web/content/docs/authserver/mcp-resource-indicators-and-audience.mdx", "web/content/docs/authserver/client-id-metadata-documents.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Mcp = \"/mcp\", Authorization(workspace model) })",
                "Host: MapPost(\"/mcp\").RequireAuthorization(\"SqlOS.Mcp\")",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                options.UseSingleApplication(ApplicationName, app =>
                {
                    app.Origin = PublicOrigin;
                    app.ClientId = AppClientId;
                    app.Api = ApiPath;
                    app.Mcp = McpPath;
                    app.Authorization(SeedWorkspaceModel);
                });
            },
            MapApplication = (app, context) =>
            {
                MapFirstPartyApi(app, context);
                app.MapPost(McpPath, (HttpContext http) => Results.Ok(DescribeValidatedToken(http)))
                    .RequireAuthorization(SqlOSJwtDefaults.McpPolicy);
            }
        },
        new HostProfile
        {
            Name = Dcr,
            DeploymentModel = "Dedicated authorization server for portable and compatibility clients: dynamic client registration (EnableChatGptCompatibility) plus explicitly configured client ID metadata documents for an MCP server deployed elsewhere.",
            Documentation = ["web/content/docs/authserver/dynamic-client-registration.mdx", "web/content/docs/guides/mcp-oauth.mdx", "web/content/docs/authserver/preregistration-vs-cimd-vs-dcr.mdx"],
            OptionsSummary =
            [
                "ConfigureApplication(\"Taskrail\", app => { Origin; Authorization(workspace model) })",
                "AuthServer.EnableChatGptCompatibility() (Dcr.Enabled = true, ResourceIndicators.Enabled = true)",
                "AuthServer.ClientRegistration.Cimd.Enabled = true, TrustedHosts = [client.example.test]",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                options.ConfigureApplication("Taskrail", app =>
                {
                    app.Origin = PublicOrigin;
                    app.Authorization(SeedWorkspaceModel);
                });
                options.AuthServer.EnableChatGptCompatibility();
                var cimd = options.AuthServer.ClientRegistration.Cimd;
                cimd.Enabled = true;
                cimd.TrustedHosts.Add(CimdClientHost);
            }
        },
        new HostProfile
        {
            Name = DashboardPassword,
            DeploymentModel = "Operator dashboard protected by Dashboard.AuthMode = Password: session cookie, login throttling, and same-origin proofs on cookie mutations.",
            Documentation = ["web/content/docs/guides/configuration.mdx", "web/content/docs/authserver/dashboard-overview.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "Dashboard.AuthMode = Password, Dashboard.Password = fixture password"
            ],
            OperatorAccess = OperatorAccess.Password,
            ConfigureSqlOS = (options, context) =>
            {
                ConfigureMinimalSingleApplication(options);
                options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
                options.Dashboard.Password = BehaviorLockConstants.DashboardPassword;
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = DashboardCallback,
            DeploymentModel = "Operator dashboard authorized by the host: Dashboard.AuthMode = DevelopmentOnly with Dashboard.AuthorizationCallback, in Production.",
            Documentation = ["web/content/docs/authserver/dashboard-overview.mdx", "web/content/docs/guides/production-readiness.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "Dashboard.AuthMode = DevelopmentOnly, Dashboard.AuthorizationCallback = operator header, environment Production"
            ],
            ConfigureSqlOS = (options, context) => ConfigureMinimalSingleApplication(options),
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = DashboardDevelopment,
            DeploymentModel = "Local development: Dashboard.AuthMode = DevelopmentOnly without a callback in the Development environment, so the dashboard and admin APIs are open.",
            Documentation = ["web/content/docs/getting-started.mdx", "web/content/docs/authserver/dashboard-overview.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "Dashboard.AuthMode = DevelopmentOnly (default), no AuthorizationCallback, environment Development"
            ],
            Environment = "Development",
            OperatorAccess = OperatorAccess.DevelopmentOpen,
            ConfigureSqlOS = (options, context) => ConfigureMinimalSingleApplication(options),
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = DashboardOff,
            DeploymentModel = "Default production deployment without operator access: Dashboard.AuthMode = DevelopmentOnly without a callback outside Development, so the dashboard and admin APIs answer 404.",
            Documentation = ["web/content/docs/guides/production-readiness.mdx", "web/content/docs/authserver/dashboard-overview.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "Dashboard.AuthMode = DevelopmentOnly (default), no AuthorizationCallback, environment Production"
            ],
            OperatorAccess = OperatorAccess.None,
            ConfigureSqlOS = (options, context) => ConfigureMinimalSingleApplication(options),
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = Enterprise,
            DeploymentModel = "B2B enterprise federation: organization SAML connections, SCIM directory sync, and the customer-managed SSO setup portal with DNS domain verification.",
            Documentation = ["web/content/docs/authserver/saml-sso.mdx", "web/content/docs/guides/scim-directory-sync.mdx", "web/content/docs/guides/customer-managed-sso.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "AuthServer.EnableSaml = true, AuthServer.EnableScim = true (ScimBasePath /sqlos/scim/v2)",
                "AuthServer.SsoPortal defaults (EnableApi, UseHostedPortal, RequireVerifiedDomainForActivation)",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                ConfigureMinimalSingleApplication(options);
                options.AuthServer.EnableSaml = true;
                options.AuthServer.EnableScim = true;
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = EnterpriseScimPath,
            DeploymentModel = "Enterprise federation with SCIM served outside the dashboard prefix (AuthServer.ScimBasePath = /scim/v2), the documented way to give directories a different public path.",
            Documentation = ["web/content/docs/guides/scim-directory-sync.mdx", "web/content/docs/reference/authserver-api.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "AuthServer.EnableSaml = true, AuthServer.EnableScim = true, AuthServer.ScimBasePath = /scim/v2",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                ConfigureMinimalSingleApplication(options);
                options.AuthServer.EnableSaml = true;
                options.AuthServer.EnableScim = true;
                options.AuthServer.ScimBasePath = "/scim/v2";
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = Modules,
            DeploymentModel = "Companion modules: calendar connections and admin, transactional email templates and admin, and the audit log admin API.",
            Documentation = ["web/content/docs/guides/calendar-integration.mdx", "web/content/docs/guides/transactional-email.mdx", "web/content/docs/guides/audit-logs.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "ConfigureCalendar(calendar => { Enabled = true; SyncScheduler.Enabled = false })",
                "ConfigureEmail(email => { AzureCommunicationServicesConnectionString = placeholder, FromAddress = no-reply@sqlos.example.test })",
                "AuthServer.SeedGoogleConnection / SeedMicrosoftConnection (calendar reuses them)",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                ConfigureMinimalSingleApplication(options);
                options.ConfigureEmail(email =>
                {
                    // A configured Azure Communication Services deployment. The host replaces the
                    // sender with a capturing fake, so the connection string is never used.
                    email.AzureCommunicationServicesConnectionString = "endpoint=https://behavior-lock.communication.azure.com/;accesskey=YmVoYXZpb3ItbG9jaw==";
                    email.FromAddress = "no-reply@sqlos.example.test";
                });
                options.AuthServer.SeedGoogleConnection("google-client-id", "google-client-secret", SocialCallbackUri);
                options.AuthServer.SeedMicrosoftConnection("microsoft-client-id", "microsoft-client-secret", tenant: null, SocialCallbackUri);
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = OAuthOnly,
            DeploymentModel = "OAuth 2.0 authorization server without the OpenID Provider role (OpenIdProvider.Enabled = false): no ID tokens, OIDC discovery, or UserInfo.",
            Documentation = ["web/content/docs/authserver/openid-provider.mdx"],
            OptionsSummary =
            [
                "UseSingleApplication(\"Behavior Lock\", app => { Origin, ClientId = \"behavior-lock-app\", Api = \"/api\", Authorization(workspace model) })",
                "AuthServer.OpenIdProvider.Enabled = false",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            ConfigureSqlOS = (options, context) =>
            {
                ConfigureMinimalSingleApplication(options);
                options.AuthServer.ConfigureOpenIdProvider(provider => provider.Enabled = false);
            },
            MapApplication = MapFirstPartyApi
        },
        new HostProfile
        {
            Name = LegacyHost,
            DeploymentModel = "Explicit host wiring without an application description: AddDbContext plus AddSqlOS<T>(options), a context that implements the SqlOS interfaces directly, SeedBrowserClient, and a manual (redundant, idempotent) MapAuthServer() call.",
            Documentation = ["web/content/docs/reference/hosting-api.mdx", "web/content/docs/authserver/clients.mdx"],
            OptionsSummary =
            [
                "services.AddDbContext<ManualBehaviorLockDbContext>(...); builder.AddSqlOS<ManualBehaviorLockDbContext>(options => ...)",
                "AuthServer.PublicOrigin, AuthServer.Issuer, AuthServer.SeedBrowserClient(\"legacy-web\", ...)",
                "AuthServer.SeedAuthPage(page => EnabledCredentialTypes = [password]), Fga.Seed(workspace model)",
                "Host: app.MapAuthServer()",
                "Dashboard.AuthorizationCallback = operator header"
            ],
            DbContextStyle = DbContextStyle.ManualInterfaces,
            OneCallRegistration = false,
            ConfigureSqlOS = (options, context) =>
            {
                options.AuthServer.PublicOrigin = PublicOrigin;
                options.AuthServer.Issuer = Issuer;
                options.AuthServer.SeedBrowserClient("legacy-web", "Legacy Web", "https://legacy.example.test/callback");
                options.AuthServer.SeedAuthPage(page => page.EnabledCredentialTypes = ["password"]);
                options.Fga.Seed(SeedWorkspaceModel);
            },
            MapApplication = (app, context) =>
            {
                // Redundant with AddSqlOS, which already maps the auth server; SqlOS withdraws its own
                // copy so no route is registered twice. This profile locks that legacy wiring.
                app.MapAuthServer();
            }
        }
    ];

    public static IReadOnlyList<HostProfile> All => Profiles;

    public static HostProfile Get(string name)
        => Profiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal))
           ?? throw new ArgumentException(
               $"Unknown behavior-lock profile '{name}'. Known profiles: {string.Join(", ", Profiles.Select(profile => profile.Name))}.",
               nameof(name));

    public static void SeedWorkspaceModel(SqlOSFgaSeedBuilder seed)
    {
        seed.ResourceType(BehaviorLockAuthorization.WorkspaceType, "Workspace", "Behavior-lock application workspace.");
        seed.Permission(BehaviorLockAuthorization.ReadPermission, "Read workspace", BehaviorLockAuthorization.WorkspaceType);
        seed.Permission(BehaviorLockAuthorization.WritePermission, "Write workspace", BehaviorLockAuthorization.WorkspaceType);
        seed.Role(BehaviorLockAuthorization.AdminRole, "Workspace admin")
            .Can(BehaviorLockAuthorization.ReadPermission, BehaviorLockAuthorization.WritePermission);
        seed.Role(BehaviorLockAuthorization.ReaderRole, "Workspace reader")
            .Can(BehaviorLockAuthorization.ReadPermission);
    }

    private static void ConfigureMinimalSingleApplication(SqlOSOptions options)
        => options.UseSingleApplication(ApplicationName, app =>
        {
            app.Origin = PublicOrigin;
            app.ClientId = AppClientId;
            app.Api = ApiPath;
            app.Authorization(SeedWorkspaceModel);
        });

    private static void ConfigurePhoneOtp(SqlOSAuthServerOptions auth)
        => auth.ConfigurePhoneOtp(phone =>
        {
            phone.Enabled = true;
            // Placeholder Twilio Verify settings: the host replaces the delivery channel with a
            // fake, so these values only satisfy PhoneOtp.IsConfigured.
            phone.TwilioAccountSid = "ACbehaviorlock";
            phone.TwilioAuthToken = "behavior-lock-twilio-token";
            phone.TwilioVerifyServiceSid = "VAbehaviorlock";
        });

    private static void SeedSocialConnections(SqlOSAuthServerOptions auth)
    {
        auth.SeedGoogleConnection("google-client-id", "google-client-secret", SocialCallbackUri);
        auth.SeedMicrosoftConnection("microsoft-client-id", "microsoft-client-secret", tenant: null, SocialCallbackUri);
        auth.SeedGitHubConnection("github-client-id", "github-client-secret", SocialCallbackUri);
        SeedCustomOidcConnection(auth);
    }

    private static void SeedCustomOidcConnection(SqlOSAuthServerOptions auth)
        => auth.SeedOidcConnection("custom", oidc =>
        {
            oidc.ProviderType = SqlOSOidcProviderType.Custom;
            oidc.DisplayName = "Example OIDC";
            oidc.ClientId = "custom-client-id";
            oidc.ClientSecret = "custom-client-secret";
            oidc.DiscoveryUrl = "https://oidc.example.local/.well-known/openid-configuration";
            oidc.AllowedCallbackUris = [SocialCallbackUri];
            oidc.ClaimMapping = new SqlOSOidcClaimMapping
            {
                SubjectClaim = "custom_sub",
                EmailClaim = "email_address",
                EmailVerifiedClaim = "email_verified_flag",
                DisplayNameClaim = "full_name"
            };
        });

    private static void MapFirstPartyApi(WebApplication app, HostProfileContext context)
    {
        var api = app.MapGroup(ApiPath).RequireAuthorization();
        api.MapGet("/me", (HttpContext http) => Results.Ok(DescribeValidatedToken(http)));
    }

    private static void AddResourceApiSchemes(WebApplicationBuilder builder, HostProfileContext context)
    {
        builder.Services.AddAuthentication()
            .AddJwtBearer(ResourceApiScheme, jwt =>
            {
                // The documented separate-resource-API setup: discovery and JWKS from the issuer,
                // no SqlOS session lookup (revoke-at-exp).
                jwt.Authority = Issuer;
                jwt.Audience = ResourceApiAudience;
                jwt.MapInboundClaims = false;
                if (context.Backchannel != null)
                {
                    jwt.BackchannelHttpHandler = context.Backchannel();
                }
            })
            .AddSqlOSJwt(BillingScheme, billing =>
            {
                billing.ExpectedAudience = BillingAudience;
                billing.Realm = "Billing API";
            });
        builder.Services.AddAuthorization(authorization =>
            authorization.AddPolicy(ResourceApiScheme, policy => policy
                .AddAuthenticationSchemes(ResourceApiScheme)
                .RequireAuthenticatedUser()));
    }

    private static void MapResourceApis(WebApplication app, HostProfileContext context)
    {
        app.MapGet("/resource-api/me", (HttpContext http) => Results.Ok(new
        {
            scheme = ResourceApiScheme,
            claims = http.User.Claims
                .Select(claim => new { type = claim.Type, value = claim.Value })
                .OrderBy(claim => claim.type, StringComparer.Ordinal)
                .ThenBy(claim => claim.value, StringComparer.Ordinal)
        })).RequireAuthorization(ResourceApiScheme);
        app.MapGet("/billing/me", (HttpContext http) => Results.Ok(DescribeValidatedToken(http)))
            .RequireAuthorization(BillingScheme);
    }

    /// <summary>The documented way a same-process route reads the SqlOS token it was authorized with.</summary>
    public static object DescribeValidatedToken(HttpContext http)
    {
        var token = http.GetSqlOSValidatedToken();
        return new
        {
            userId = token?.UserId,
            sessionId = token?.SessionId,
            organizationId = token?.OrganizationId,
            clientId = token?.ClientId,
            audience = token?.Audience,
            scope = token?.Scope,
            claims = http.User.Claims
                .Select(claim => new { type = claim.Type, value = claim.Value })
                .OrderBy(claim => claim.type, StringComparer.Ordinal)
                .ThenBy(claim => claim.value, StringComparer.Ordinal)
        };
    }
}
