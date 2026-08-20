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
    private static void MapHeadlessAuthEndpoints(RouteGroupBuilder headless)
    {
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
                        SqlOSHeadlessAuthService.NormalizeUiContext(request.UiContext),
                        request.MaxAge),
                    cancellationToken);

                SqlOSEmailInvitationResult? invitation = null;
                if (!string.IsNullOrWhiteSpace(request.InvitationToken))
                {
                    invitation = await invitationService.BindInvitationToAuthorizationRequestAsync(request.InvitationToken, authorizationRequest, cancellationToken);
                }

                // OIDC prompt is a space-delimited list; recognize "none" even when
                // combined with other values. The raw string is persisted unchanged.
                var promptValues = request.Prompt?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                if (promptValues.Contains("none", StringComparer.Ordinal))
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
            string? mfaToken,
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
                    cancellationToken,
                    mfaToken));
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
                    new SqlOSForgotPasswordRequest(
                        request.Email,
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

        headless.MapPost("/magic-link/start", async (
            SqlOSHeadlessMagicLinkStartRequest request,
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
                return Results.Ok(await headlessAuthService.RequestMagicLinkAsync(context, request, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(context, ex, SqlOSPublicAuthErrorSurface.HeadlessApi, cancellationToken);
            }
        });

        headless.MapPost("/magic-link/complete", async (
            SqlOSHeadlessMagicLinkCompleteRequest request,
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
                return Results.Ok(await headlessAuthService.CompleteMagicLinkAsync(context, request, cancellationToken));
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
    }
}
