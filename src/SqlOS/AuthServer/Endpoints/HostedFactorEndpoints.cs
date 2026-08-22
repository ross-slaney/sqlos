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
    private static void MapHostedFactorEndpoints(RouteGroupBuilder auth, RouteGroupBuilder hostedForms, string authPrefix)
    {
        auth.MapGet("/login/magic-link", async (
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
                    "magic-link",
                    context.Request.Query["request"].ToString(),
                    invitation?.Email ?? context.Request.Query["email"].ToString(),
                    uiContext));
            }

            var page = await BuildAuthPageViewModelAsync(
                "magic-link",
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

        hostedForms.MapPost("/login/magic-link/start", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSMagicLinkService magicLinkService,
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

                var start = await magicLinkService.StartForAuthorizationRequestAsync(
                    authorizationRequest,
                    email,
                    context,
                    cancellationToken);

                var page = await BuildAuthPageViewModelAsync(
                    "magic-link-sent",
                    requestId,
                    email,
                    null,
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    info: start.Message,
                    invitationToken: invitationToken,
                    invitation: invitation,
                    invitationService: invitationService,
                    deviceUserCode: deviceUserCode);
                return Html(page);
            }
            catch (InvalidOperationException ex)
            {
                var error = await MapPublicAuthErrorAsync(
                    context,
                    ex,
                    SqlOSPublicAuthErrorSurface.HostedPage,
                    cancellationToken);
                var page = await BuildAuthPageViewModelAsync(
                    "magic-link",
                    requestId,
                    email,
                    error.PublicMessage,
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

        auth.MapGet("/login/magic-link/complete", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            CancellationToken cancellationToken) =>
        {
            var token = context.Request.Query["token"].ToString();
            if (string.IsNullOrWhiteSpace(token))
            {
                var loginPage = await BuildAuthPageViewModelAsync(
                    "login",
                    null,
                    null,
                    "The sign-in link is invalid or expired.",
                    null,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
                return Html(loginPage, StatusCodes.Status400BadRequest);
            }

            var page = await BuildAuthPageViewModelAsync(
                "magic-link-confirm",
                null,
                null,
                null,
                null,
                token,
                authPrefix,
                authorizationServerService,
                cancellationToken);
            return Html(page);
        });

        hostedForms.MapPost("/login/magic-link/complete", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSMagicLinkService magicLinkService,
            SqlOSAuthService authService,
            SqlOSAuthPageSessionService authPageSessionService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var token = form["token"].ToString();

            try
            {
                var verification = await magicLinkService.CompleteAsync(
                    new SqlOSMagicLinkCompleteRequest(token),
                    expectedAuthorizationRequestId: null,
                    requireAuthorizationRequestMatch: false,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(verification.Payload.AuthorizationRequestId))
                {
                    var organizationId = verification.Organizations.FirstOrDefault()?.Id;
                    await authPageSessionService.SignInAsync(
                        context,
                        verification.User,
                        organizationId,
                        verification.AuthenticationMethod,
                        cancellationToken);
                    return RedirectAfterStandaloneSignIn(authPrefix, "signed-in", deviceUserCode: null);
                }

                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(
                    verification.Payload.AuthorizationRequestId,
                    cancellationToken)
                    ?? throw new InvalidOperationException("The sign-in link is invalid or expired.");
                if (!string.Equals(authorizationRequest.ClientApplicationId, verification.Token.ClientApplicationId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The sign-in link is invalid or expired.");
                }

                var completion = await authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                    authorizationRequest,
                    verification.User,
                    verification.AuthenticationMethod,
                    context,
                    cancellationToken);

                if (completion.RequiresConsent)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "consent",
                        authorizationRequest.Id,
                        verification.Payload.Email,
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
                        authorizationRequest.Id,
                        verification.Payload.Email,
                        null,
                        null,
                        completion.PendingToken,
                        authPrefix,
                        authorizationServerService,
                        cancellationToken,
                        completion.Organizations);
                    return Html(organizationPage);
                }

                if (completion.RequiresMfa)
                {
                    return await RenderMfaChallengeAsync(
                        completion,
                        authorizationRequest.Id,
                        verification.Payload.Email,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        cancellationToken);
                }

                return Results.Redirect(completion.RedirectUrl!);
            }
            catch (InvalidOperationException ex)
            {
                var error = await MapPublicAuthErrorAsync(
                    context,
                    ex,
                    SqlOSPublicAuthErrorSurface.HostedPage,
                    cancellationToken);
                var page = await BuildAuthPageViewModelAsync(
                    "magic-link-confirm",
                    null,
                    null,
                    error.PublicMessage,
                    null,
                    token,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken);
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

        hostedForms.MapPost("/login/phone-otp/start", async (
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

        hostedForms.MapPost("/login/phone-otp/verify", async (
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

                if (completion.RequiresConsent)
                {
                    return Html(await BuildAuthPageViewModelAsync(
                        "consent",
                        requestId,
                        email: null,
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

        hostedForms.MapPost("/login/select-organization", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var pendingToken = form["pendingToken"].ToString();
            var organizationId = form["organizationId"].ToString();
            try
            {
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
            }
            catch (InvalidOperationException ex)
            {
                // Pre-consumption rejections (for example a lapsed max_age) leave the
                // pending token unconsumed; re-render the chooser with the safe public
                // message instead of surfacing a 500.
                IReadOnlyList<SqlOSOrganizationOption>? organizations = null;
                try
                {
                    organizations = (await authorizationServerService.GetPendingOrganizationSelectionForLoginAsync(
                        pendingToken,
                        requestId,
                        cancellationToken)).Organizations;
                }
                catch (InvalidOperationException)
                {
                    // The pending token itself is invalid or already consumed.
                }

                var page = await BuildAuthPageViewModelAsync(
                    "organization",
                    requestId,
                    email: null,
                    error: await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName: null,
                    pendingToken: pendingToken,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    organizations);
                return Html(page, StatusCodes.Status400BadRequest);
            }
        });

        hostedForms.MapPost("/mfa/verify", async (
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

        hostedForms.MapPost("/mfa/totp/enroll/verify", async (
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
                var redirectUrl = await authorizationServerService.VerifyMfaTotpEnrollmentAsync(
                    mfaToken,
                    enrollmentToken,
                    code,
                    requestId,
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
                var publicMessage = await PublicAuthMessageAsync(
                    context,
                    ex,
                    SqlOSPublicAuthErrorSurface.HostedPage,
                    cancellationToken);
                try
                {
                    return await RenderMfaChallengeAsync(
                        completion,
                        requestId,
                        email: null,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        cancellationToken,
                        error: publicMessage);
                }
                catch (InvalidOperationException)
                {
                    return Results.BadRequest(publicMessage);
                }
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
    }
}
