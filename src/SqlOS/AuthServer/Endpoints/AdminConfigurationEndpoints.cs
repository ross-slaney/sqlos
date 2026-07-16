using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.AuthServer.Security;
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private static void MapAdminConfigurationEndpoints(RouteGroupBuilder api)
    {
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
                request.ApplePrivateKeyPem,
                request.LogoDataUrl), cancellationToken);
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
                request.ApplePrivateKeyPem,
                request.LogoDataUrl), cancellationToken);
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

        api.MapGet("/settings/mfa", async (HttpContext context, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetMfaSettingsAsync(cancellationToken));
        });

        api.MapPut("/settings/mfa", async (HttpContext context, SqlOSUpdateMfaSettingsRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateMfaSettingsAsync(request, cancellationToken));
        });

        api.MapGet("/organizations/{organizationId}/mfa-policy", async (HttpContext context, string organizationId, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetOrganizationMfaPolicyAsync(organizationId, cancellationToken));
        });

        api.MapPut("/organizations/{organizationId}/mfa-policy", async (HttpContext context, string organizationId, SqlOSUpdateOrganizationMfaPolicyRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateOrganizationMfaPolicyAsync(organizationId, request, cancellationToken));
        });

        api.MapGet("/signing-keys", async (HttpContext context, SqlOSCryptoService cryptoService, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var keys = await cryptoService.ListSigningKeysAsync(cancellationToken);
            var rotationSettings = await settingsService.GetKeyRotationSettingsAsync(cancellationToken);
            var activeKey = keys.FirstOrDefault(k => k.IsActive);

            return Results.Ok(new
            {
                keys = keys.Select(k => new
                {
                    k.Id,
                    k.Kid,
                    k.Algorithm,
                    k.IsActive,
                    k.ActivatedAt,
                    k.RetiredAt,
                    ageDays = Math.Round((DateTime.UtcNow - k.ActivatedAt).TotalDays, 1)
                }),
                rotationIntervalDays = rotationSettings.RotationInterval.TotalDays,
                graceWindowDays = rotationSettings.GraceWindow.TotalDays,
                nextRotationDue = activeKey != null
                    ? activeKey.ActivatedAt.Add(rotationSettings.RotationInterval)
                    : (DateTime?)null
            });
        });

        api.MapPost("/signing-keys/rotate", async (HttpContext context, SqlOSCryptoService cryptoService, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            var newKey = await cryptoService.RotateSigningKeyAsync(cancellationToken);
            await adminService.RecordAuditAsync(
                "signing_key_rotated_manual",
                "admin",
                "dashboard",
                data: new { newKeyId = newKey.Id, newKid = newKey.Kid },
                cancellationToken: cancellationToken);

            return Results.Ok(new
            {
                newKey.Id,
                newKey.Kid,
                newKey.Algorithm,
                newKey.ActivatedAt
            });
        });

        api.MapGet("/settings/auth-page", async (HttpContext context, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetAuthPageSettingsAsync(cancellationToken));
        });

        api.MapPut("/settings/auth-page", async (HttpContext context, SqlOSUpdateAuthPageSettingsRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateAuthPageSettingsAsync(request, cancellationToken));
        });

        api.MapGet("/settings/email", async (HttpContext context, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.GetAuthEmailBrandingSettingsAsync(cancellationToken));
        });

        api.MapPut("/settings/email", async (HttpContext context, SqlOSUpdateAuthEmailBrandingSettingsRequest request, SqlOSSettingsService settingsService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await settingsService.UpdateAuthEmailBrandingSettingsAsync(request, cancellationToken));
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
}
