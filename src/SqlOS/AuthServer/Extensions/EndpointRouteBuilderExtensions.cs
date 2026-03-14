using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Extensions;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthServer(this IEndpointRouteBuilder endpoints, string? pathPrefix = null)
    {
        var authPrefix = (pathPrefix ?? "/sqlos/auth").TrimEnd('/');
        var adminPrefix = authPrefix.EndsWith("/auth", StringComparison.OrdinalIgnoreCase)
            ? $"{authPrefix[..^5]}/admin/auth"
            : $"{authPrefix}/admin";
        var legacySamlPrefix = authPrefix.EndsWith("/auth", StringComparison.OrdinalIgnoreCase)
            ? authPrefix[..^5]
            : null;

        var auth = endpoints.MapGroup(authPrefix);
        var adminRoot = endpoints.MapGroup(adminPrefix);
        var adminApi = adminRoot.MapGroup("/api");

        auth.MapGet("/.well-known/jwks.json", async (SqlOSCryptoService cryptoService, CancellationToken cancellationToken) =>
        {
            var keys = await cryptoService.GetValidationSigningKeysAsync(cancellationToken);
            return Results.Ok(cryptoService.GetJwksDocument(keys));
        });

        auth.MapPost("/signup", async (SqlOSSignupRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.SignUpAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/password/login", async (SqlOSPasswordLoginRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.LoginWithPasswordAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/select-organization", async (SqlOSSelectOrganizationRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.SelectOrganizationAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/token/exchange", async (SqlOSExchangeCodeRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.ExchangeCodeAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/token/refresh", async (SqlOSRefreshRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RefreshAsync(request, cancellationToken)));

        auth.MapPost("/logout", async (HttpContext context, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            var request = await context.Request.ReadFromJsonAsync<LogoutRequest>(cancellationToken: cancellationToken) ?? new LogoutRequest(null, null);
            await authService.LogoutAsync(request.RefreshToken, request.SessionId, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/logout-all", async (LogoutAllRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.LogoutAllAsync(request.UserId, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/password/forgot", async (SqlOSForgotPasswordRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(new { token = await authService.CreatePasswordResetTokenAsync(request, cancellationToken) }));

        auth.MapPost("/password/reset", async (SqlOSResetPasswordRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.ResetPasswordAsync(request, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/email/verification-token", async (SqlOSCreateVerificationTokenRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(new { token = await authService.CreateEmailVerificationTokenAsync(request, cancellationToken) }));

        auth.MapPost("/email/verify", async (SqlOSVerifyEmailRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.VerifyEmailAsync(request, cancellationToken);
            return Results.NoContent();
        });

        auth.MapGet("/oidc/providers", async (SqlOSOidcAuthService oidcAuthService, CancellationToken cancellationToken) =>
            Results.Ok(await oidcAuthService.ListEnabledProvidersAsync(cancellationToken)));

        auth.MapPost("/oidc/authorization-url", async (SqlOSOidcAuthorizationUrlRequest request, SqlOSOidcBrowserAuthService oidcBrowserAuthService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await oidcBrowserAuthService.CreateAuthorizationUrlAsync(request, httpContext, cancellationToken)));

        auth.MapMethods("/oidc/callback", ["GET", "POST"], async (SqlOSOidcBrowserAuthService oidcBrowserAuthService, HttpContext httpContext, CancellationToken cancellationToken) =>
            await oidcBrowserAuthService.HandleCallbackAsync(httpContext, cancellationToken));

        auth.MapPost("/oidc/exchange", async (SqlOSPkceExchangeRequest request, SqlOSOidcBrowserAuthService oidcBrowserAuthService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await oidcBrowserAuthService.ExchangeCodeAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/sso/authorization-url", async (SqlOSAuthorizationUrlRequest request, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
            Results.Ok(new { authorizationUrl = await samlService.CreateAuthorizationUrlAsync(request, cancellationToken) }));

        auth.MapGet("/saml/login/{connectionId}", async (string connectionId, string requestToken, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
            Results.Redirect(await samlService.BuildIdentityProviderRedirectAsync(connectionId, requestToken, cancellationToken)));

        static async Task<IResult> HandleSamlAcsAsync(string connectionId, HttpContext httpContext, SqlOSSamlService samlService, CancellationToken cancellationToken)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var samlResponse = form["SAMLResponse"].ToString();
            var relayState = form["RelayState"].ToString();
            if (string.IsNullOrWhiteSpace(samlResponse) || string.IsNullOrWhiteSpace(relayState))
            {
                return Results.BadRequest(new { error = "SAMLResponse and RelayState are required." });
            }

            var redirectUrl = await samlService.HandleAcsAsync(connectionId, samlResponse, relayState, cancellationToken);
            return Results.Redirect(redirectUrl);
        }

        auth.MapPost("/saml/acs/{connectionId}", HandleSamlAcsAsync);
        if (!string.IsNullOrWhiteSpace(legacySamlPrefix))
        {
            endpoints.MapPost($"{legacySamlPrefix}/saml/acs/{{connectionId}}", HandleSamlAcsAsync);
        }

        adminApi.MapGet("/stats", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            var authorized = await IsAdminAuthorizedAsync(context, options.Value, environment);
            if (!authorized)
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetDashboardSummaryAsync(cancellationToken));
        });

        MapAdminEndpoints(adminApi);
        return endpoints;
    }

    private static void MapAdminEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/users", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUsersAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}", async (HttpContext context, string userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetUserAsync(userId, cancellationToken));
        });

        api.MapGet("/users/{userId}/memberships", async (HttpContext context, string userId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUserMembershipsAsync(userId, page, pageSize, cancellationToken));
        });

        api.MapGet("/users/{userId}/sessions", async (HttpContext context, string userId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListUserSessionsAsync(userId, page, pageSize, cancellationToken));
        });

        api.MapPost("/users", async (HttpContext context, SqlOSCreateUserRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var user = await adminService.CreateUserAsync(request, cancellationToken);
            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.DefaultEmail,
                user.IsActive,
                user.CreatedAt,
                user.UpdatedAt
            });
        });

        api.MapGet("/organizations", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}", async (HttpContext context, string organizationId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.GetOrganizationAsync(organizationId, cancellationToken));
        });

        api.MapPost("/organizations", async (HttpContext context, SqlOSCreateOrganizationRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var organization = await adminService.CreateOrganizationAsync(request, cancellationToken);
            return Results.Ok(new
            {
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                organization.IsActive,
                organization.CreatedAt
            });
        });

        api.MapPut("/organizations/{organizationId}", async (HttpContext context, string organizationId, SqlOSUpdateOrganizationRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var organization = await adminService.UpdateOrganizationAsync(organizationId, request, cancellationToken);
            return Results.Ok(new
            {
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain,
                organization.IsActive,
                organization.CreatedAt
            });
        });

        api.MapGet("/memberships", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListMembershipsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationMembershipsAsync(organizationId, page, pageSize, cancellationToken));
        });

        api.MapPost("/memberships", async (HttpContext context, CreateMembershipRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var membership = await adminService.CreateMembershipAsync(request.OrganizationId, new SqlOSCreateMembershipRequest(request.UserId, request.Role), cancellationToken);
            return Results.Ok(new
            {
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                membership.CreatedAt
            });
        });

        api.MapPost("/organizations/{organizationId}/memberships", async (HttpContext context, string organizationId, SqlOSCreateMembershipRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var membership = await adminService.CreateMembershipAsync(organizationId, request, cancellationToken);
            return Results.Ok(new
            {
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                membership.CreatedAt
            });
        });

        api.MapGet("/clients", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListClientsAsync(cancellationToken));
        });

        api.MapPost("/clients", async (HttpContext context, SqlOSCreateClientRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var client = await adminService.CreateClientAsync(request, cancellationToken);
            return Results.Ok(new
            {
                client.Id,
                client.ClientId,
                client.Name,
                client.Audience,
                RedirectUris = client.RedirectUrisJson,
                client.IsActive,
                client.CreatedAt
            });
        });

        api.MapGet("/oidc-connections", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOidcConnectionsAsync(cancellationToken));
        });

        api.MapPost("/oidc-connections", async (HttpContext context, CreateOidcConnectionRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            if (!Enum.TryParse<SqlOSOidcProviderType>(request.ProviderType, ignoreCase: true, out var providerType))
            {
                return Results.BadRequest(new { message = $"Unsupported OIDC provider '{request.ProviderType}'." });
            }

            if (!TryParseClientAuthMethod(request.ClientAuthMethod, out var clientAuthMethod))
            {
                return Results.BadRequest(new { message = $"Unsupported OIDC client auth method '{request.ClientAuthMethod}'." });
            }

            var connection = await adminService.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
                providerType,
                request.DisplayName,
                request.ClientId,
                request.ClientSecret,
                request.AllowedCallbackUris,
                request.UseDiscovery,
                request.DiscoveryUrl,
                request.Issuer,
                request.AuthorizationEndpoint,
                request.TokenEndpoint,
                request.UserInfoEndpoint,
                request.JwksUri,
                request.MicrosoftTenant,
                request.Scopes,
                request.ClaimMapping,
                clientAuthMethod,
                request.UseUserInfo,
                request.AppleTeamId,
                request.AppleKeyId,
                request.ApplePrivateKeyPem), cancellationToken);
            return Results.Ok(ToOidcConnectionResponse(connection));
        });

        api.MapPut("/oidc-connections/{connectionId}", async (HttpContext context, string connectionId, UpdateOidcConnectionRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            if (!TryParseClientAuthMethod(request.ClientAuthMethod, out var clientAuthMethod))
            {
                return Results.BadRequest(new { message = $"Unsupported OIDC client auth method '{request.ClientAuthMethod}'." });
            }

            var connection = await adminService.UpdateOidcConnectionAsync(connectionId, new SqlOSUpdateOidcConnectionRequest(
                request.DisplayName,
                request.ClientId,
                request.ClientSecret,
                request.AllowedCallbackUris,
                request.UseDiscovery,
                request.DiscoveryUrl,
                request.Issuer,
                request.AuthorizationEndpoint,
                request.TokenEndpoint,
                request.UserInfoEndpoint,
                request.JwksUri,
                request.MicrosoftTenant,
                request.Scopes,
                request.ClaimMapping,
                clientAuthMethod,
                request.UseUserInfo,
                request.AppleTeamId,
                request.AppleKeyId,
                request.ApplePrivateKeyPem), cancellationToken);
            return Results.Ok(ToOidcConnectionResponse(connection));
        });

        api.MapPost("/oidc-connections/{connectionId}/enable", async (HttpContext context, string connectionId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.SetOidcConnectionEnabledAsync(connectionId, true, cancellationToken);
            return Results.Ok(new { connection.Id, connection.IsEnabled, connection.UpdatedAt });
        });

        api.MapPost("/oidc-connections/{connectionId}/disable", async (HttpContext context, string connectionId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.SetOidcConnectionEnabledAsync(connectionId, false, cancellationToken);
            return Results.Ok(new { connection.Id, connection.IsEnabled, connection.UpdatedAt });
        });

        api.MapGet("/sso-connections", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListSsoConnectionsAsync(cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/sso-connections", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListOrganizationSsoConnectionsAsync(organizationId, page, pageSize, cancellationToken));
        });

        api.MapPost("/sso-connections/draft", async (HttpContext context, SqlOSCreateSsoConnectionDraftRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.CreateSsoConnectionDraftAsync(request, cancellationToken);
            return Results.Ok(new
            {
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                ServiceProviderEntityId = adminService.GetServiceProviderEntityId(),
                AssertionConsumerServiceUrl = adminService.GetAssertionConsumerServiceUrl(connection.Id)
            });
        });

        api.MapPost("/sso-connections/{connectionId}/metadata", async (HttpContext context, string connectionId, SqlOSImportSsoMetadataRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.ImportSsoMetadataAsync(connectionId, request, cancellationToken);
            return Results.Ok(new
            {
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                connection.IdentityProviderEntityId,
                connection.SingleSignOnUrl,
                connection.UpdatedAt
            });
        });

        api.MapPost("/sso-connections", async (HttpContext context, SqlOSCreateSsoConnectionRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var connection = await adminService.CreateSsoConnectionAsync(request, cancellationToken);
            return Results.Ok(new
            {
                connection.Id,
                connection.OrganizationId,
                connection.DisplayName,
                connection.IsEnabled,
                connection.IdentityProviderEntityId,
                connection.SingleSignOnUrl,
                connection.AutoProvisionUsers,
                connection.AutoLinkByEmail,
                connection.CreatedAt,
                connection.UpdatedAt
            });
        });

        api.MapGet("/settings/security", async (HttpContext context, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetSecuritySettingsAsync(cancellationToken));
        });

        api.MapPut("/settings/security", async (HttpContext context, SqlOSUpdateSecuritySettingsRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateSecuritySettingsAsync(request, cancellationToken));
        });

        api.MapGet("/sessions", async (HttpContext context, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListSessionsAsync(page, pageSize, cancellationToken));
        });

        api.MapGet("/audit-events", async (HttpContext context, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListAuditEventsAsync(cancellationToken));
        });
    }

    private static async Task<bool> IsAdminAuthorizedAsync(HttpContext context, SqlOSAuthServerOptions options, IHostEnvironment environment)
    {
        if (options.Dashboard.AuthMode == SqlOSDashboardAuthMode.Password)
        {
            var sessionService = context.RequestServices.GetService<SqlOSDashboardSessionService>();
            if (sessionService == null || !sessionService.HasActiveSession(context))
            {
                return false;
            }

            if (options.Dashboard.AuthorizationCallback != null)
            {
                return await options.Dashboard.AuthorizationCallback(context);
            }

            return true;
        }

        if (options.Dashboard.AuthorizationCallback != null)
        {
            return await options.Dashboard.AuthorizationCallback(context);
        }

        return environment.IsDevelopment();
    }

    private sealed record LogoutRequest(string? RefreshToken, string? SessionId);
    private sealed record LogoutAllRequest(string UserId);
    private sealed record CreateMembershipRequest(string OrganizationId, string UserId, string Role);
    private static bool TryParseClientAuthMethod(string? value, out SqlOSOidcClientAuthMethod? method)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            method = null;
            return true;
        }

        if (Enum.TryParse<SqlOSOidcClientAuthMethod>(value, ignoreCase: true, out var parsed))
        {
            method = parsed;
            return true;
        }

        method = null;
        return false;
    }

    private static object ToOidcConnectionResponse(SqlOSOidcConnection connection) => new
    {
        connection.Id,
        ProviderType = connection.ProviderType.ToString(),
        connection.DisplayName,
        connection.ClientId,
        AllowedCallbackUris = connection.AllowedCallbackUrisJson,
        connection.UseDiscovery,
        connection.DiscoveryUrl,
        connection.Issuer,
        connection.AuthorizationEndpoint,
        connection.TokenEndpoint,
        connection.UserInfoEndpoint,
        connection.JwksUri,
        connection.MicrosoftTenant,
        Scopes = connection.ScopesJson,
        ClaimMapping = connection.ClaimMappingJson,
        ClientAuthMethod = connection.ClientAuthMethod.ToString(),
        connection.UseUserInfo,
        connection.AppleTeamId,
        connection.AppleKeyId,
        connection.IsEnabled,
        connection.CreatedAt,
        connection.UpdatedAt
    };

    private sealed record CreateOidcConnectionRequest(
        string ProviderType,
        string DisplayName,
        string ClientId,
        string? ClientSecret,
        List<string> AllowedCallbackUris,
        bool UseDiscovery,
        string? DiscoveryUrl,
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        string? MicrosoftTenant,
        List<string>? Scopes,
        SqlOSOidcClaimMapping? ClaimMapping,
        string? ClientAuthMethod,
        bool? UseUserInfo,
        string? AppleTeamId,
        string? AppleKeyId,
        string? ApplePrivateKeyPem);

    private sealed record UpdateOidcConnectionRequest(
        string DisplayName,
        string ClientId,
        string? ClientSecret,
        List<string> AllowedCallbackUris,
        bool UseDiscovery,
        string? DiscoveryUrl,
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        string? MicrosoftTenant,
        List<string>? Scopes,
        SqlOSOidcClaimMapping? ClaimMapping,
        string? ClientAuthMethod,
        bool? UseUserInfo,
        string? AppleTeamId,
        string? AppleKeyId,
        string? ApplePrivateKeyPem);
}
