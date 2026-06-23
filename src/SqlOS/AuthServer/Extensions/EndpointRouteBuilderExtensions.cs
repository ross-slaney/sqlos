using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
using SqlOS.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Extensions;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthServer(this IEndpointRouteBuilder endpoints, string? pathPrefix = null)
    {
        var authPrefix = (pathPrefix ?? "/sqlos/auth").TrimEnd('/');
        var authOptions = endpoints.ServiceProvider.GetService<IOptions<SqlOSAuthServerOptions>>()?.Value ?? new SqlOSAuthServerOptions();
        var adminPrefix = authPrefix.EndsWith("/auth", StringComparison.OrdinalIgnoreCase)
            ? $"{authPrefix[..^5]}/admin/auth"
            : $"{authPrefix}/admin";
        var resolvedHeadlessPath = authOptions.Headless.ResolveApiBasePath(authPrefix);
        var resolvedSsoSetupApiPath = authOptions.SsoPortal.ResolveHeadlessApiBasePath(adminPrefix);

        var auth = endpoints.MapGroup(authPrefix);
        auth.ExcludeFromDescription();

        var adminRoot = endpoints.MapGroup(adminPrefix);
        adminRoot.ExcludeFromDescription();

        var adminApi = adminRoot.MapGroup("/api");
        var ssoSetupApi = endpoints.MapGroup(resolvedSsoSetupApiPath);
        ssoSetupApi.ExcludeFromDescription();

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

        auth.MapGet("/.well-known/jwks.json", async (SqlOSCryptoService cryptoService, SqlOSSettingsService settingsService, CancellationToken cancellationToken) =>
        {
            var rotationSettings = await settingsService.GetKeyRotationSettingsAsync(cancellationToken);
            var keys = await cryptoService.GetValidationSigningKeysAsync(rotationSettings.GraceWindow, cancellationToken);
            return Results.Ok(cryptoService.GetJwksDocument(keys));
        });

        auth.MapGet("/authorize", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
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
                    "phone-otp" => "phone-otp",
                    "phone-otp-signup" => "phone-otp-signup",
                    _ when invitation != null => "invite",
                    _ => "login"
                };

                var existingSession = await authPageSessionService.TryGetSessionAsync(context, cancellationToken);
                if (existingSession != null && !string.Equals(prompt, "login", StringComparison.Ordinal))
                {
                    var redirectUrl = await authorizationServerService.IssueAuthorizationRedirectAsync(
                        authorizationRequest,
                        existingSession.User,
                        existingSession.OrganizationId,
                        existingSession.AuthenticationMethod,
                        context,
                        cancellationToken);
                    return Results.Redirect(redirectUrl);
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

        auth.MapPost("/password/forgot/submit", async (
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
                    new SqlOSSendPasswordResetEmailRequest(
                        email,
                        ResetUrlTemplate: null,
                        ClientId: authorizationRequest?.ClientApplication?.ClientId),
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

        auth.MapPost("/device/verify", async (
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

        auth.MapPost("/device/approve", async (
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

        auth.MapPost("/device/deny", async (
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

        auth.MapPost("/login/identify", async (
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

        auth.MapPost("/login/password", async (
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

        auth.MapPost("/login/email-otp/start", async (
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

        auth.MapPost("/login/email-otp/verify", async (
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

        auth.MapGet("/login/phone-otp", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            var deviceUserCode = ReadDeviceUserCode(context);
            var phoneNumber = context.Request.Query["phoneNumber"].ToString();
            if (headlessAuthService.IsBrowserUiEnabled)
            {
                var uiContext = SqlOSHeadlessAuthService.ParseUiContext(context.Request.Query["ui_context"].ToString()) ?? new JsonObject();
                if (!string.IsNullOrWhiteSpace(deviceUserCode))
                {
                    uiContext["deviceUserCode"] = deviceUserCode;
                }

                return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                    context,
                    "phone-otp",
                    context.Request.Query["request"].ToString(),
                    email: null,
                    uiContext));
            }

            var page = await BuildAuthPageViewModelAsync(
                "phone-otp",
                context.Request.Query["request"].ToString(),
                email: null,
                error: null,
                displayName: null,
                pendingToken: null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                deviceUserCode: deviceUserCode,
                phoneNumber: phoneNumber);
            return Html(page);
        });

        auth.MapPost("/login/phone-otp/start", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSPhoneOtpService phoneOtpService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var phoneNumber = form["phoneNumber"].ToString();
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var challenge = await phoneOtpService.StartForAuthorizationRequestAsync(
                    authorizationRequest,
                    phoneNumber,
                    context,
                    cancellationToken);

                var page = await BuildAuthPageViewModelAsync(
                    "phone-otp-verify",
                    requestId,
                    email: null,
                    error: null,
                    displayName: null,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    info: challenge.Message,
                    challengeToken: challenge.ChallengeToken,
                    deviceUserCode: deviceUserCode,
                    phoneNumber: challenge.PhoneNumber);
                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "phone-otp",
                    requestId,
                    email: null,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName: null,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    deviceUserCode: deviceUserCode,
                    phoneNumber: phoneNumber);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/login/phone-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSPhoneOtpService phoneOtpService,
            SqlOSAuthService authService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var phoneNumber = form["phoneNumber"].ToString();
            var challengeToken = form["challengeToken"].ToString();
            var code = form["code"].ToString();
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var verification = await phoneOtpService.VerifyAsync(
                    new SqlOSPhoneOtpVerifyRequest(challengeToken, code),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var organizationId = verification.Organizations.FirstOrDefault()?.Id;
                    await authPageSessionService.SignInAsync(
                        context,
                        verification.User,
                        organizationId,
                        verification.AuthenticationMethod,
                        cancellationToken);
                    return RedirectAfterStandaloneSignIn(authPrefix, "signed-in", deviceUserCode);
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
                        email: null,
                        error: null,
                        displayName: null,
                        pendingToken: completion.PendingToken,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        completion.Organizations,
                        phoneNumber: phoneNumber);
                    return Html(organizationPage);
                }

                if (completion.RequiresMfa)
                {
                    return await RenderMfaChallengeAsync(
                        completion,
                        requestId,
                        email: null,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        cancellationToken,
                        phoneNumber: phoneNumber);
                }

                return Results.Redirect(completion.RedirectUrl!);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "phone-otp-verify",
                    requestId,
                    email: null,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName: null,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    challengeToken: challengeToken,
                    deviceUserCode: deviceUserCode,
                    phoneNumber: phoneNumber);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/login/select-organization", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var pendingToken = form["pendingToken"].ToString();
            var organizationId = form["organizationId"].ToString();
            var completion = await authorizationServerService.CompletePendingOrganizationSelectionForLoginAsync(
                pendingToken,
                organizationId,
                context,
                cancellationToken);
            if (completion.RequiresMfa)
            {
                return await RenderMfaChallengeAsync(
                    completion,
                    requestId,
                    email: null,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
            }

            return Results.Redirect(completion.RedirectUrl!);
        });

        auth.MapPost("/mfa/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var mfaToken = form["mfaToken"].ToString();
            var code = form["code"].ToString();

            try
            {
                var redirectUrl = await authorizationServerService.CompleteMfaChallengeAsync(
                    mfaToken,
                    code,
                    context,
                    cancellationToken);
                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "mfa",
                    requestId,
                    email: null,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName: null,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    mfaToken: mfaToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/mfa/totp/enroll/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var mfaToken = form["mfaToken"].ToString();
            var enrollmentToken = form["enrollmentToken"].ToString();
            var code = form["code"].ToString();

            try
            {
                await authService.VerifyTotpEnrollmentAsync(
                    new SqlOSTotpEnrollmentVerifyRequest(enrollmentToken, code),
                    context,
                    cancellationToken);
                var redirectUrl = await authorizationServerService.CompleteMfaChallengeWithoutCodeAsync(
                    mfaToken,
                    SqlOSMfaFactorTypes.Totp,
                    context,
                    cancellationToken);
                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                var completion = new SqlOSAuthorizationRequestLoginResult(
                    null,
                    false,
                    null,
                    Array.Empty<SqlOSOrganizationOption>(),
                    RequiresMfa: true,
                    MfaToken: mfaToken,
                    RequiresMfaEnrollment: true,
                    MfaMethods: [SqlOSMfaFactorTypes.Totp]);
                return await RenderMfaChallengeAsync(
                    completion,
                    requestId,
                    email: null,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken));
            }
        });

        auth.MapGet("/login/oidc/{connectionId}", async (
            string connectionId,
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSOidcBrowserAuthService oidcBrowserAuthService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var requestId = context.Request.Query["request"].ToString();
            var email = context.Request.Query["email"].ToString();
            var invitationToken = ReadInvitationToken(context);
            if (string.IsNullOrWhiteSpace(requestId))
            {
                var page = await BuildAuthPageViewModelAsync(
                    "login",
                    null,
                    email,
                    "OIDC sign-in requires an active authorization request.",
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(page, StatusCodes.Status400BadRequest);
            }

            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
            var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken);
            email = invitation?.Email ?? email;
            var result = await oidcBrowserAuthService.CreateAuthorizationUrlForAuthRequestAsync(requestId, connectionId, email, context, cancellationToken);
            return Results.Redirect(result.AuthorizationUrl);
        });

        auth.MapGet("/signup", async (
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
                    "signup",
                    context.Request.Query["request"].ToString(),
                    invitation?.Email ?? context.Request.Query["email"].ToString(),
                    uiContext));
            }

            var page = await BuildAuthPageViewModelAsync(
                "signup",
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

        auth.MapGet("/signup/phone-otp", async (
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
            var phoneNumber = context.Request.Query["phoneNumber"].ToString();

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
                if (!string.IsNullOrWhiteSpace(phoneNumber))
                {
                    uiContext["phoneNumber"] = phoneNumber;
                }

                return Results.Redirect(headlessAuthService.BuildStandaloneUiUrl(
                    context,
                    "phone-otp-signup",
                    context.Request.Query["request"].ToString(),
                    email: null,
                    uiContext));
            }

            var page = await BuildAuthPageViewModelAsync(
                "phone-otp-signup",
                context.Request.Query["request"].ToString(),
                email: null,
                error: null,
                displayName: context.Request.Query["displayName"].ToString(),
                pendingToken: null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                invitationToken: invitationToken,
                invitation: invitation,
                deviceUserCode: deviceUserCode,
                phoneNumber: phoneNumber);
            return Html(page);
        });

        auth.MapPost("/signup/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSInvitationService invitationService,
            SqlOSHomeRealmDiscoveryService discoveryService,
            SqlOSSamlService samlService,
            ISqlOSAuthServerDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var displayName = form["displayName"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var organizationName = form["organizationName"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);
            IDbContextTransaction? transaction = null;

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

                if (SupportsDatabaseTransactions(dbContext))
                {
                    transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                }

                var signup = await authorizationServerService.SignUpAsync(
                    displayName,
                    email,
                    password,
                    invitation == null ? organizationName : null,
                    invitation == null ? authorizationRequest?.OrganizationId : null,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var organizationId = signup.Organizations.FirstOrDefault()?.Id;
                    if (!string.IsNullOrWhiteSpace(invitationToken))
                    {
                        var acceptance = await invitationService.AcceptEmailInvitationInCurrentTransactionAsync(
                            new SqlOSAcceptEmailInvitationRequest(invitationToken, signup.User.Id),
                            context,
                            cancellationToken);
                        organizationId = acceptance.OrganizationId;
                    }

                    await authPageSessionService.SignInAsync(context, signup.User, organizationId, signup.AuthenticationMethod, cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-up" : "invitation-accepted", deviceUserCode);
                }

                var redirectUrl = await authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    signup.User,
                    invitation?.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var page = await BuildAuthPageViewModelAsync(
                    "signup",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName,
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

        auth.MapPost("/signup/invitation/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSInvitationService invitationService,
            SqlOSSettingsService settingsService,
            SqlOSHomeRealmDiscoveryService discoveryService,
            SqlOSSamlService samlService,
            ISqlOSAuthServerDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var displayName = form["displayName"].ToString();
            var email = form["email"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);
            IDbContextTransaction? transaction = null;

            try
            {
                if (SupportsDatabaseTransactions(dbContext))
                {
                    transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                }

                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken)
                    ?? throw new InvalidOperationException("Invitation is invalid or expired.");
                email = invitation.Email;
                var ssoRedirect = await RedirectToSsoIfRequiredAsync(
                    authorizationRequest,
                    email,
                    discoveryService,
                    samlService,
                    dbContext,
                    cancellationToken);
                if (ssoRedirect != null)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return ssoRedirect;
                }

                var credentialSettings = await settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
                if (!credentialSettings.EmailOtpEnabled)
                {
                    throw new InvalidOperationException("Invitation signup without a password requires Email OTP to be enabled.");
                }

                var signup = await authorizationServerService.SignUpWithInvitationAsync(
                    displayName,
                    email,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var acceptance = await invitationService.AcceptEmailInvitationInCurrentTransactionAsync(
                        new SqlOSAcceptEmailInvitationRequest(invitationToken!, signup.User.Id),
                        context,
                        cancellationToken);

                    await authPageSessionService.SignInAsync(context, signup.User, acceptance.OrganizationId, signup.AuthenticationMethod, cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return RedirectAfterStandaloneSignIn(authPrefix, "invitation-accepted", deviceUserCode);
                }

                var redirectUrl = await authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    signup.User,
                    invitation.OrganizationId,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var page = await BuildAuthPageViewModelAsync(
                    "signup",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName,
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

        auth.MapPost("/signup/email-otp/start", async (
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
            var displayName = form["displayName"].ToString();
            var email = form["email"].ToString();
            var organizationName = form["organizationName"].ToString();
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

                var signup = await emailOtpService.StartSignupForAuthorizationRequestAsync(
                    authorizationRequest,
                    displayName,
                    email,
                    invitation == null ? organizationName : null,
                    customFields: invitation?.CustomFields,
                    context,
                    cancellationToken);

                var page = await BuildAuthPageViewModelAsync(
                    "email-otp-signup-verify",
                    requestId,
                    email,
                    null,
                    displayName,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    info: signup.Message,
                    challengeToken: signup.ChallengeToken,
                    signupToken: signup.SignupToken,
                    invitationToken: invitationToken,
                    invitation: invitation,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode);
                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "signup",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName,
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

        auth.MapPost("/signup/email-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSEmailOtpService emailOtpService,
            ISqlOSAuthServerDbContext dbContext,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var signupToken = form["signupToken"].ToString();
            var challengeToken = form["challengeToken"].ToString();
            var code = form["code"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);
            IDbContextTransaction? transaction = null;

            try
            {
                if (SupportsDatabaseTransactions(dbContext))
                {
                    transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                }

                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                email = invitation?.Email ?? email;
                var signupVerification = await emailOtpService.VerifySignupAsync(
                    new SqlOSEmailOtpSignupVerifyRequest(signupToken, challengeToken, code),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                var signup = await authorizationServerService.SignUpWithEmailOtpAsync(
                    signupVerification.DisplayName,
                    signupVerification.Email,
                    invitation == null ? signupVerification.OrganizationName : null,
                    invitation == null ? authorizationRequest?.OrganizationId ?? signupVerification.OrganizationId : null,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var organizationId = signup.Organizations.FirstOrDefault()?.Id;
                    if (!string.IsNullOrWhiteSpace(invitationToken))
                    {
                        var acceptance = await invitationService.AcceptEmailInvitationInCurrentTransactionAsync(
                            new SqlOSAcceptEmailInvitationRequest(invitationToken, signup.User.Id),
                            context,
                            cancellationToken);
                        organizationId = acceptance.OrganizationId;
                    }

                    await authPageSessionService.SignInAsync(context, signup.User, organizationId, signup.AuthenticationMethod, cancellationToken);
                    await emailOtpService.ConsumeSignupTokenAsync(signupVerification.SignupToken, cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-up" : "invitation-accepted", deviceUserCode);
                }

                var redirectUrl = await authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    signup.User,
                    invitation?.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken);

                await emailOtpService.ConsumeSignupTokenAsync(signupVerification.SignupToken, cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var page = await BuildAuthPageViewModelAsync(
                    "email-otp-signup-verify",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    challengeToken: challengeToken,
                    signupToken: signupToken,
                    invitationToken: invitationToken,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/signup/phone-otp/start", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSPhoneOtpService phoneOtpService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var displayName = form["displayName"].ToString();
            var phoneNumber = form["phoneNumber"].ToString();
            var organizationName = form["organizationName"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                if (invitation != null)
                {
                    throw new InvalidOperationException("Phone signup is not available for email invitations.");
                }

                var signup = await phoneOtpService.StartSignupForAuthorizationRequestAsync(
                    authorizationRequest,
                    displayName,
                    phoneNumber,
                    organizationName,
                    customFields: null,
                    context,
                    cancellationToken);

                var page = await BuildAuthPageViewModelAsync(
                    "phone-otp-signup-verify",
                    requestId,
                    email: null,
                    error: null,
                    displayName: displayName,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    info: signup.Message,
                    challengeToken: signup.ChallengeToken,
                    signupToken: signup.SignupToken,
                    invitationToken: invitationToken,
                    deviceUserCode: deviceUserCode,
                    phoneNumber: signup.PhoneNumber);
                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "phone-otp-signup",
                    requestId,
                    email: null,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName: displayName,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    invitationToken: invitationToken,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode,
                    phoneNumber: phoneNumber);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/signup/phone-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            SqlOSPhoneOtpService phoneOtpService,
            ISqlOSAuthServerDbContext dbContext,
            SqlOSInvitationService invitationService,
            SqlOSAdminService adminService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var phoneNumber = form["phoneNumber"].ToString();
            var signupToken = form["signupToken"].ToString();
            var challengeToken = form["challengeToken"].ToString();
            var code = form["code"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);
            IDbContextTransaction? transaction = null;

            try
            {
                if (SupportsDatabaseTransactions(dbContext))
                {
                    transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                }

                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                if (invitation != null)
                {
                    throw new InvalidOperationException("Phone signup is not available for email invitations.");
                }

                var signupVerification = await phoneOtpService.VerifySignupAsync(
                    new SqlOSPhoneOtpSignupVerifyRequest(signupToken, challengeToken, code),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                var signup = await authorizationServerService.SignUpWithPhoneOtpAsync(
                    signupVerification.DisplayName,
                    signupVerification.PhoneNumber,
                    signupVerification.OrganizationName,
                    authorizationRequest?.OrganizationId ?? signupVerification.OrganizationId,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var organizationId = signup.Organizations.FirstOrDefault()?.Id;
                    await authPageSessionService.SignInAsync(context, signup.User, organizationId, signup.AuthenticationMethod, cancellationToken);
                    await phoneOtpService.ConsumeSignupTokenAsync(signupVerification.SignupToken, cancellationToken);
                    await adminService.RecordAuditAsync(
                        "user.signup.phone_otp",
                        "user",
                        signup.User.Id,
                        userId: signup.User.Id,
                        organizationId: organizationId,
                        ipAddress: context.Connection.RemoteIpAddress?.ToString(),
                        cancellationToken: cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return RedirectAfterStandaloneSignIn(authPrefix, "signed-up", deviceUserCode);
                }

                var redirectUrl = await authorizationServerService.IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    signup.User,
                    signup.Organizations.FirstOrDefault()?.Id,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken);

                await phoneOtpService.ConsumeSignupTokenAsync(signupVerification.SignupToken, cancellationToken);
                await adminService.RecordAuditAsync(
                    "user.signup.phone_otp",
                    "user",
                    signup.User.Id,
                    userId: signup.User.Id,
                    organizationId: signup.Organizations.FirstOrDefault()?.Id,
                    ipAddress: context.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken: cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                var page = await BuildAuthPageViewModelAsync(
                    "phone-otp-signup-verify",
                    requestId,
                    email: null,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName: null,
                    pendingToken: null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    challengeToken: challengeToken,
                    signupToken: signupToken,
                    invitationToken: invitationToken,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode,
                    phoneNumber: phoneNumber);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        var headless = endpoints.MapGroup(resolvedHeadlessPath);
        headless.ExcludeFromDescription();

        headless.MapPost("/start", async (
            SqlOSHeadlessStartRequest request,
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                await headlessAuthService.EnsureNativeHeadlessClientAllowedAsync(
                    request.ClientId,
                    request.RedirectUri,
                    cancellationToken);

                var authorizationRequest = await authorizationServerService.CreateAuthorizationRequestAsync(
                    new SqlOSAuthorizeRequestInput(
                        request.ResponseType,
                        request.ClientId,
                        request.RedirectUri,
                        request.State,
                        request.Scope,
                        request.CodeChallenge,
                        request.CodeChallengeMethod,
                        request.Resource,
                        request.LoginHint,
                        request.Prompt,
                        request.Nonce,
                        "headless",
                        SqlOSHeadlessAuthService.NormalizeUiContext(request.UiContext)),
                    cancellationToken);

                SqlOSEmailInvitationResult? invitation = null;
                if (!string.IsNullOrWhiteSpace(request.InvitationToken))
                {
                    invitation = await invitationService.BindInvitationToAuthorizationRequestAsync(request.InvitationToken, authorizationRequest, cancellationToken);
                }

                if (string.Equals(request.Prompt, "none", StringComparison.Ordinal))
                {
                    return Results.Ok(new SqlOSHeadlessActionResult(
                        "redirect",
                        await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
                            authorizationRequest,
                            "login_required",
                            "The user is not signed in.",
                            cancellationToken),
                        null));
                }

                return Results.Ok(new SqlOSHeadlessActionResult(
                    "view",
                    null,
                    await headlessAuthService.GetRequestAsync(
                        authorizationRequest.Id,
                        string.IsNullOrWhiteSpace(request.View) && invitation != null ? "invite" : request.View,
                        error: null,
                        pendingToken: null,
                        email: invitation?.Email ?? authorizationRequest.LoginHintEmail,
                        displayName: null,
                        cancellationToken)));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/invitations/resolve", async (
            SqlOSHeadlessInvitationResolveRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.ResolveInvitationAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/device/resolve", async (
            SqlOSHeadlessDeviceAuthorizationResolveRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.ResolveDeviceAuthorizationAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/device/approve", async (
            SqlOSHeadlessDeviceAuthorizationApproveRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.ApproveDeviceAuthorizationAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/device/deny", async (
            SqlOSHeadlessDeviceAuthorizationResolveRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.DenyDeviceAuthorizationAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapGet("/requests/{requestId}", async (
            string requestId,
            string? view,
            string? error,
            string? pendingToken,
            string? email,
            string? displayName,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.GetRequestAsync(
                    requestId,
                    view,
                    error,
                    pendingToken,
                    email,
                    displayName,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/identify", async (
            SqlOSHeadlessIdentifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.IdentifyAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/password/login", async (
            SqlOSHeadlessPasswordLoginRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.PasswordLoginAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/password/forgot", async (
            SqlOSHeadlessPasswordResetEmailRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                var authorizationRequest = string.IsNullOrWhiteSpace(request.RequestId)
                    ? null
                    : await authorizationServerService.TryGetActiveAuthorizationRequestAsync(request.RequestId, cancellationToken);
                return Results.Ok(await authService.RequestPasswordResetEmailAsync(
                    new SqlOSSendPasswordResetEmailRequest(
                        request.Email,
                        request.ResetUrlTemplate,
                        authorizationRequest?.ClientApplication?.ClientId),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/password/reset", async (
            SqlOSResetPasswordRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                await authService.ResetPasswordAsync(request, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/email-otp/start", async (
            SqlOSHeadlessEmailOtpStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.RequestEmailOtpAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/email-otp/verify", async (
            SqlOSHeadlessEmailOtpVerifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.VerifyEmailOtpAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/signup/email-otp/start", async (
            SqlOSHeadlessEmailOtpSignupStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.RequestEmailOtpSignupAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/signup/email-otp/verify", async (
            SqlOSHeadlessEmailOtpSignupVerifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.VerifyEmailOtpSignupAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/invitations/signup", async (
            SqlOSHeadlessInvitationSignupRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.SignUpWithInvitationAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/phone-otp/start", async (
            SqlOSHeadlessPhoneOtpStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.RequestPhoneOtpAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/phone-otp/verify", async (
            SqlOSHeadlessPhoneOtpVerifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.VerifyPhoneOtpAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/signup/phone-otp/start", async (
            SqlOSHeadlessPhoneOtpSignupStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.RequestPhoneOtpSignupAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/signup/phone-otp/verify", async (
            SqlOSHeadlessPhoneOtpSignupVerifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.VerifyPhoneOtpSignupAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/signup", async (
            SqlOSHeadlessSignupRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.SignUpAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/organization/select", async (
            SqlOSHeadlessOrganizationSelectionRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.SelectOrganizationAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/mfa/verify", async (
            SqlOSHeadlessMfaVerifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.VerifyMfaAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/mfa/totp/enroll/start", async (
            SqlOSHeadlessMfaTotpEnrollmentStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.StartMfaTotpEnrollmentAsync(request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/mfa/totp/enroll/verify", async (
            SqlOSHeadlessMfaTotpEnrollmentVerifyRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.VerifyMfaTotpEnrollmentAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/provider/start", async (
            SqlOSHeadlessProviderStartRequest request,
            HttpContext context,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken) =>
        {
            if (!headlessAuthService.IsApiEnabled)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await headlessAuthService.StartProviderAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        auth.MapGet("/logged-out", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            await authPageSessionService.SignOutAsync(context, cancellationToken);
            var page = await BuildAuthPageViewModelAsync(
                "logged-out",
                null,
                null,
                null,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        auth.MapGet("/logout", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            await authPageSessionService.SignOutAsync(context, cancellationToken);

            var requestedReturnUrl = context.Request.Query["returnTo"].ToString();
            if (string.IsNullOrWhiteSpace(requestedReturnUrl))
            {
                requestedReturnUrl = context.Request.Query["post_logout_redirect_uri"].ToString();
            }

            var redirectTarget = await authorizationServerService.ResolvePostLogoutRedirectAsync(
                context,
                requestedReturnUrl,
                cancellationToken);

            return redirectTarget == null
                ? Results.Redirect($"{authPrefix}/logged-out")
                : Results.Redirect(redirectTarget);
        });

        auth.MapPost("/token", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSDeviceAuthorizationService deviceAuthorizationService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var grantType = form["grant_type"].ToString();

            try
            {
                if (string.Equals(grantType, SqlOSOAuthGrantTypes.DeviceCode, StringComparison.Ordinal))
                {
                    var deviceResult = await deviceAuthorizationService.PollAsync(
                        new SqlOSDeviceTokenPollRequest(
                            form["client_id"].ToString(),
                            form["device_code"].ToString(),
                            form["resource"].ToString()),
                        context,
                        cancellationToken);

                    return Results.Ok(new
                    {
                        access_token = deviceResult.Tokens.AccessToken,
                        refresh_token = deviceResult.Tokens.RefreshToken,
                        token_type = "Bearer",
                        expires_in = Math.Max(1, (int)(deviceResult.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds),
                        scope = deviceResult.Scope ?? string.Empty
                    });
                }

                var result = await authorizationServerService.ExchangeAuthorizationCodeAsync(
                    new SqlOSTokenRequest(
                        grantType,
                        form["code"].ToString(),
                        form["redirect_uri"].ToString(),
                        form["client_id"].ToString(),
                        form["code_verifier"].ToString(),
                        form["refresh_token"].ToString(),
                        form["resource"].ToString()),
                    context,
                    cancellationToken);

                return Results.Ok(new
                {
                    access_token = result.Tokens.AccessToken,
                    refresh_token = result.Tokens.RefreshToken,
                    token_type = "Bearer",
                    expires_in = Math.Max(1, (int)(result.Tokens.AccessTokenExpiresAt - DateTime.UtcNow).TotalSeconds),
                    scope = result.Scope ?? string.Empty
                });
            }
            catch (SqlOSDeviceAuthorizationException ex)
            {
                return Results.BadRequest(BuildDeviceAuthorizationError(ex));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicOAuthTokenErrorAsync(context, ex, cancellationToken);
            }
        });

        if (authOptions.ClientRegistration.Dcr.Enabled)
        {
            auth.MapPost("/register", async (
                SqlOSDynamicClientRegistrationRequest request,
                SqlOSDynamicClientRegistrationService registrationService,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await registrationService.RegisterAsync(request, context, cancellationToken);
                    return Results.Json(result, statusCode: StatusCodes.Status201Created);
                }
                catch (SqlOSClientRegistrationException ex)
                {
                    var error = await MapPublicAuthErrorAsync(
                        context,
                        ex,
                        SqlOSPublicAuthErrorSurface.DynamicClientRegistration,
                        cancellationToken);
                    return Results.Json(new
                    {
                        error = error.Error,
                        error_description = error.PublicMessage
                    }, statusCode: error.StatusCode);
                }
            });
        }

        auth.MapPost("/signup", async (SqlOSSignupRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await authService.SignUpAsync(request, httpContext, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(httpContext, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken);
            }
        });

        auth.MapPost("/password/login", async (SqlOSPasswordLoginRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.LoginWithPasswordAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/email-otp/start", async (SqlOSEmailOtpStartRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestEmailOtpAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/email-otp/verify", async (SqlOSEmailOtpVerifyRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.VerifyEmailOtpAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/select-organization", async (SqlOSSelectOrganizationRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.SelectOrganizationForLoginAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/mfa/challenge/verify", async (SqlOSMfaChallengeVerifyRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.VerifyMfaChallengeAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/mfa/challenge/totp/enroll/start", async (SqlOSTotpChallengeEnrollmentStartRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(await authService.StartTotpEnrollmentForChallengeAsync(
                request.MfaToken,
                new SqlOSTotpEnrollmentStartRequest(request.DisplayName),
                cancellationToken)));

        auth.MapPost("/mfa/challenge/totp/enroll/verify", async (SqlOSTotpEnrollmentVerifyRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.VerifyTotpEnrollmentAsync(request, httpContext, cancellationToken)));

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

        auth.MapPost("/password/forgot", async (SqlOSForgotPasswordRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestPasswordResetEmailAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/password/reset-email", async (SqlOSSendPasswordResetEmailRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestPasswordResetEmailAsync(request, httpContext, cancellationToken)));

        auth.MapGet("/password/reset", (HttpContext context) =>
            Results.Content(
                BuildPasswordResetPage(context.Request.Query["token"].ToString(), error: null, success: false),
                contentType: "text/html"));

        auth.MapPost("/password/reset/submit", async (HttpContext context, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var token = form["token"].ToString();
            var newPassword = form["newPassword"].ToString();
            var confirmPassword = form["confirmPassword"].ToString();

            try
            {
                if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Passwords do not match.");
                }

                await authService.ResetPasswordAsync(new SqlOSResetPasswordRequest(token, newPassword), cancellationToken);
                return Results.Content(BuildPasswordResetPage(token: null, error: null, success: true), contentType: "text/html");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Content(
                    BuildPasswordResetPage(token, await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken), success: false),
                    contentType: "text/html",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

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

        static async Task<IResult> HandleSamlAcsAsync(
            string connectionId,
            HttpContext httpContext,
            SqlOSSamlService samlService,
            SqlOSHeadlessAuthService headlessAuthService,
            CancellationToken cancellationToken)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var samlResponse = form["SAMLResponse"].ToString();
            var relayState = form["RelayState"].ToString();
            if (string.IsNullOrWhiteSpace(samlResponse) || string.IsNullOrWhiteSpace(relayState))
            {
                return Results.BadRequest(new { error = "SAMLResponse and RelayState are required." });
            }

            try
            {
                var redirectUrl = await samlService.HandleAcsAsync(connectionId, samlResponse, relayState, httpContext, cancellationToken);
                return Results.Redirect(redirectUrl);
            }
            catch (InvalidOperationException ex)
            {
                var error = await MapPublicAuthErrorAsync(
                    httpContext,
                    ex,
                    SqlOSPublicAuthErrorSurface.SamlAcs,
                    cancellationToken);
                var headlessErrorRedirect = await headlessAuthService.TryBuildUiUrlForAuthorizationRequestAsync(
                    httpContext,
                    relayState,
                    "login",
                    error.PublicMessage,
                    pendingToken: null,
                    email: null,
                    displayName: null,
                    cancellationToken);
                if (headlessErrorRedirect != null)
                {
                    return Results.Redirect(headlessErrorRedirect);
                }

                return Results.Json(new
                {
                    error = error.Error,
                    message = error.PublicMessage
                }, statusCode: error.StatusCode);
            }
        }

        auth.MapPost("/saml/acs/{connectionId}", HandleSamlAcsAsync);

        var ssoPortal = adminRoot.MapGroup("/sso-portal");
        ssoPortal.ExcludeFromDescription();
        MapSsoPortalEndpoints(adminApi, ssoPortal, ssoSetupApi);

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
                return Results.Content(SqlOSSsoPortalPageRenderer.RenderStartError(ex.Message), "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            }
        });

        portal.MapGet("", (SqlOSSsoPortalService portalService) => portalService.IsHostedPortalEnabled
            ? Results.Content(SqlOSSsoPortalPageRenderer.RenderShell(), "text/html; charset=utf-8")
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
                        new SqlOSAuthorizationUrlRequest(state.Connection.Id, request.ClientId, request.RedirectUri),
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

        api.MapGet("/users/{userId}/applications", async (HttpContext context, string userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.ListApplicationsForUserAsync(userId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/users/{userId}/password-reset-email", async (HttpContext context, string userId, SqlOSSendUserPasswordResetEmailRequest request, SqlOSAuthService authService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await authService.SendPasswordResetEmailForUserAsync(userId, request, context, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
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

        api.MapGet("/organizations/{organizationId}/applications", async (HttpContext context, string organizationId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.ListApplicationsForOrganizationAsync(organizationId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
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

        api.MapGet("/organizations/{organizationId}/invitations", async (HttpContext context, string organizationId, int? page, int? pageSize, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.ListOrganizationInvitationsAsync(organizationId, page, pageSize, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/organizations/{organizationId}/invitations", async (HttpContext context, string organizationId, CreateOrganizationInvitationRequest request, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.CreateEmailInvitationAsync(
                    new SqlOSCreateEmailInvitationRequest(
                        organizationId,
                        request.Email,
                        string.IsNullOrWhiteSpace(request.Role) ? "member" : request.Role,
                        request.ClientId,
                        request.RedirectUri,
                        request.Scope,
                        request.Resource,
                        request.ExpiresAt,
                        request.CustomFields,
                        request.InvitedByUserId,
                        request.SendEmail ?? true),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/invitations/{invitationId}/resend", async (HttpContext context, string invitationId, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.ResendEmailInvitationAsync(
                    new SqlOSResendEmailInvitationRequest(invitationId),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/invitations/{invitationId}/revoke", async (HttpContext context, string invitationId, RevokeInvitationRequest request, SqlOSInvitationService invitationService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await invitationService.RevokeEmailInvitationAsync(
                    new SqlOSRevokeEmailInvitationRequest(invitationId, request.Reason),
                    context,
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
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

        api.MapGet("/clients", async (HttpContext context, string? source, string? status, string? search, int? page, int? pageSize, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            return Results.Ok(await adminService.ListClientsAsync(source, status, search, page, pageSize, cancellationToken));
        });

        api.MapGet("/clients/{clientId}", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.GetClientDetailAsync(clientId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/applications/{applicationId}/assignments", async (HttpContext context, string applicationId, bool? includeRevoked, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.ListApplicationAssignmentsAsync(applicationId, includeRevoked == true, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/applications/{applicationId}/access-mode", async (HttpContext context, string applicationId, SqlOSSetApplicationAccessModeRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.SetApplicationAccessModeAsync(applicationId, request, cancellationToken: cancellationToken);
                return Results.Ok(new { client.Id, client.ClientId, client.Name, client.AccessMode, client.IsActive, client.DisabledAt, client.DisabledReason });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/applications/{applicationId}/assignments", async (HttpContext context, string applicationId, SqlOSCreateApplicationAssignmentRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var assignment = await adminService.AssignApplicationAsync(applicationId, request, cancellationToken: cancellationToken);
                return Results.Ok(new
                {
                    assignment.Id,
                    assignment.ClientApplicationId,
                    assignment.OrganizationId,
                    assignment.PrincipalType,
                    assignment.PrincipalId,
                    assignment.RoleKey,
                    assignment.Access,
                    assignment.Reason,
                    assignment.CreatedAt,
                    assignment.RevokedAt
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapDelete("/applications/{applicationId}/assignments/{assignmentId}", async (HttpContext context, string applicationId, string assignmentId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var assignment = await adminService.RevokeApplicationAssignmentAsync(applicationId, assignmentId, null, cancellationToken);
                return Results.Ok(new { assignment.Id, assignment.RevokedAt, assignment.RevokedByActorType, assignment.RevokedByActorId });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/applications/{applicationId}/access/check", async (HttpContext context, string applicationId, string? organizationId, string? userId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await adminService.CheckApplicationAccessAsync(applicationId, userId, organizationId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients", async (HttpContext context, SqlOSCreateClientRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.CreateClientAsync(request, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.Name,
                    client.Audience,
                    client.AccessMode,
                    client.AllowNativeHeadlessAuth,
                    client.AllowDeviceAuthorization,
                    RedirectUris = SqlOSAdminService.DeserializeJsonList(client.RedirectUrisJson),
                    client.IsActive,
                    client.CreatedAt
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/disable", async (HttpContext context, string clientId, ClientLifecycleRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.DisableClientAsync(clientId, request.Reason, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.IsActive,
                    client.DisabledAt,
                    client.DisabledReason
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/enable", async (HttpContext context, string clientId, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var client = await adminService.EnableClientAsync(clientId, cancellationToken);
                return Results.Ok(new
                {
                    client.Id,
                    client.ClientId,
                    client.IsActive,
                    client.DisabledAt,
                    client.DisabledReason
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/clients/{clientId}/revoke", async (HttpContext context, string clientId, ClientLifecycleRequest request, SqlOSAdminService adminService, IOptions<SqlOSAuthServerOptions> options, IHostEnvironment environment, CancellationToken cancellationToken) =>
        {
            if (!await IsAdminAuthorizedAsync(context, options.Value, environment))
            {
                return Results.NotFound();
            }

            try
            {
                var revokedSessions = await adminService.RevokeClientSessionsAsync(clientId, string.IsNullOrWhiteSpace(request.Reason) ? "client_revoked" : request.Reason.Trim(), cancellationToken);
                return Results.Ok(new { revokedSessions });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
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

    private static IResult Html(SqlOSAuthPageViewModel model, int statusCode = StatusCodes.Status200OK)
        => Results.Content(SqlOSAuthPageRenderer.RenderPage(model), contentType: "text/html", statusCode: statusCode);

    private static string BuildPasswordResetPage(string? token, string? error, bool success)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        var errorMarkup = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"""<div class="callout error">{H(error)}</div>""";
        var body = success
            ? """
              <div class="state-card">
                <strong>Password updated.</strong>
                <p>You can close this tab and sign in with your new password.</p>
              </div>
              """
            : $$"""
              <form method="post" action="reset/submit">
                <input type="hidden" name="token" value="{{H(token)}}" />
                <label>
                  <span>New password</span>
                  <input name="newPassword" type="password" autocomplete="new-password" required />
                </label>
                <label>
                  <span>Confirm password</span>
                  <input name="confirmPassword" type="password" autocomplete="new-password" required />
                </label>
                <button type="submit">Reset password</button>
              </form>
              """;

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Reset password</title>
          <style>
            body { margin:0; min-height:100vh; display:grid; place-items:center; background:#f8fafc; color:#0f172a; font-family:Segoe UI,Arial,sans-serif; }
            main { width:min(440px, calc(100vw - 32px)); background:#fff; border:1px solid #e2e8f0; border-radius:20px; padding:28px; box-shadow:0 24px 70px rgba(15,23,42,.10); }
            h1 { margin:0 0 8px; font-size:28px; line-height:1.1; }
            p { margin:0 0 20px; color:#475569; line-height:1.5; }
            form { display:grid; gap:16px; }
            label { display:grid; gap:8px; font-size:13px; font-weight:700; color:#334155; }
            input { border:1px solid #cbd5e1; border-radius:10px; padding:12px; font:inherit; }
            button { border:0; border-radius:10px; padding:12px 16px; font:inherit; font-weight:700; color:white; background:#2563eb; cursor:pointer; }
            .callout { border-radius:12px; padding:12px; margin:0 0 16px; font-size:14px; line-height:1.4; }
            .error { background:#fef2f2; color:#991b1b; border:1px solid #fecaca; }
            .state-card strong { display:block; margin:0 0 8px; font-size:20px; }
          </style>
        </head>
        <body>
          <main>
            <h1>Reset password</h1>
            <p>Choose a new password for your account.</p>
            {{errorMarkup}}
            {{body}}
          </main>
        </body>
        </html>
        """;
    }

    private static async Task<IResult> RenderMfaChallengeAsync(
        SqlOSAuthorizationRequestLoginResult completion,
        string? authorizationRequestId,
        string? email,
        string authPrefix,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSAuthService authService,
        CancellationToken cancellationToken,
        string? error = null,
        string? invitationToken = null,
        SqlOSInvitationService? invitationService = null,
        string? phoneNumber = null)
    {
        if (string.IsNullOrWhiteSpace(completion.MfaToken))
        {
            throw new InvalidOperationException("MFA challenge is invalid.");
        }

        if (completion.RequiresMfaEnrollment)
        {
            var enrollment = await authService.StartTotpEnrollmentForChallengeAsync(
                completion.MfaToken,
                new SqlOSTotpEnrollmentStartRequest(),
                cancellationToken);
            var enrollmentPage = await BuildAuthPageViewModelAsync(
                "mfa-enroll",
                authorizationRequestId,
                email,
                error,
                null,
                null,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                invitationToken: invitationToken,
                invitationService: invitationService,
                phoneNumber: phoneNumber,
                mfaToken: completion.MfaToken,
                mfaMethods: completion.MfaMethods,
                enrollmentToken: enrollment.EnrollmentToken,
                totpSecret: enrollment.Secret,
                totpProvisioningUri: enrollment.ProvisioningUri,
                totpQrCodeDataUrl: enrollment.QrCodeDataUrl);
            return Html(enrollmentPage, string.IsNullOrWhiteSpace(error) ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        }

        var page = await BuildAuthPageViewModelAsync(
            "mfa",
            authorizationRequestId,
            email,
            error,
            null,
            null,
            authPrefix,
            authorizationServerService,
            cancellationToken,
            invitationToken: invitationToken,
            invitationService: invitationService,
            phoneNumber: phoneNumber,
            mfaToken: completion.MfaToken,
            mfaMethods: completion.MfaMethods);
        return Html(page, string.IsNullOrWhiteSpace(error) ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }

    private static async Task<SqlOSAuthPageViewModel> BuildAuthPageViewModelAsync(
        string mode,
        string? authorizationRequestId,
        string? email,
        string? error,
        string? displayName,
        string? pendingToken,
        string authPrefix,
        SqlOSAuthorizationServerService authorizationServerService,
        CancellationToken cancellationToken,
        IReadOnlyList<SqlOSOrganizationOption>? organizationSelection = null,
        string? info = null,
        string? challengeToken = null,
        string? signupToken = null,
        string? invitationToken = null,
        SqlOSEmailInvitationResult? invitation = null,
        SqlOSInvitationService? invitationService = null,
        string? deviceUserCode = null,
        SqlOSDeviceAuthorizationResolveResult? deviceAuthorization = null,
        string? phoneNumber = null,
        string? mfaToken = null,
        IReadOnlyList<string>? mfaMethods = null,
        string? enrollmentToken = null,
        string? totpSecret = null,
        string? totpProvisioningUri = null,
        string? totpQrCodeDataUrl = null)
    {
        var settings = await authorizationServerService.GetAuthPageSettingsAsync(cancellationToken);
        var isDeviceView = string.Equals(mode, "device", StringComparison.OrdinalIgnoreCase)
            || mode.StartsWith("device-", StringComparison.OrdinalIgnoreCase);
        var effectiveDeviceUserCode = deviceUserCode ?? (isDeviceView ? deviceAuthorization?.UserCode : null);
        if (invitation == null && invitationService != null && !string.IsNullOrWhiteSpace(invitationToken))
        {
            invitation = await invitationService.ResolveEmailInvitationAsync(invitationToken, cancellationToken: cancellationToken);
            email ??= invitation.Email;
        }

        if (invitation == null && invitationService != null && !string.IsNullOrWhiteSpace(authorizationRequestId))
        {
            var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
            if (authorizationRequest != null)
            {
                invitation = await invitationService.GetBoundInvitationAsync(authorizationRequest, cancellationToken);
                email ??= invitation?.Email;
            }
        }

        var providerBasePath = authorizationRequestId == null
            ? null
            : $"{authPrefix}/login/oidc/{{0}}?request={Uri.EscapeDataString(authorizationRequestId)}&email={Uri.EscapeDataString(email ?? string.Empty)}";
        if (!string.IsNullOrWhiteSpace(invitationToken) && providerBasePath != null)
        {
            providerBasePath += $"&invitationToken={Uri.EscapeDataString(invitationToken)}";
        }
        if (!string.IsNullOrWhiteSpace(effectiveDeviceUserCode) && providerBasePath != null)
        {
            providerBasePath += $"&deviceUserCode={Uri.EscapeDataString(effectiveDeviceUserCode)}";
        }

        var providers = providerBasePath == null
            ? Array.Empty<SqlOSAuthPageProviderLink>()
            : (await authorizationServerService.ListEnabledOidcProvidersAsync(cancellationToken))
                .Select(provider => new SqlOSAuthPageProviderLink(
                    provider.ConnectionId,
                    provider.DisplayName,
                    string.Format(providerBasePath, provider.ConnectionId),
                    provider.LogoDataUrl))
                .ToArray();

        return new SqlOSAuthPageViewModel(
            mode,
            settings,
            authPrefix,
            authorizationRequestId,
            email,
            displayName,
            error,
            info,
            pendingToken,
            organizationSelection ?? Array.Empty<SqlOSOrganizationOption>(),
            providers,
            challengeToken,
            signupToken,
            invitationToken,
            invitation,
            effectiveDeviceUserCode,
            deviceAuthorization,
            phoneNumber,
            mfaToken,
            mfaMethods,
            enrollmentToken,
            totpSecret,
            totpProvisioningUri,
            totpQrCodeDataUrl);
    }

    private static string ResolvePreferredLocalView(SqlOSResolvedCredentialSettings credentialSettings)
    {
        if (credentialSettings.EmailOtpEnabled)
        {
            return "email-otp";
        }

        if (credentialSettings.PhoneOtpEnabled)
        {
            return "phone-otp";
        }

        if (credentialSettings.PasswordEnabled)
        {
            return "password";
        }

        return "login";
    }

    private static bool SupportsDatabaseTransactions(ISqlOSAuthServerDbContext context)
        => !string.Equals(context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private static string? ReadInvitationToken(HttpContext context, IFormCollection? form = null)
    {
        var formValue = form?["invitationToken"].ToString();
        if (!string.IsNullOrWhiteSpace(formValue))
        {
            return formValue;
        }

        var queryValue = context.Request.Query["invitationToken"].ToString();
        if (!string.IsNullOrWhiteSpace(queryValue))
        {
            return queryValue;
        }

        queryValue = context.Request.Query["invitation_token"].ToString();
        if (!string.IsNullOrWhiteSpace(queryValue))
        {
            return queryValue;
        }

        queryValue = context.Request.Query["token"].ToString();
        return string.IsNullOrWhiteSpace(queryValue) ? null : queryValue;
    }

    private static string? ReadDeviceUserCode(HttpContext context, IFormCollection? form = null)
    {
        var formValue = form?["deviceUserCode"].ToString();
        if (!string.IsNullOrWhiteSpace(formValue))
        {
            return formValue.Trim();
        }

        var queryValue = context.Request.Query["deviceUserCode"].ToString();
        if (string.IsNullOrWhiteSpace(queryValue))
        {
            queryValue = context.Request.Query["user_code"].ToString();
        }

        return string.IsNullOrWhiteSpace(queryValue) ? null : queryValue.Trim();
    }

    private static string? ReadRequestId(HttpContext context, IFormCollection? form = null)
    {
        var formValue = form?["requestId"].ToString();
        if (!string.IsNullOrWhiteSpace(formValue))
        {
            return formValue.Trim();
        }

        var queryValue = context.Request.Query["request"].ToString();
        return string.IsNullOrWhiteSpace(queryValue) ? null : queryValue.Trim();
    }

    private static IResult RedirectAfterStandaloneSignIn(string authPrefix, string status, string? deviceUserCode)
    {
        if (!string.IsNullOrWhiteSpace(deviceUserCode))
        {
            return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                $"{authPrefix}/device",
                "user_code",
                deviceUserCode));
        }

        return Results.Redirect($"{authPrefix}/login?status={status}");
    }

    private static async Task<SqlOSPublicAuthError> MapPublicAuthErrorAsync(
        HttpContext context,
        Exception exception,
        SqlOSPublicAuthErrorSurface surface,
        CancellationToken cancellationToken)
    {
        var error = SqlOSPublicAuthErrorMapper.Map(exception, surface);
        var adminService = context.RequestServices.GetService<SqlOSAdminService>();
        if (adminService != null)
        {
            await SqlOSPublicAuthErrorAudit.RecordIfDiagnosticAsync(
                adminService,
                context,
                surface,
                exception,
                error,
                cancellationToken);
        }

        return error;
    }

    private static async Task<string> PublicAuthMessageAsync(
        HttpContext context,
        Exception exception,
        SqlOSPublicAuthErrorSurface surface,
        CancellationToken cancellationToken)
        => (await MapPublicAuthErrorAsync(context, exception, surface, cancellationToken)).PublicMessage;

    private static async Task<IResult> PublicAuthJsonErrorAsync(
        HttpContext context,
        Exception exception,
        SqlOSPublicAuthErrorSurface surface,
        CancellationToken cancellationToken)
    {
        var error = await MapPublicAuthErrorAsync(context, exception, surface, cancellationToken);
        return Results.Json(new
        {
            error = error.Error,
            message = error.PublicMessage
        }, statusCode: error.StatusCode);
    }

    private static async Task<IResult> PublicOAuthTokenErrorAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var error = await MapPublicAuthErrorAsync(
            context,
            exception,
            SqlOSPublicAuthErrorSurface.OAuthToken,
            cancellationToken);
        return Results.Json(new
        {
            error = error.Error,
            error_description = error.PublicMessage
        }, statusCode: error.StatusCode);
    }

    private static object BuildDeviceAuthorizationError(SqlOSDeviceAuthorizationException exception)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = exception.Error,
            ["error_description"] = exception.Message
        };
        if (exception.Interval.HasValue)
        {
            payload["interval"] = exception.Interval.Value;
        }

        return payload;
    }

    private static async Task<SqlOSEmailInvitationResult?> BindInvitationIfPresentAsync(
        SqlOSInvitationService invitationService,
        SqlOSAuthorizationRequest? authorizationRequest,
        string? invitationToken,
        CancellationToken cancellationToken)
    {
        if (authorizationRequest == null || string.IsNullOrWhiteSpace(invitationToken))
        {
            return null;
        }

        return await invitationService.BindInvitationToAuthorizationRequestAsync(invitationToken, authorizationRequest, cancellationToken);
    }

    private static async Task<SqlOSEmailInvitationResult?> ResolveStandaloneInvitationAsync(
        SqlOSInvitationService invitationService,
        SqlOSAuthorizationRequest? authorizationRequest,
        string? invitationToken,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return authorizationRequest != null || string.IsNullOrWhiteSpace(invitationToken)
            ? null
            : await invitationService.ResolveEmailInvitationAsync(invitationToken, context, cancellationToken);
    }

    private static async Task<IResult?> RedirectToSsoIfRequiredAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string email,
        SqlOSHomeRealmDiscoveryService discoveryService,
        SqlOSSamlService samlService,
        ISqlOSAuthServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (authorizationRequest == null)
        {
            return null;
        }

        var discovery = await discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(email), cancellationToken);
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

        return string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(discovery.ConnectionId)
            ? Results.Redirect(await samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken))
            : null;
    }

    private sealed record LogoutRequest(string? RefreshToken, string? SessionId);
    private sealed record CreateOrganizationInvitationRequest(
        string Email,
        string Role,
        string? ClientId,
        string? RedirectUri,
        string? Scope,
        string? Resource,
        DateTime? ExpiresAt,
        JsonObject? CustomFields,
        string? InvitedByUserId,
        bool? SendEmail);
    private sealed record RevokeInvitationRequest(string? Reason);
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
        Protocol = connection.Protocol.ToString(),
        connection.DisplayName,
        connection.LogoDataUrl,
        EffectiveLogoDataUrl = SqlOSOidcProviderLogoCatalog.ResolveEffectiveLogoDataUrl(connection.ProviderType, connection.LogoDataUrl),
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
        string? ApplePrivateKeyPem,
        string? LogoDataUrl);

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
        string? ApplePrivateKeyPem,
        string? LogoDataUrl);

    private sealed record ClientLifecycleRequest(string? Reason);
}
