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
    private static void MapProtocolEndpoints(RouteGroupBuilder auth, string authPrefix)
    {
        auth.MapGet("/.well-known/oauth-authorization-server", async (HttpContext context, SqlOSAuthorizationServerService authorizationServerService, CancellationToken cancellationToken) =>
            Results.Ok(await authorizationServerService.GetMetadataAsync(context, cancellationToken)));

        auth.MapPost("/device_authorization", async (
            HttpContext context,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);

            try
            {
                var result = await deviceAuthorizationService.StartAsync(
                    new SqlOSDeviceAuthorizationStartRequest(
                        form["client_id"].ToString(),
                        form["scope"].ToString(),
                        form["resource"].ToString()),
                    context,
                    cancellationToken);

                return Results.Ok(new
                {
                    device_code = result.DeviceCode,
                    user_code = result.UserCode,
                    verification_uri = result.VerificationUri,
                    verification_uri_complete = result.VerificationUriComplete,
                    expires_in = result.ExpiresIn,
                    interval = result.Interval
                });
            }
            catch (SqlOSDeviceAuthorizationException ex)
            {
                return Results.BadRequest(BuildDeviceAuthorizationError(ex));
            }
        });

        auth.MapGet("/.well-known/jwks.json", async (SqlOSCryptoService cryptoService, CancellationToken cancellationToken) =>
        {
            var keys = await cryptoService.GetValidationSigningKeysAsync(cancellationToken);
            return Results.Ok(cryptoService.GetJwksDocument(keys));
        });

        auth.MapGet("/continue", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var requestId = context.Request.Query["request"].ToString();
            var mfaToken = context.Request.Query["mfa_token"].ToString();
            var pendingToken = context.Request.Query["pending_token"].ToString();
            var authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);

            SqlOSAuthorizationRequestLoginResult completion;
            if (!string.IsNullOrWhiteSpace(mfaToken) && string.IsNullOrWhiteSpace(pendingToken))
            {
                var state = await authService.GetAuthorizationMfaChallengeStateAsync(
                    mfaToken,
                    authorizationRequest.Id,
                    cancellationToken);
                completion = new SqlOSAuthorizationRequestLoginResult(
                    null,
                    false,
                    null,
                    Array.Empty<SqlOSOrganizationOption>(),
                    RequiresMfa: true,
                    MfaToken: mfaToken,
                    RequiresMfaEnrollment: state.EnrollmentRequired,
                    MfaMethods: state.Methods,
                    AuthorizationRequestId: authorizationRequest.Id);
            }
            else if (!string.IsNullOrWhiteSpace(pendingToken) && string.IsNullOrWhiteSpace(mfaToken))
            {
                completion = await authorizationServerService.GetPendingOrganizationSelectionForLoginAsync(
                    pendingToken,
                    authorizationRequest.Id,
                    cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("Authorization continuation is invalid.");
            }

            if (headlessAuthService.IsBrowserUiEnabled && SqlOSHeadlessAuthService.IsHeadlessRequest(authorizationRequest))
            {
                return Results.Redirect(headlessAuthService.BuildUiUrl(
                    context,
                    authorizationRequest.Id,
                    completion.RequiresMfa
                        ? completion.RequiresMfaEnrollment ? "mfa-enroll" : "mfa"
                        : "organization",
                    error: null,
                    pendingToken: completion.PendingToken,
                    email: authorizationRequest.LoginHintEmail,
                    displayName: null,
                    uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson),
                    mfaToken: completion.MfaToken));
            }

            return await RenderHostedAuthorizationCompletionAsync(
                completion,
                authorizationRequest,
                authorizationRequest.LoginHintEmail,
                authPrefix,
                authorizationServerService,
                authService,
                cancellationToken);
        });

        auth.MapGet("/authorize", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSAuthService authService,
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var prompt = context.Request.Query["prompt"].ToString();
                var invitationToken = ReadInvitationToken(context);
                if (string.Equals(prompt, "login", StringComparison.Ordinal))
                {
                    await authPageSessionService.SignOutAsync(context, cancellationToken);
                }

                var authorizationRequest = await authorizationServerService.CreateAuthorizationRequestAsync(
                    new SqlOSAuthorizeRequestInput(
                        context.Request.Query["response_type"].ToString(),
                        context.Request.Query["client_id"].ToString(),
                        context.Request.Query["redirect_uri"].ToString(),
                        context.Request.Query["state"].ToString(),
                        context.Request.Query["scope"].ToString(),
                        context.Request.Query["code_challenge"].ToString(),
                        context.Request.Query["code_challenge_method"].ToString(),
                        context.Request.Query["resource"].ToString(),
                        context.Request.Query["login_hint"].ToString(),
                        prompt,
                        context.Request.Query["nonce"].ToString(),
                        headlessAuthService.IsBrowserUiEnabled ? "headless" : "hosted",
                        context.Request.Query["ui_context"].ToString()),
                    cancellationToken);
                SqlOSEmailInvitationResult? invitation = null;
                if (!string.IsNullOrWhiteSpace(invitationToken))
                {
                    invitation = await invitationService.BindInvitationToAuthorizationRequestAsync(invitationToken, authorizationRequest, cancellationToken);
                }

                var requestedView = context.Request.Query["view"].ToString().Trim().ToLowerInvariant() switch
                {
                    "invite" => "invite",
                    "login" => "login",
                    "signup" => "signup",
                    "password" => "password",
                    "forgot-password" => "forgot-password",
                    "email-otp" => "email-otp",
                    "magic-link" => "magic-link",
                    "phone-otp" => "phone-otp",
                    "phone-otp-signup" => "phone-otp-signup",
                    _ when invitation != null => "invite",
                    _ => "login"
                };

                var existingSession = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
                if (existingSession != null && !string.Equals(prompt, "login", StringComparison.Ordinal))
                {
                    if (authorizationRequest.ClientApplication?.IsFirstParty != true)
                    {
                        if (string.Equals(prompt, "none", StringComparison.Ordinal))
                        {
                            return Results.Redirect(await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
                                authorizationRequest,
                                "consent_required",
                                "User interaction is required for this client.",
                                cancellationToken));
                        }
                    }
                    else
                    {
                        var completion = await authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                            authorizationRequest,
                            existingSession.User,
                            existingSession.AuthenticationMethod,
                            context,
                            cancellationToken);
                        if ((completion.RequiresOrganizationSelection || completion.RequiresMfa)
                            && string.Equals(prompt, "none", StringComparison.Ordinal))
                        {
                            await authorizationServerService.CancelAuthorizationInteractionAsync(
                                completion,
                                cancellationToken);
                            return Results.Redirect(await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
                                authorizationRequest,
                                "interaction_required",
                                "Additional user interaction is required.",
                                cancellationToken));
                        }

                        if (completion.RequiresOrganizationSelection)
                        {
                            if (headlessAuthService.IsBrowserUiEnabled)
                            {
                                return Results.Redirect(headlessAuthService.BuildUiUrl(
                                    context,
                                    authorizationRequest.Id,
                                    "organization",
                                    error: null,
                                    pendingToken: completion.PendingToken,
                                    email: existingSession.User.DefaultEmail,
                                    displayName: null,
                                    uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson)));
                            }

                            return Html(await BuildAuthPageViewModelAsync(
                                "organization",
                                authorizationRequest.Id,
                                existingSession.User.DefaultEmail,
                                error: null,
                                displayName: null,
                                pendingToken: completion.PendingToken,
                                authPrefix,
                                authorizationServerService,
                                cancellationToken,
                                organizationSelection: completion.Organizations));
                        }

                        if (completion.RequiresMfa)
                        {
                            if (headlessAuthService.IsBrowserUiEnabled)
                            {
                                return Results.Redirect(headlessAuthService.BuildUiUrl(
                                    context,
                                    authorizationRequest.Id,
                                    completion.RequiresMfaEnrollment ? "mfa-enroll" : "mfa",
                                    error: null,
                                    pendingToken: null,
                                    email: existingSession.User.DefaultEmail,
                                    displayName: null,
                                    uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson),
                                    mfaToken: completion.MfaToken));
                            }

                            return await RenderMfaChallengeAsync(
                                completion,
                                authorizationRequest.Id,
                                existingSession.User.DefaultEmail,
                                authPrefix,
                                authorizationServerService,
                                authService,
                                cancellationToken);
                        }

                        return Results.Redirect(completion.RedirectUrl!);
                    }
                }

                if (string.Equals(prompt, "none", StringComparison.Ordinal))
                {
                    return Results.Redirect(await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
                        authorizationRequest,
                        "login_required",
                        "The user is not signed in.",
                        cancellationToken));
                }

                if (headlessAuthService.IsBrowserUiEnabled)
                {
                    return Results.Redirect(headlessAuthService.BuildUiUrl(
                        context,
                        authorizationRequest.Id,
                        requestedView,
                        error: null,
                        pendingToken: null,
                        email: invitation?.Email ?? authorizationRequest.LoginHintEmail,
                        displayName: null,
                        uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson)));
                }

                var page = await BuildAuthPageViewModelAsync(
                    requestedView,
                    authorizationRequest.Id,
                    invitation?.Email ?? authorizationRequest.LoginHintEmail,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    invitationToken: invitationToken,
                    invitation: invitation);

                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                var mapped = await MapPublicAuthErrorAsync(
                    context,
                    ex,
                    SqlOSPublicAuthErrorSurface.OAuthAuthorize,
                    cancellationToken);
                if (headlessAuthService.IsBrowserUiEnabled)
                {
                    return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                        context,
                        "login",
                        requestId: null,
                        email: context.Request.Query["login_hint"].ToString(),
                        uiContext: SqlOSHeadlessAuthService.ParseUiContext(context.Request.Query["ui_context"].ToString()))
                        + $"&error={Uri.EscapeDataString(mapped.PublicMessage)}");
                }

                var page = await BuildAuthPageViewModelAsync(
                    "login",
                    null,
                    context.Request.Query["login_hint"].ToString(),
                    mapped.PublicMessage,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });
    }
}
