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
    private static void MapSsoPortalEndpoints(
        RouteGroupBuilder adminApi,
        RouteGroupBuilder portal,
        RouteGroupBuilder setupApi)
    {
        adminApi.MapGet("/organizations/{organizationId}/sso-portal/sessions", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSSsoPortalService portalService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await portalService.ListOrganizationSessionsAsync(organizationId, page, pageSize, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        adminApi.MapPost("/sso-portal/sessions", async (HttpContext context, SqlOSCreateSsoPortalSessionRequest request, SqlOSSsoPortalService portalService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await portalService.CreateSessionAsync(request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        adminApi.MapPost("/organizations/{organizationId}/sso-portal/sessions", async (HttpContext context, string organizationId, SqlOSCreateSsoPortalSessionRequest request, SqlOSSsoPortalService portalService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var effectiveRequest = request with { OrganizationId = organizationId };
                return Results.Ok(await portalService.CreateSessionAsync(effectiveRequest, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        adminApi.MapPost("/sso-portal/sessions/{sessionId}/revoke", async (HttpContext context, string sessionId, SqlOSRevokeSsoPortalSessionRequest request, SqlOSSsoPortalService portalService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await portalService.RevokeSessionAsync(sessionId, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        portal.MapGet("/start", async (HttpContext context, string? token, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            try
            {
                var opened = await portalService.OpenSessionAsync(token ?? string.Empty, context, cancellationToken);
                var setupUiUrl = portalService.TryBuildSetupUiUrl(context, opened.Id, opened.OrganizationId, "provider");
                if (!string.IsNullOrWhiteSpace(setupUiUrl))
                {
                    return Results.Redirect(setupUiUrl);
                }

                if (!portalService.IsHostedPortalEnabled)
                {
                    return Results.Json(
                        new { message = "Hosted SSO setup portal is disabled. Configure SsoPortal.BuildUiUrl for browser handoff." },
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Redirect(portalService.BuildPortalUrl(context));
            }
            catch (InvalidOperationException ex)
            {
                return HostedHtml(SqlOSSsoPortalPageRenderer.RenderStartError(ex.Message), StatusCodes.Status400BadRequest);
            }
        });

        portal.MapGet("", (SqlOSSsoPortalService portalService) => portalService.IsHostedPortalEnabled
            ? HostedHtml(SqlOSSsoPortalPageRenderer.RenderShell())
            : Results.NotFound());

        var api = portal.MapGroup("/api");

        api.MapGet("/state", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.GetStateAsync(session, cancellationToken));
        });

        api.MapPut("/provider", async (HttpContext context, SqlOSUpdateSsoPortalProviderRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.SetProviderAsync(session, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPut("/enrollment-policy", async (HttpContext context, SqlOSSsoPortalEnrollmentPolicyRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.UpdateEnrollmentPolicyAsync(session, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/domain", async (HttpContext context, SqlOSSsoPortalDomainRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.StartDomainVerificationAsync(session, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/domains/{domainId}/confirm", async (HttpContext context, string domainId, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.ConfirmDomainOwnershipAsync(session, domainId, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/metadata/validate", async (HttpContext context, SqlOSSsoPortalMetadataRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(portalService.ValidateMetadata(request));
        });

        api.MapPost("/metadata", async (HttpContext context, SqlOSSsoPortalMetadataRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.ImportMetadataAsync(session, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/activate", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.ActivateAsync(session, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/disable", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.DisableAsync(session, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/organization-sessions/revoke", async (HttpContext context, SqlOSSsoPortalRevokeOrganizationSessionsRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.RevokeOrganizationSessionsAsync(session, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/test", async (HttpContext context, SqlOSSsoPortalTestRequest request, SqlOSSsoPortalService portalService, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                var state = await portalService.GetStateAsync(session, cancellationToken);
                if (!state.Connection.IsEnabled)
                {
                    return Results.Ok(await portalService.RecordTestAsync(
                        session,
                        "blocked",
                        "Activate the SSO connection before starting a test sign-in.",
                        null,
                        context,
                        cancellationToken));
                }

                string? authorizationUrl = null;
                if (!string.IsNullOrWhiteSpace(request.ClientId) && !string.IsNullOrWhiteSpace(request.RedirectUri))
                {
                    authorizationUrl = await samlService.CreateAuthorizationUrlAsync(
                        new SqlOSAuthorizationUrlRequest(
                            state.Connection.Id,
                            request.ClientId,
                            request.RedirectUri,
                            request.State ?? string.Empty,
                            request.CodeChallenge ?? string.Empty,
                            request.CodeChallengeMethod ?? string.Empty),
                        cancellationToken);
                }

                return Results.Ok(await portalService.RecordTestAsync(
                    session,
                    authorizationUrl == null ? "ready" : "started",
                    authorizationUrl == null
                        ? "Connection is active and ready for a SAML sign-in test."
                        : "SAML sign-in test redirect created.",
                    authorizationUrl,
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/signout", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            await portalService.SignOutAsync(context, cancellationToken);
            return Results.NoContent();
        });

        setupApi.MapGet("", async (HttpContext context, string? view, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.GetSetupActionAsync(session, view, cancellationToken));
        });

        setupApi.MapPut("/provider", async (HttpContext context, SqlOSUpdateSsoPortalProviderRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.SetProviderActionAsync(session, request, context, cancellationToken));
        });

        setupApi.MapPut("/enrollment-policy", async (HttpContext context, SqlOSSsoPortalEnrollmentPolicyRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.UpdateEnrollmentPolicyActionAsync(session, request, context, cancellationToken));
        });

        setupApi.MapPost("/domain", async (HttpContext context, SqlOSSsoPortalDomainRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.StartDomainVerificationActionAsync(session, request, context, cancellationToken));
        });

        setupApi.MapPost("/domains/{domainId}/confirm", async (HttpContext context, string domainId, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.ConfirmDomainOwnershipActionAsync(session, domainId, context, cancellationToken));
        });

        setupApi.MapPost("/metadata/validate", async (HttpContext context, SqlOSSsoPortalMetadataRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(portalService.ValidateMetadata(request));
        });

        setupApi.MapPost("/metadata", async (HttpContext context, SqlOSSsoPortalMetadataRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.ImportMetadataActionAsync(session, request, context, cancellationToken));
        });

        setupApi.MapPost("/activate", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.ActivateActionAsync(session, context, cancellationToken));
        });

        setupApi.MapPost("/disable", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.DisableActionAsync(session, context, cancellationToken));
        });

        setupApi.MapPost("/organization-sessions/revoke", async (HttpContext context, SqlOSSsoPortalRevokeOrganizationSessionsRequest request, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                return Results.Ok(await portalService.RevokeOrganizationSessionsAsync(session, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        setupApi.MapPost("/test", async (HttpContext context, SqlOSSsoPortalTestRequest request, SqlOSSsoPortalService portalService, SqlOSSamlService samlService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            var session = await portalService.TryGetSessionAsync(context, cancellationToken);
            return session == null
                ? Results.Json(new { message = "Portal session is invalid or expired." }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(await portalService.RecordTestActionAsync(session, request, samlService, context, cancellationToken));
        });

        setupApi.MapPost("/signout", async (HttpContext context, SqlOSSsoPortalService portalService, CancellationToken cancellationToken) =>
        {
            if (!portalService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            await portalService.SignOutAsync(context, cancellationToken);
            return Results.Ok(new SqlOSSsoSetupActionResult("redirect", null, null));
        });
    }
}
