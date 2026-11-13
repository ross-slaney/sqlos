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
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
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
                SqlOSSignupOrchestration.RejectInvitationEmailMismatch(invitation?.Email, email);
                email = invitation?.Email ?? email;
                if (authorizationRequest != null)
                {
                    await authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
                }

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

                if (await SqlOSSignupOrchestration.EmailHasAccountAsync(dbContext, email, cancellationToken))
                {
                    // The wrong door: the address has an account, so its password signs it in.
                    var authentication = await SqlOSSignupOrchestration.SignInExistingAccountAsync(() =>
                        authorizationServerService.AuthenticatePasswordAsync(
                            email,
                            password,
                            cancellationToken,
                            allowUnverifiedEmailForInvitation: invitation != null,
                            httpContext: context,
                            clientKey: authorizationRequest?.ClientApplication?.ClientId ?? authorizationRequest?.ClientApplicationId,
                            authorizationRequestId: authorizationRequest?.Id,
                            surface: authorizationRequest == null ? "hosted_standalone" : "hosted"));
                    if (authorizationRequest == null)
                    {
                        var signedInOrganizationId = authentication.Organizations.FirstOrDefault()?.Id;
                        if (!string.IsNullOrWhiteSpace(invitationToken))
                        {
                            var acceptance = await invitationService.AcceptEmailInvitationAsync(
                                new SqlOSAcceptEmailInvitationRequest(invitationToken, authentication.User.Id) { AuthenticationMethod = authentication.AuthenticationMethod },
                                context,
                                cancellationToken);
                            signedInOrganizationId = acceptance.OrganizationId;
                        }

                        await issuerSessionService.SignInAsync(context, authentication.User, signedInOrganizationId, authentication.AuthenticationMethod, cancellationToken);
                        return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-in" : "invitation-accepted", deviceUserCode);
                    }

                    var signInCompletion = await authorizationServerService.CompleteCredentialSignInAsync(
                        authorizationRequest,
                        authentication.User,
                        authentication.AuthenticationMethod,
                        context,
                        cancellationToken);
                    return await RenderHostedAuthorizationCompletionAsync(
                        signInCompletion,
                        authorizationRequest,
                        authentication.User.DefaultEmail,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        cancellationToken);
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
                            new SqlOSAcceptEmailInvitationRequest(invitationToken, signup.User.Id) { AuthenticationMethod = signup.AuthenticationMethod },
                            context,
                            cancellationToken);
                        organizationId = acceptance.OrganizationId;
                    }

                    await issuerSessionService.SignInAsync(context, signup.User, organizationId, signup.AuthenticationMethod, cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-up" : "invitation-accepted", deviceUserCode);
                }

                authorizationRequest.OrganizationId ??= invitation?.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id;
                var completion = await authorizationServerService.CompleteCredentialSignInAsync(
                    authorizationRequest,
                    signup.User,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return await RenderHostedAuthorizationCompletionAsync(
                    completion,
                    authorizationRequest,
                    signup.User.DefaultEmail,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
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

        hostedForms.MapPost("/signup/invitation/submit", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
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
                        new SqlOSAcceptEmailInvitationRequest(invitationToken!, signup.User.Id) { AuthenticationMethod = signup.AuthenticationMethod },
                        context,
                        cancellationToken);

                    await issuerSessionService.SignInAsync(context, signup.User, acceptance.OrganizationId, signup.AuthenticationMethod, cancellationToken);
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return RedirectAfterStandaloneSignIn(authPrefix, "invitation-accepted", deviceUserCode);
                }

                authorizationRequest.OrganizationId ??= invitation.OrganizationId;
                var completion = await authorizationServerService.CompleteCredentialSignInAsync(
                    authorizationRequest,
                    signup.User,
                    signup.AuthenticationMethod,
                    context,
                    cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return await RenderHostedAuthorizationCompletionAsync(
                    completion,
                    authorizationRequest,
                    signup.User.DefaultEmail,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
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

        hostedForms.MapPost("/signup/email-otp/start", async (
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
                if (authorizationRequest != null)
                {
                    await authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
                }

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

        hostedForms.MapPost("/signup/email-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
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

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                email = invitation?.Email ?? email;
                // Check the code before the sign-up transaction opens: a wrong code's attempt must
                // survive the rollback below, or the code could be guessed without limit.
                var outcome = await emailOtpService.VerifySignupOrSignInAsync(
                    new SqlOSEmailOtpSignupVerifyRequest(signupToken, challengeToken, code),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                if (outcome.SignIn is { } signIn)
                {
                    // The address already has an account and the code proved it: sign that account in.
                    return await HostedEmailOtpSignInAsync(
                        context,
                        signIn,
                        authorizationRequest,
                        invitation,
                        requestId,
                        invitationToken,
                        deviceUserCode,
                        authPrefix,
                        authorizationServerService,
                        authService,
                        issuerSessionService,
                        invitationService,
                        cancellationToken);
                }

                return await HostedEmailOtpCreateAccountAsync(
                    context,
                    outcome.Signup!,
                    authorizationRequest,
                    invitation,
                    invitationToken,
                    deviceUserCode,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    issuerSessionService,
                    emailOtpService,
                    dbContext,
                    invitationService,
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

        hostedForms.MapPost("/signup/email-otp/complete", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
            SqlOSEmailOtpService emailOtpService,
            ISqlOSAuthServerDbContext dbContext,
            SqlOSInvitationService invitationService,
            CancellationToken cancellationToken) =>
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var requestId = form["requestId"].ToString();
            var email = form["email"].ToString();
            var displayName = form["displayName"].ToString();
            var organizationName = form["organizationName"].ToString();
            var signupToken = form["signupToken"].ToString();
            var invitationToken = ReadInvitationToken(context, form);
            var deviceUserCode = ReadDeviceUserCode(context, form);

            try
            {
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                var verification = await emailOtpService.VerifySignupCompletionAsync(
                    new SqlOSEmailOtpSignupCompleteRequest(signupToken, displayName, organizationName),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                return await HostedEmailOtpCreateAccountAsync(
                    context,
                    verification,
                    authorizationRequest,
                    invitation,
                    invitationToken,
                    deviceUserCode,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    issuerSessionService,
                    emailOtpService,
                    dbContext,
                    invitationService,
                    cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                var page = await BuildAuthPageViewModelAsync(
                    "email-otp-signup-complete",
                    requestId,
                    email,
                    await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken),
                    displayName,
                    null,
                    authPrefix,
                    authorizationServerService,
                    cancellationToken,
                    info: HostedSignupCompleteInfo,
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

        hostedForms.MapPost("/signup/phone-otp/verify", async (
            HttpContext context,
            SqlOSAuthorizationServerService authorizationServerService,
            SqlOSIssuerSessionService issuerSessionService,
            SqlOSAuthService authService,
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
                var authorizationRequest = await authorizationServerService.TryGetActiveAuthorizationRequestAsync(requestId, cancellationToken);
                var invitation = await BindInvitationIfPresentAsync(invitationService, authorizationRequest, invitationToken, cancellationToken)
                    ?? await ResolveStandaloneInvitationAsync(invitationService, authorizationRequest, invitationToken, context, cancellationToken);
                if (invitation != null)
                {
                    throw new InvalidOperationException("Phone signup is not available for email invitations.");
                }

                // Check the code before the sign-up transaction opens: a rejected code must keep
                // its challenge invalidated through the rollback below.
                var signupVerification = await phoneOtpService.VerifySignupAsync(
                    new SqlOSPhoneOtpSignupVerifyRequest(signupToken, challengeToken, code),
                    authorizationRequest?.Id,
                    requireAuthorizationRequestMatch: true,
                    cancellationToken);

                if (SupportsDatabaseTransactions(dbContext))
                {
                    transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                }

                var signup = await authorizationServerService.SignUpWithPhoneOtpAsync(
                    signupVerification.DisplayName,
                    signupVerification.PhoneNumber,
                    signupVerification.OrganizationName,
                    authorizationRequest?.OrganizationId ?? signupVerification.OrganizationId,
                    cancellationToken);

                if (authorizationRequest == null)
                {
                    var organizationId = signup.Organizations.FirstOrDefault()?.Id;
                    await issuerSessionService.SignInAsync(context, signup.User, organizationId, signup.AuthenticationMethod, cancellationToken);
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

                authorizationRequest.OrganizationId ??= signup.Organizations.FirstOrDefault()?.Id;
                var completion = await authorizationServerService.CompleteCredentialSignInAsync(
                    authorizationRequest,
                    signup.User,
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

                return await RenderHostedAuthorizationCompletionAsync(
                    completion,
                    authorizationRequest,
                    signup.User.DefaultEmail,
                    authPrefix,
                    authorizationServerService,
                    authService,
                    cancellationToken);
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
    }

    /// <summary>
    /// Creates the account a checked hosted sign-up proved (a sign-up code, or the name step after a
    /// sign-in code) and signs it in, in one transaction with spending the sign-up token. A failure
    /// rolls everything back and rethrows for the page to show.
    /// </summary>
    private static async Task<IResult> HostedEmailOtpCreateAccountAsync(
        HttpContext context,
        SqlOSEmailOtpSignupVerificationResult verification,
        SqlOSAuthorizationRequest? authorizationRequest,
        SqlOSEmailInvitationResult? invitation,
        string? invitationToken,
        string? deviceUserCode,
        string authPrefix,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSAuthService authService,
        SqlOSIssuerSessionService issuerSessionService,
        SqlOSEmailOtpService emailOtpService,
        ISqlOSAuthServerDbContext dbContext,
        SqlOSInvitationService invitationService,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (SupportsDatabaseTransactions(dbContext))
            {
                transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            }

            // Claim the one-use artifacts before creating the account. Concurrent
            // submissions must lose here, not at the unique email constraint.
            // A later failure rolls consumption back with the account transaction.
            if (transaction != null)
            {
                await emailOtpService.ConsumeSignupTokenAsync(verification, cancellationToken);
            }

            var signup = await authorizationServerService.SignUpWithEmailOtpAsync(
                verification.DisplayName,
                verification.Email,
                invitation == null ? verification.OrganizationName : null,
                invitation == null ? authorizationRequest?.OrganizationId ?? verification.OrganizationId : null,
                cancellationToken);

            if (authorizationRequest == null)
            {
                var organizationId = signup.Organizations.FirstOrDefault()?.Id;
                if (!string.IsNullOrWhiteSpace(invitationToken))
                {
                    var acceptance = await invitationService.AcceptEmailInvitationInCurrentTransactionAsync(
                        new SqlOSAcceptEmailInvitationRequest(invitationToken, signup.User.Id) { AuthenticationMethod = signup.AuthenticationMethod },
                        context,
                        cancellationToken);
                    organizationId = acceptance.OrganizationId;
                }

                await issuerSessionService.SignInAsync(context, signup.User, organizationId, signup.AuthenticationMethod, cancellationToken);
                if (transaction == null)
                {
                    await emailOtpService.ConsumeSignupTokenAsync(verification, cancellationToken);
                }
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return RedirectAfterStandaloneSignIn(authPrefix, invitation == null ? "signed-up" : "invitation-accepted", deviceUserCode);
            }

            authorizationRequest.OrganizationId ??= invitation?.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id;
            var completion = await authorizationServerService.CompleteCredentialSignInAsync(
                authorizationRequest,
                signup.User,
                signup.AuthenticationMethod,
                context,
                cancellationToken);

            if (transaction == null)
            {
                await emailOtpService.ConsumeSignupTokenAsync(verification, cancellationToken);
            }
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await RenderHostedAuthorizationCompletionAsync(
                completion,
                authorizationRequest,
                signup.User.DefaultEmail,
                authPrefix,
                authorizationServerService,
                authService,
                cancellationToken);
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }
}
