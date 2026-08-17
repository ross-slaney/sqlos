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
using SqlOS.Security;

namespace SqlOS.AuthServer.Extensions;

public static partial class EndpointRouteBuilderExtensions
{
    private static IResult Html(SqlOSAuthPageViewModel model, int statusCode = StatusCodes.Status200OK)
        => HostedHtml(SqlOSAuthPageRenderer.RenderPage(model), statusCode);

    private static IResult HostedHtml(string html, int statusCode = StatusCodes.Status200OK)
        => new SqlOSHostedHtmlResult(html, statusCode);

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
          <meta name="referrer" content="same-origin" />
          <title>Reset password</title>
          <style {{SqlOSCspNonce.Attribute}}>
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

    private static string BuildEmailVerificationPage(string? error)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        var succeeded = string.IsNullOrWhiteSpace(error);
        var title = succeeded ? "Email verified" : "Verification failed";
        var message = succeeded
            ? "Your email is verified. You can close this tab and continue signing in."
            : H(error);
        var stateClass = succeeded ? "success" : "error";

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{title}}</title>
          <style {{SqlOSCspNonce.Attribute}}>
            body { margin:0; min-height:100vh; display:grid; place-items:center; background:#f8fafc; color:#0f172a; font-family:Segoe UI,Arial,sans-serif; }
            main { width:min(440px, calc(100vw - 32px)); background:#fff; border:1px solid #e2e8f0; border-radius:20px; padding:28px; box-shadow:0 24px 70px rgba(15,23,42,.10); }
            h1 { margin:0 0 10px; font-size:28px; line-height:1.1; }
            p { margin:0; color:#475569; line-height:1.5; }
            .success { border-left:4px solid #16a34a; padding-left:16px; }
            .error { border-left:4px solid #dc2626; padding-left:16px; }
          </style>
        </head>
        <body>
          <main class="{{stateClass}}">
            <h1>{{title}}</h1>
            <p>{{message}}</p>
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
            var enrollment = await authService.StartTotpEnrollmentForAuthorizationChallengeAsync(
                completion.MfaToken,
                authorizationRequestId ?? throw new InvalidOperationException("MFA authorization request is invalid."),
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

    private static async Task<IResult> RenderHostedAuthorizationCompletionAsync(
        SqlOSAuthorizationRequestLoginResult completion,
        SqlOSAuthorizationRequest authorizationRequest,
        string? email,
        string authPrefix,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSAuthService authService,
        CancellationToken cancellationToken)
    {
        if (completion.RequiresMfa)
        {
            return await RenderMfaChallengeAsync(
                completion,
                authorizationRequest.Id,
                email,
                authPrefix,
                authorizationServerService,
                authService,
                cancellationToken);
        }

        if (completion.RequiresOrganizationSelection)
        {
            return Html(await BuildAuthPageViewModelAsync(
                "organization",
                authorizationRequest.Id,
                email,
                error: null,
                displayName: null,
                pendingToken: completion.PendingToken,
                authPrefix,
                authorizationServerService,
                cancellationToken,
                organizationSelection: completion.Organizations));
        }

        return Results.Redirect(completion.RedirectUrl
            ?? throw new InvalidOperationException("Authorization completion did not produce a redirect."));
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

        if (credentialSettings.MagicLinkEnabled)
        {
            return "magic-link";
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
}
