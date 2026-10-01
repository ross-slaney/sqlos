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
    private static void MapHostedPrimaryEndpoints(RouteGroupBuilder auth, RouteGroupBuilder hostedForms, string authPrefix)
    {
        auth.MapGet("/login", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var invitationToken = ReadInvitationToken(context);
            var deviceUserCode = ReadDeviceUserCode(context);
            var statusMode = context.Request.Query["status"].ToString().Trim().ToLowerInvariant() switch
            {
                "signed-in" => "signed-in",
                "signed-up" => "signed-up",
                "invitation-accepted" => "invitation-accepted",
                _ => null
            };
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
                    invitation == null ? "login" : "invite",
                    context.Request.Query["request"].ToString(),
                invitation?.Email ?? context.Request.Query["email"].ToString(),
                uiContext));
            }

            var page = await BuildAuthPageViewModelAsync(
                statusMode ?? (invitation == null ? "login" : "invite"),
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

        auth.MapGet("/password/forgot", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (headlessAuthService.IsBrowserUiEnabled)
            {
                return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                    context,
                    "forgot-password",
                    context.Request.Query["request"].ToString(),
                    context.Request.Query["email"].ToString(),
                    uiContext: null));
            }

            var page = await BuildAuthPageViewModelAsync(
                "forgot-password",
                context.Request.Query["request"].ToString(),
                context.Request.Query["email"].ToString(),
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        hostedForms.MapPost("/password/forgot/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSIdentityProcesses processes,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var email = form["email"].ToString();
            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);

            try
            {
                // The link is bound to the authorization request's client.
                var outcome = await processes.RequestPasswordReset().ExecuteAsync(
                    new RequestPasswordResetCommand(
                        email,
                        authorizationRequest?.ClientApplication?.ClientId,
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                if (outcome is PasswordResetRequestOutcome.Refused refused)
                {
                    throw refused.Refusal.ToException();
                }

                return Html(await BuildAuthPageViewModelAsync(
                    "forgot-password-sent",
                    requestId,
                    email,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Html(await BuildAuthPageViewModelAsync(
                    "forgot-password",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken),
                    StatusCodes.Status400BadRequest);
            }
        });

        auth.MapGet("/invitations/accept", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var invitationToken = ReadInvitationToken(context);
            try
            {
                if (string.IsNullOrWhiteSpace(invitationToken))
                {
                    throw new InvalidOperationException("Invitation is invalid or expired.");
                }

                var invitation = await invitationService.ResolveEmailInvitationAsync(invitationToken, context, cancellationToken);
                if (headlessAuthService.IsBrowserUiEnabled)
                {
                    return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                        context,
                        "invite",
                        requestId: null,
                        email: invitation.Email,
                        uiContext: new JsonObject { ["invitationToken"] = invitationToken }));
                }

                var page = await BuildAuthPageViewModelAsync(
                    "invite",
                    null,
                    invitation.Email,
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
                var page = await BuildAuthPageViewModelAsync(
                    "login",
                    null,
                    null,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapGet("/device", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            var userCode = ReadDeviceUserCode(context);
            if (headlessAuthService.IsBrowserUiEnabled)
            {
                if (string.IsNullOrWhiteSpace(userCode))
                {
                    return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                        context,
                        "device",
                        requestId: null,
                        email: null,
                        uiContext: null));
                }
            }

            if (string.IsNullOrWhiteSpace(userCode))
            {
                return Html(await BuildAuthPageViewModelAsync(
                    "device",
                    null,
                    null,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken));
            }

            try
            {
                var authorizationRequest = await deviceAuthorizationService.CreateOrGetAuthorizationRequestAsync(
                    userCode,
                    headlessAuthService.IsBrowserUiEnabled ? "headless" : "hosted",
                    cancellationToken);
                if (headlessAuthService.IsBrowserUiEnabled)
                {
                    return Results.Redirect(headlessAuthService.BuildUiUrl(
                        context,
                        authorizationRequest.Id,
                        "device",
                        error: null,
                        pendingToken: null,
                        email: authorizationRequest.LoginHintEmail,
                        displayName: null,
                        uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson)));
                }

                var session = await issuerSessionService.TryGetSessionAsync(context, cancellationToken);
                var resolved = await deviceAuthorizationService.ResolveAsync(authorizationRequest, session?.User, cancellationToken);
                if (session == null)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "login",
                        authorizationRequest.Id,
                        null,
                        null,
                        null,
                        null,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        info: $"Sign in to approve CLI access for {resolved.ClientName}.",
                        deviceAuthorization: resolved));
                }

                if (string.IsNullOrWhiteSpace(authorizationRequest.ResolvedAuthMethod))
                {
                    var completion = await authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                        authorizationRequest,
                        session.User,
                        session.AuthenticationMethod,
                        context,
                        cancellationToken);
                    if (completion.RequiresOrganizationSelection || completion.RequiresMfa)
                    {
                        if (headlessAuthService.IsBrowserUiEnabled)
                        {
                            return Results.Redirect(headlessAuthService.BuildUiUrl(
                                context,
                                authorizationRequest.Id,
                                completion.RequiresOrganizationSelection
                                    ? "organization"
                                    : completion.RequiresMfaEnrollment ? "mfa-enroll" : "mfa",
                                error: null,
                                pendingToken: completion.PendingToken,
                                email: session.User.DefaultEmail,
                                displayName: null,
                                uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson),
                                mfaToken: completion.MfaToken));
                        }

                        return await RenderHostedAuthorizationCompletionAsync(
                            completion,
                            authorizationRequest,
                            session.User.DefaultEmail,
                            authPrefix,
                            authorizationServerService,
                            authService,
                            cancellationToken);
                    }

                    return ClientRedirect(completion.RedirectUrl!);
                }

                return Html(await BuildAuthPageViewModelAsync(
                    "device-approve",
                    authorizationRequest.Id,
                    session.User.DefaultEmail,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    resolved.Organizations,
                    deviceAuthorization: resolved));
            }
            catch (InvalidOperationException ex)
            {
                return Html(await BuildAuthPageViewModelAsync(
                    "device",
                    null,
                    null,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    deviceUserCode: userCode),
                    StatusCodes.Status400BadRequest);
            }
        });

        auth.MapGet("/device/approve", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            var requestId = context.Request.Query["request"].ToString();
            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken)
                ?? throw new InvalidOperationException("Device authorization request is invalid or expired.");
            var session = await issuerSessionService.TryGetSessionAsync(context, cancellationToken);
            var resolved = await deviceAuthorizationService.ResolveAsync(authorizationRequest, session?.User, cancellationToken);
            if (headlessAuthService.IsBrowserUiEnabled && SqlOSHeadlessAuthService.IsHeadlessRequest(authorizationRequest))
            {
                return Results.Redirect(headlessAuthService.BuildUiUrl(
                    context,
                    authorizationRequest.Id,
                    session == null ? "login" : "device-approve",
                    error: null,
                    pendingToken: null,
                    email: session?.User.DefaultEmail ?? authorizationRequest.LoginHintEmail,
                    displayName: null,
                    uiContext: SqlOSHeadlessAuthService.ParseUiContext(authorizationRequest.UiContextJson)));
            }

            if (session == null)
            {
                return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                    $"{authPrefix}/device",
                    "user_code",
                    resolved.UserCode));
            }

            return Html(await BuildAuthPageViewModelAsync(
                "device-approve",
                authorizationRequest.Id,
                session.User.DefaultEmail,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                resolved.Organizations,
                deviceAuthorization: resolved));
        });

        hostedForms.MapPost("/device/verify", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var userCode = form["userCode"].ToString();
            return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                $"{authPrefix}/device",
                "user_code",
                userCode));
        });

        hostedForms.MapPost("/device/approve", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var userCode = form["userCode"].ToString();
            var organizationId = form["organizationId"].ToString();
            var session = await issuerSessionService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                    $"{authPrefix}/device",
                    "user_code",
                    userCode));
            }

            try
            {
                SqlOSAuthorizationRequest authorizationRequest;
                SqlOSDeviceAuthorizationResolveResult resolved;
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
                    SqlOSDeviceAuthorizationService.RequireDeviceAuthorizationRequest(authorizationRequest);
                }
                else
                {
                    authorizationRequest = await deviceAuthorizationService.CreateOrGetAuthorizationRequestAsync(
                        userCode,
                        "hosted",
                        cancellationToken);
                }

                // The picked organization is authorized by the membership-checked completion
                // and approval below. It is never written to the request itself.
                if (string.IsNullOrWhiteSpace(authorizationRequest.ResolvedAuthMethod))
                {
                    var completion = await authorizationServerService.CompleteDeviceApprovalLoginAsync(
                        authorizationRequest,
                        session.User,
                        session.AuthenticationMethod,
                        context,
                        organizationId,
                        cancellationToken);
                    if (completion.RequiresMfa || completion.RequiresOrganizationSelection)
                    {
                        return await RenderHostedAuthorizationCompletionAsync(
                            completion,
                            authorizationRequest,
                            session.User.DefaultEmail,
                            authPrefix,
                            authorizationServerService,
                            authService,
                            cancellationToken);
                    }

                    session = await issuerSessionService.TryGetSessionAsync(context, cancellationToken)
                        ?? throw new InvalidOperationException("Sign in before approving this device request.");
                }

                resolved = await deviceAuthorizationService.ApproveAuthorizationRequestAsync(
                    authorizationRequest,
                    session.User,
                    session.AuthenticationMethod,
                    context,
                    organizationId,
                    cancellationToken);

                if (resolved.RequiresOrganizationSelection)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "device-approve",
                        authorizationRequest.Id,
                        session.User.DefaultEmail,
                        null,
                        null,
                        null,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        resolved.Organizations,
                        deviceAuthorization: resolved));
                }

                return Html(await BuildAuthPageViewModelAsync(
                    "device-approved",
                    authorizationRequest.Id,
                    session.User.DefaultEmail,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    deviceAuthorization: resolved));
            }
            catch (InvalidOperationException ex)
            {
                var authorizationRequest = string.IsNullOrWhiteSpace(requestId)
                    ? null
                    : await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                if (authorizationRequest != null
                    && !SqlOSDeviceAuthorizationService.IsDeviceAuthorizationRequest(authorizationRequest))
                {
                    // No device authorization to resolve: return to device code entry.
                    return Html(await BuildAuthPageViewModelAsync(
                        "device",
                        null,
                        null,
                        await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                        null,
                        null,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken),
                        StatusCodes.Status400BadRequest);
                }

                var resolved = authorizationRequest == null
                    ? await deviceAuthorizationService.ResolveAsync(userCode, session.User, cancellationToken)
                    : await deviceAuthorizationService.ResolveAsync(authorizationRequest, session.User, cancellationToken);
                return Html(await BuildAuthPageViewModelAsync(
                    "device-approve",
                    authorizationRequest?.Id,
                    session.User.DefaultEmail,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    resolved.Organizations,
                    deviceAuthorization: resolved),
                    StatusCodes.Status400BadRequest);
            }
        });

        hostedForms.MapPost("/device/deny", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            SqlOSIssuerSessionService issuerSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var userCode = form["userCode"].ToString();
            var session = await issuerSessionService.TryGetSessionAsync(context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                var authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
                var resolved = await deviceAuthorizationService.ResolveAsync(authorizationRequest, session?.User, cancellationToken);
                userCode = resolved.UserCode;
                authorizationRequest.CancelledAt = DateTime.UtcNow;
            }

            await deviceAuthorizationService.DenyAsync(userCode, session?.User, context, cancellationToken);
            return Html(await BuildAuthPageViewModelAsync(
                "device-approved",
                string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                session?.User.DefaultEmail,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                info: "CLI access was denied.",
                deviceUserCode: userCode));
        });

        hostedForms.MapPost("/login/identify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSSettingsService settingsService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);

            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
            var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken);
            email = invitation?.Email ?? email;
            if (await RouteToIdentityProviderAsync(processes, authorizationRequest, email, cancellationToken) is { } identityProvider)
            {
                return identityProvider;
            }

            var credentialSettings = await settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
            var nextView = ResolvePreferredLocalView(credentialSettings);

            var page = await BuildAuthPageViewModelAsync(
                nextView,
                requestId,
                email,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                invitationToken: invitationToken,
                invitation: invitation,
                invitationService: invitationService,
                deviceUserCode: deviceUserCode);
            return Html(page);
        });

        hostedForms.MapPost("/login/password", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
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

                var outcome = await processes.SignInWithPassword(context).ExecuteAsync(
                    new SignInWithPasswordCommand(
                        email,
                        password,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken),
                        CarriesInvitation: invitation != null,
                        PasswordAttemptContext.ForAuthorizationRequest(
                            authorizationRequest,
                            authorizationRequest == null ? "hosted_standalone" : "hosted"),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                var completion = outcome switch
                {
                    SignInOutcome.SignedIn signedIn => signedIn.Completion,
                    SignInOutcome.Refused refused => throw refused.Refusal.ToException(),
                    _ => throw new InvalidOperationException($"Unknown sign-in outcome '{outcome.GetType().Name}'.")
                };
                if (completion is LoginCompletion.BrowserSignedIn)
                {
                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-in" : "invitation-accepted", deviceUserCode);
                }

                var result = ((LoginCompletion.AuthorizationRequestContinued)completion).Result;
                if (result.RequiresConsent)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "consent",
                        requestId,
                        email,
                        null,
                        null,
                        null,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        consentToken: result.ConsentToken,
                        consentScopes: result.ConsentScopes));
                }

                if (result.RequiresOrganizationSelection)
                {
                    var organizationPage = await BuildAuthPageViewModelAsync(
                        "organization",
                        requestId,
                        email,
                        null,
                        null,
                        result.PendingToken,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        result.Organizations,
                        invitationToken: invitationToken,
                        invitation: invitation,
                        invitationService: invitationService);
                    return Html(organizationPage);
                }

                if (result.RequiresMfa)
                {
                    return await RenderMfaChallengeAsync(
                        result,
                        requestId,
                        email,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        cancellationToken,
                        invitationToken: invitationToken,
                        invitationService: invitationService);
                }

                return ClientRedirect(result.RedirectUrl!);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "password",
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
    }
}
