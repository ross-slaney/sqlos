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

        hostedForms.MapPost("/signup/invitation/submit", async (
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
    }
}
