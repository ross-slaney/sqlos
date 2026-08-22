using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private static void MapHostedConsentEndpoints(RouteGroupBuilder hostedForms, string authPrefix)
    {
        hostedForms.MapPost("/consent/approve", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var consentToken = form["consentToken"].ToString();

            try
            {
                var authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(
                    requestId ?? string.Empty,
                    cancellationToken);
                var completion = await authorizationServerService.ApproveConsentAsync(
                    consentToken,
                    authorizationRequest.Id,
                    context,
                    cancellationToken);
                var session = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
                return await RenderHostedAuthorizationCompletionAsync(
                    completion,
                    authorizationRequest,
                    session?.User.DefaultEmail ?? authorizationRequest.LoginHintEmail,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Html(await BuildAuthPageViewModelAsync(
                    "login",
                    requestId,
                    null,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken),
                    StatusCodes.Status400BadRequest);
            }
        });

        hostedForms.MapPost("/consent/deny", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var consentToken = form["consentToken"].ToString();

            try
            {
                var authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(
                    requestId ?? string.Empty,
                    cancellationToken);
                var errorRedirect = await authorizationServerService.DenyConsentAsync(
                    consentToken,
                    authorizationRequest.Id,
                    context,
                    cancellationToken);
                // Same CSP constraint as ClientRedirect: a POSTed deny cannot 302
                // cross-origin to the relying party's error redirect.
                return ClientRedirect(errorRedirect);
            }
            catch (InvalidOperationException ex)
            {
                return Html(await BuildAuthPageViewModelAsync(
                    "login",
                    requestId,
                    null,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken),
                    StatusCodes.Status400BadRequest);
            }
        });
    }
}
