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
    private static void MapPublicAccountEndpoints(RouteGroupBuilder auth, RouteGroupBuilder hostedForms, string authPrefix)
    {
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

        auth.MapPost("/magic-link/start", async (SqlOSMagicLinkStartRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestMagicLinkAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/magic-link/complete", async (SqlOSMagicLinkCompleteRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.CompleteMagicLinkAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/select-organization", async (SqlOSSelectOrganizationRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.SelectOrganizationForLoginAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/mfa/challenge/verify", async (SqlOSMfaChallengeVerifyRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await authService.VerifyMfaChallengeAsync(request, httpContext, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return await PublicAuthJsonErrorAsync(httpContext, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken);
            }
        });

        auth.MapPost("/mfa/challenge/totp/enroll/start", async (SqlOSTotpChallengeEnrollmentStartRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await authService.StartTotpEnrollmentForChallengeAsync(
                    request.MfaToken,
                    new SqlOSTotpEnrollmentStartRequest(request.DisplayName),
                    cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        auth.MapPost("/mfa/challenge/totp/enroll/verify", async (SqlOSTotpEnrollmentVerifyRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await authService.VerifyTotpEnrollmentAsync(request, httpContext, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        auth.MapPost("/token/refresh", async (SqlOSRefreshRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RefreshAsync(request, cancellationToken)));

        auth.MapPost("/logout", async (HttpContext context, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            var request = await context.Request.ReadFromJsonAsync<LogoutRequest>(cancellationToken: cancellationToken) ?? new LogoutRequest(null);
            await authService.LogoutByRefreshTokenAsync(request.RefreshToken, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/logout-all", async (LogoutAllRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            return await authService.LogoutAllByRefreshTokenAsync(request.RefreshToken, cancellationToken)
                ? Results.NoContent()
                : Results.Unauthorized();
        });

        auth.MapPost("/password/forgot", async (SqlOSForgotPasswordRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestPasswordResetEmailAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/password/reset-email", async (SqlOSForgotPasswordRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestPasswordResetEmailAsync(request, httpContext, cancellationToken)));

        auth.MapGet("/password/reset", (HttpContext context) =>
            HostedHtml(BuildPasswordResetPage(
                context.Request.Query["token"].ToString(),
                error: null,
                success: false)));

        hostedForms.MapPost("/password/reset/submit", async (HttpContext context, SqlOSAuthService authService, CancellationToken cancellationToken) =>
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
                return HostedHtml(BuildPasswordResetPage(token: null, error: null, success: true));
            }
            catch (InvalidOperationException ex)
            {
                return HostedHtml(
                    BuildPasswordResetPage(token, await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken), success: false),
                    StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/password/reset", async (SqlOSResetPasswordRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.ResetPasswordAsync(request, cancellationToken);
            return Results.NoContent();
        });

        auth.MapPost("/email/verification-token", async (SqlOSCreateVerificationTokenRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestEmailVerificationAsync(request, httpContext, cancellationToken)));

        auth.MapPost("/email/verification-email", async (SqlOSCreateVerificationTokenRequest request, SqlOSAuthService authService, HttpContext httpContext, CancellationToken cancellationToken) =>
            Results.Ok(await authService.RequestEmailVerificationAsync(request, httpContext, cancellationToken)));

        auth.MapGet("/email/verify", async (HttpContext context, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try
            {
                await authService.VerifyEmailAsync(
                    new SqlOSVerifyEmailRequest(context.Request.Query["token"].ToString()),
                    cancellationToken);
                return HostedHtml(BuildEmailVerificationPage(error: null));
            }
            catch (InvalidOperationException ex)
            {
                return HostedHtml(
                    BuildEmailVerificationPage(await PublicAuthMessageAsync(context, ex, SqlOSPublicAuthErrorSurface.HostedPage, cancellationToken)),
                    StatusCodes.Status400BadRequest);
            }
        });

        auth.MapPost("/email/verify", async (SqlOSVerifyEmailRequest request, SqlOSAuthService authService, CancellationToken cancellationToken) =>
        {
            await authService.VerifyEmailAsync(request, cancellationToken);
            return Results.NoContent();
        });
    }
}
