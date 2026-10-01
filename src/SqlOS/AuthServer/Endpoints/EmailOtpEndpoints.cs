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
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.AuthServer.Services;
using SqlOS.AuthServer.Security;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Domain;
using SqlOS.Hosting;

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
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
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
                if (await RouteToIdentityProviderAsync(processes, authorizationRequest, email, cancellationToken) is { } identityProvider)
                {
                    return identityProvider;
                }

                var outcome = await processes.StartEmailOtpSignIn().ExecuteAsync(
                    new StartEmailOtpSignInCommand(
                        email,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                var challenge = outcome switch
                {
                    EmailCodeStartOutcome.Sent sent => sent.Result,
                    EmailCodeStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                    _ => throw new InvalidOperationException($"Unknown email-code start outcome '{outcome.GetType().Name}'.")
                };

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
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
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
                var target = LoginTarget.ForBrowser(authorizationRequest, invitationToken);
                var outcome = await processes.VerifyEmailOtpSignIn(context).ExecuteAsync(
                    new VerifyEmailOtpSignInCommand(challengeToken, code, target, ChallengeBinding.For(target)),
                    cancellationToken);
                var signedIn = outcome switch
                {
                    EmailCodeSignInOutcome.SignedIn success => success,
                    EmailCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
                    _ => throw new InvalidOperationException($"Unknown email-code sign-in outcome '{outcome.GetType().Name}'.")
                };
                if (signedIn.Completion is LoginCompletion.BrowserSignedIn)
                {
                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-in" : "invitation-accepted", deviceUserCode);
                }

                var completion = ((LoginCompletion.AuthorizationRequestContinued)signedIn.Completion).Result;
                var challengeEmail = signedIn.Challenge.Email;
                if (completion.RequiresConsent)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "consent",
                        requestId,
                        challengeEmail,
                        null,
                        null,
                        null,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        consentToken: completion.ConsentToken,
                        consentScopes: completion.ConsentScopes));
                }

                if (completion.RequiresOrganizationSelection)
                {
                    var organizationPage = await BuildAuthPageViewModelAsync(
                        "organization",
                        requestId,
                        challengeEmail,
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
                        challengeEmail,
                        authPrefix,
                        authorizationServerService,
                        processes,
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
