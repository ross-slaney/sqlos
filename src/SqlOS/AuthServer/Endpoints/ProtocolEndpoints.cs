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
            try
            {
                var requestId = context.Request.Query["request"].ToString();
                // The continuation cookie name is derived per authorization request, so two
                // flows racing in separate tabs each resolve their own handle.
                var continuationHandle = context.Request.Cookies[
                    SqlOSAuthorizationServerService.BuildContinuationCookieName(requestId)];
                if (string.IsNullOrWhiteSpace(continuationHandle))
                {
                    throw new InvalidOperationException("Authorization continuation is invalid or expired.");
                }

                var authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
                var completion = await authorizationServerService.ResolveAuthorizationContinuationAsync(
                    authorizationRequest.Id,
                    continuationHandle,
                    cancellationToken);

                if (headlessAuthService.IsBrowserUiEnabled && SqlOSHeadlessAuthService.IsHeadlessRequest(authorizationRequest))
                {
                    return Results.Redirect(headlessAuthService.BuildUiUrl(
                        context,
                        authorizationRequest.Id,
                        completion.RequiresConsent
                            ? "consent"
                            : completion.RequiresMfa
                                ? completion.RequiresMfaEnrollment ? "mfa-enroll" : "mfa"
                                : "organization",
                        error: null,
                        pendingToken: completion.PendingToken,
                        email: authorizationRequest.LoginHintEmail,
                        displayName: null,
                        uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson),
                        mfaToken: completion.MfaToken,
                        consentToken: completion.ConsentToken));
                }

                return await RenderHostedAuthorizationCompletionAsync(
                    completion,
                    authorizationRequest,
                    authorizationRequest.LoginHintEmail,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
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
                return Html(page, mapped.StatusCode);
            }
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
                var rawMaxAge = context.Request.Query["max_age"].ToString();
                var invitationToken = ReadInvitationToken(context);
                // OIDC prompt is a space-delimited list; tokenize once so values
                // like "select_account consent" are still recognized. The raw
                // string is persisted on the authorization request unchanged.
                var promptValues = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var promptRequestsNone = promptValues.Contains("none", StringComparer.Ordinal);
                // select_account has no account chooser yet; forcing a fresh
                // sign-in is the compliant conservative reading.
                var promptForcesLogin = promptValues.Contains("login", StringComparer.Ordinal)
                    || promptValues.Contains("select_account", StringComparer.Ordinal);
                if (promptForcesLogin)
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
                        context.Request.Query["ui_context"].ToString(),
                        rawMaxAge),
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
                // TotalSeconds avoids the OverflowException TimeSpan.FromSeconds
                // would throw for max_age values near long.MaxValue.
                if (existingSession != null
                    && SqlOSAuthorizationServerService.TryParseMaxAge(rawMaxAge, out var maxAgeSeconds)
                    && maxAgeSeconds is { } maxAge
                    && (DateTime.UtcNow - existingSession.AuthenticatedAt).TotalSeconds >= maxAge)
                {
                    if (promptRequestsNone)
                    {
                        return Results.Redirect(await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
                            authorizationRequest,
                            "login_required",
                            "The user's authentication is older than max_age.",
                            cancellationToken));
                    }

                    await authPageSessionService.SignOutAsync(context, cancellationToken);
                    existingSession = null;
                }

                if (existingSession != null && !promptForcesLogin)
                {
                    var completion = await authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                        authorizationRequest,
                        existingSession.User,
                        existingSession.AuthenticationMethod,
                        context,
                        cancellationToken,
                        // Silent SSO reuses the session's original authentication moment, so
                        // the consent gate stamps the true auth time into its pending token
                        // instead of falling back to a later resolution.
                        knownAuthenticatedAt: existingSession.AuthenticatedAt);
                    if ((completion.RequiresConsent || completion.RequiresOrganizationSelection || completion.RequiresMfa)
                        && promptRequestsNone)
                    {
                        await authorizationServerService.CancelAuthorizationInteractionAsync(
                            completion,
                            cancellationToken);
                        return Results.Redirect(await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
                            authorizationRequest,
                            completion.RequiresConsent ? "consent_required" : "interaction_required",
                            completion.RequiresConsent
                                ? "User interaction is required for this client."
                                : "Additional user interaction is required.",
                            cancellationToken));
                    }

                    if (completion.RequiresConsent)
                    {
                        if (headlessAuthService.IsBrowserUiEnabled)
                        {
                            return Results.Redirect(headlessAuthService.BuildUiUrl(
                                context,
                                authorizationRequest.Id,
                                "consent",
                                error: null,
                                pendingToken: null,
                                email: existingSession.User.DefaultEmail,
                                displayName: null,
                                uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson),
                                consentToken: completion.ConsentToken));
                        }

                        return Html(await BuildAuthPageViewModelAsync(
                            "consent",
                            authorizationRequest.Id,
                            existingSession.User.DefaultEmail,
                            error: null,
                            displayName: null,
                            pendingToken: null,
                            authPrefix,
                            authorizationServerService,
                            cancellationToken,
                            consentToken: completion.ConsentToken,
                            consentScopes: completion.ConsentScopes));
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

                if (promptRequestsNone)
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
