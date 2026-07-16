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
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var email = form["email"].ToString();
            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);

            try
            {
                await authService.RequestPasswordResetEmailAsync(
                    new SqlOSForgotPasswordRequest(
                        email,
                        authorizationRequest?.ClientApplication?.ClientId),
                    context,
                    cancellationToken);

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
            SqlOSAuthPageSessionService authPageSessionService,
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

                var session = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
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
                    return Results.Redirect(await authorizationServerService.IssueAuthorizationRedirectAsync(
                        authorizationRequest,
                        session.User,
                        session.OrganizationId,
                        session.AuthenticationMethod,
                        context,
                        cancellationToken));
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
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            var requestId = context.Request.Query["request"].ToString();
            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken)
                ?? throw new InvalidOperationException("Device authorization request is invalid or expired.");
            var session = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
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
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var userCode = form["userCode"].ToString();
            var organizationId = form["organizationId"].ToString();
            var session = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
            if (session == null)
            {
                return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                    $"{authPrefix}/device",
                    "user_code",
                    userCode));
            }

            try
            {
                SqlOSAuthorizationRequest? authorizationRequest = null;
                SqlOSDeviceAuthorizationResolveResult resolved;
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    authorizationRequest = await authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(organizationId))
                    {
                        authorizationRequest.ResolvedOrganizationId = organizationId;
                    }

                    resolved = await deviceAuthorizationService.ApproveAsync(
                        authorizationRequest,
                        session.User,
                        session.AuthenticationMethod,
                        context,
                        cancellationToken);
                }
                else
                {
                    resolved = await deviceAuthorizationService.ApproveAsync(
                        new SqlOSDeviceAuthorizationApprovalRequest(userCode, string.IsNullOrWhiteSpace(organizationId) ? null : organizationId),
                        session.User,
                        session.AuthenticationMethod,
                        context,
                        cancellationToken);
                }

                if (resolved.RequiresOrganizationSelection)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "device-approve",
                        authorizationRequest?.Id,
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
                    authorizationRequest?.Id,
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
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = ReadRequestId(context, form);
            var userCode = form["userCode"].ToString();
            var session = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
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
            SqlOSHomeRealmDiscoveryService discoveryService,
            SqlOSSamlService samlService,
            ISqlOSAuthServerDbContext dbContext,
            SqlOSSettingsService settingsService,
            SqlOSInvitationService invitationService,
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
            var discovery = await discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(email), cancellationToken);
            if (authorizationRequest != null)
            {
                authorizationRequest.LoginHintEmail = email;
                if (!string.IsNullOrWhiteSpace(discovery.OrganizationId))
                {
                    authorizationRequest.OrganizationId = discovery.OrganizationId;
                    authorizationRequest.ResolvedOrganizationId = discovery.OrganizationId;
                }

                if (!string.IsNullOrWhiteSpace(discovery.ConnectionId))
                {
                    authorizationRequest.ConnectionId = discovery.ConnectionId;
                    authorizationRequest.ResolvedConnectionId = discovery.ConnectionId;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (authorizationRequest != null
                && string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(discovery.ConnectionId))
            {
                return Results.Redirect(await samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken));
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
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSInvitationService invitationService,
            SqlOSHomeRealmDiscoveryService discoveryService,
            SqlOSSamlService samlService,
            ISqlOSAuthServerDbContext dbContext,
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

                var authentication = await authorizationServerService.AuthenticatePasswordAsync(
                    email,
                    password,
                    cancellationToken,
                    allowUnverifiedEmailForInvitation: invitation != null,
                    httpContext: context,
                    clientKey: authorizationRequest?.ClientApplication?.ClientId ?? authorizationRequest?.ClientApplicationId,
                    authorizationRequestId: authorizationRequest?.Id,
                    surface: authorizationRequest == null ? "hosted_standalone" : "hosted");
                if (authorizationRequest == null)
                {
                    var organizationId = authentication.Organizations.FirstOrDefault()?.Id;
                    if (!string.IsNullOrWhiteSpace(invitationToken))
                    {
                        var acceptance = await invitationService.AcceptEmailInvitationAsync(
                            new SqlOSAcceptEmailInvitationRequest(invitationToken, authentication.User.Id),
                            context,
                            cancellationToken);
                        organizationId = acceptance.OrganizationId;
                    }

                    await authPageSessionService.SignInAsync(context, authentication.User, organizationId, authentication.AuthenticationMethod, cancellationToken);
                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-in" : "invitation-accepted", deviceUserCode);
                }
                var completion = await authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                    authorizationRequest,
                    authentication.User,
                    authentication.AuthenticationMethod,
                    context,
                    cancellationToken);

                if (completion.RequiresOrganizationSelection)
                {
                    var organizationPage = await BuildAuthPageViewModelAsync(
                        "organization",
                        requestId,
                        email,
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
                        email,
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
