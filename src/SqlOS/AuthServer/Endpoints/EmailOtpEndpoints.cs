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
    private static void MapEmailOtpEndpoints(RouteGroupBuilder auth, RouteGroupBuilder hostedForms, string authPrefix)
    {
        auth.MapGet("/login/email-otp", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var invitationToken = ReadInvitationToken(context);
            var deviceUserCode = ReadDeviceUserCode(context);
            var invitation = !string.IsNullOrWhiteSpace(invitationToken)
                ? await invitationService.ResolveEmailInvitationAsync(invitationToken, context, cancellationToken)
                : null;
            if (headlessAuthService.IsBrowserUiEnabled)
            {
                var uiContext = SqlOSHeadlessAuthService.ParseUiContext(context.Request.Query["ui_context"].ToString()) ?? new JsonObject();
                if (!string.IsNullOrWhiteSpace(invitationToken))
                {
                    uiContext["invitationToken"] = invitationToken;
                }
                if (!string.IsNullOrWhiteSpace(deviceUserCode))
                {
                    uiContext["deviceUserCode"] = deviceUserCode;
                }

                return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                    context,
                    "email-otp",
                    context.Request.Query["request"].ToString(),
                    invitation?.Email ?? context.Request.Query["email"].ToString(),
                    uiContext));
            }

            var page = await BuildAuthPageViewModelAsync(
                "email-otp",
                context.Request.Query["request"].ToString(),
                invitation?.Email ?? context.Request.Query["email"].ToString(),
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                invitationToken: invitationToken,
                invitation: invitation,
                deviceUserCode: deviceUserCode);
            return Html(page);
        });

        hostedForms.MapPost("/login/email-otp/start", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSEmailOtpService emailOtpService,
            SqlOSInvitationService invitationService,
            SqlOSHomeRealmDiscoveryService discoveryService,
            SqlOSSamlService samlService,
            ISqlOSAuthServerDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                email = invitation?.Email ?? email;
                var ssoRedirect = await RedirectToSsoIfRequiredAsync(
                    authorizationRequest,
                    email,
                    discoveryService,
                    samlService,
                    dbContext,
                    cancellationToken);
                if (ssoRedirect != null)
                {
                    return ssoRedirect;
                }

                var challenge = await emailOtpService.StartForAuthorizationRequestAsync(
                    authorizationRequest,
                    email,
                    context,
                    cancellationToken);

                var page = await BuildAuthPageViewModelAsync(
                    "email-otp-verify",
                    requestId,
                    email,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    info: challenge.Message,
                    challengeToken: challenge.ChallengeToken,
                    invitationToken: invitationToken,
                    invitation: invitation,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode);
                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "email-otp",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    invitationToken: invitationToken,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        hostedForms.MapPost("/login/email-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSEmailOtpService emailOtpService,
            SqlOSAuthService authService,
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var challengeToken = form["challengeToken"].ToString();
            var code = form["code"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                email = invitation?.Email ?? email;
                var verification = await emailOtpService.VerifyAsync(
                    new SqlOSEmailOtpVerifyRequest(challengeToken, code),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var organizationId = verification.Organizations.FirstOrDefault()?.Id;
                    if (!string.IsNullOrWhiteSpace(invitationToken))
                    {
                        var acceptance = await invitationService.AcceptEmailInvitationAsync(
                            new SqlOSAcceptEmailInvitationRequest(invitationToken, verification.User.Id),
                            context,
                            cancellationToken);
                        organizationId = acceptance.OrganizationId;
                    }

                    await authPageSessionService.SignInAsync(
                        context,
                        verification.User,
                        organizationId,
                        verification.AuthenticationMethod,
                        cancellationToken);
                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-in" : "invitation-accepted", deviceUserCode);
                }

                if (!string.Equals(verification.Challenge.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The sign-in code is invalid or expired.");
                }

                var completion = await authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                    authorizationRequest,
                    verification.User,
                    verification.AuthenticationMethod,
                    context,
                    cancellationToken);

                if (completion.RequiresOrganizationSelection)
                {
                    var organizationPage = await BuildAuthPageViewModelAsync(
                        "organization",
                        requestId,
                        verification.Challenge.Email,
                        null,
                        null,
                        completion.PendingToken,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        completion.Organizations,
                        invitationToken: invitationToken,
                        invitation: invitation,
                        invitationService: invitationService);
                    return Html(organizationPage);
                }

                if (completion.RequiresMfa)
                {
                    return await RenderMfaChallengeAsync(
                        completion,
                        requestId,
                        verification.Challenge.Email,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        cancellationToken,
                        invitationToken: invitationToken,
                        invitationService: invitationService);
                }

                return Results.Redirect(completion.RedirectUrl!);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "email-otp-verify",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    challengeToken: challengeToken,
                    invitationToken: invitationToken,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });
    }
}
