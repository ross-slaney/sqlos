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
    private static void MapHostedSignupEndpoints(RouteGroupBuilder auth, RouteGroupBuilder hostedForms, string authPrefix)
    {
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

        hostedForms.MapPost("/signup/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
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

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                SqlOSSignupOrchestration.RejectInvitationEmailMismatch(invitation?.Email, email);
                email = invitation?.Email ?? email;
                if (authorizationRequest != null)
                {
                    await authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
                }

                if (await RouteToIdentityProviderAsync(processes, authorizationRequest, email, cancellationToken) is { } identityProvider)
                {
                    return identityProvider;
                }

                var outcome = await processes.SignUpWithPassword(context).ExecuteAsync(
                    new SignUpWithPasswordCommand(
                        displayName,
                        email,
                        password,
                        invitation == null ? organizationName : null,
                        invitation == null ? authorizationRequest?.OrganizationId : null,
                        invitation,
                        CustomFields: null,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                return await RenderSignedUpAsync(
                    outcome,
                    authorizationRequest,
                    invitation == null ? "signed-up" : "invitation-accepted",
                    deviceUserCode,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
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

        hostedForms.MapPost("/signup/invitation/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var displayName = form["displayName"].ToString();
            var email = form["email"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken)
                    ?? throw new InvalidOperationException("Invitation is invalid or expired.");
                SqlOSSignupOrchestration.RejectInvitationEmailMismatch(invitation.Email, email);
                email = invitation.Email;
                if (authorizationRequest != null)
                {
                    await authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
                }

                if (await RouteToIdentityProviderAsync(processes, authorizationRequest, email, cancellationToken) is { } identityProvider)
                {
                    return identityProvider;
                }

                var outcome = await processes.SignUpWithInvitation(context).ExecuteAsync(
                    new SignUpWithInvitationCommand(
                        displayName,
                        invitationToken,
                        invitation,
                        CustomFields: null,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                return await RenderSignedUpAsync(
                    outcome,
                    authorizationRequest,
                    "invitation-accepted",
                    deviceUserCode,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
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

        hostedForms.MapPost("/signup/email-otp/start", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
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
                if (authorizationRequest != null)
                {
                    await authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
                }

                if (await RouteToIdentityProviderAsync(processes, authorizationRequest, email, cancellationToken) is { } identityProvider)
                {
                    return identityProvider;
                }

                var outcome = await processes.StartEmailOtpSignUp().ExecuteAsync(
                    new StartEmailOtpSignUpCommand(
                        displayName,
                        email,
                        invitation == null ? organizationName : null,
                        invitation?.CustomFields,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                var signup = outcome switch
                {
                    EmailCodeSignUpStartOutcome.Sent sent => sent.Result,
                    EmailCodeSignUpStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                    _ => throw new InvalidOperationException($"Unknown email-code sign-up start outcome '{outcome.GetType().Name}'.")
                };

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

        hostedForms.MapPost("/signup/email-otp/verify", async (
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
            var signupToken = form["signupToken"].ToString();
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
                var outcome = await processes.CompleteEmailOtpSignUp(context).ExecuteAsync(
                    new CompleteEmailOtpSignUpCommand(
                        signupToken,
                        challengeToken,
                        code,
                        invitation,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                return await RenderSignedUpAsync(
                    outcome,
                    authorizationRequest,
                    invitation == null ? "signed-up" : "invitation-accepted",
                    deviceUserCode,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
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

        hostedForms.MapPost("/signup/phone-otp/start", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
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
                var outcome = await processes.StartPhoneOtpSignUp().ExecuteAsync(
                    new StartPhoneOtpSignUpCommand(
                        displayName,
                        phoneNumber,
                        organizationName,
                        CustomFields: null,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                        CarriesInvitation: invitation != null,
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                var signup = outcome switch
                {
                    PhoneCodeSignUpStartOutcome.Sent sent => sent.Result,
                    PhoneCodeSignUpStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                    _ => throw new InvalidOperationException($"Unknown phone-code sign-up start outcome '{outcome.GetType().Name}'.")
                };

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

        hostedForms.MapPost("/signup/phone-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSAuthService authService,
            SqlOSInvitationService invitationService,
            SqlOSIdentityProcesses processes,
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

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                var outcome = await processes.CompletePhoneOtpSignUp(context).ExecuteAsync(
                    new CompletePhoneOtpSignUpCommand(
                        signupToken,
                        challengeToken,
                        code,
                        CarriesInvitation: invitation != null,
                        LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                        SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Hosted)),
                    cancellationToken);
                return await RenderSignedUpAsync(
                    outcome,
                    authorizationRequest,
                    "signed-up",
                    deviceUserCode,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
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
    }

    /// <summary>
    /// The hosted answer to a completed sign-up: the AuthPage's own sign-in redirects to its status
    /// page (or the device page), and an authorization request continues to consent, MFA, an
    /// organization choice, or the client. A refusal is answered as the 7.x exception was.
    /// </summary>
    private static async Task<IResult> RenderSignedUpAsync(
        SignUpOutcome outcome,
        SqlOSAuthorizationRequest? authorizationRequest,
        string standaloneStatus,
        string? deviceUserCode,
        string authPrefix,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSAuthService authService,
        CancellationToken cancellationToken)
    {
        var signedUp = outcome switch
        {
            SignUpOutcome.SignedUp success => success,
            SignUpOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown sign-up outcome '{outcome.GetType().Name}'.")
        };
        if (signedUp.Completion is LoginCompletion.BrowserSignedIn)
        {
            return RedirectAfterStandaloneSignIn(authPrefix, standaloneStatus, deviceUserCode);
        }

        return await RenderHostedAuthorizationCompletionAsync(
            ((LoginCompletion.AuthorizationRequestContinued)signedUp.Completion).Result,
            authorizationRequest!,
            signedUp.Evidence.User.DefaultEmail,
            authPrefix,
            authorizationServerService,
            authService,
            cancellationToken);
    }
}
