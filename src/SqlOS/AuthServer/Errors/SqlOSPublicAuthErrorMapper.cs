using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Errors;

public static class SqlOSPublicAuthErrorMapper
{
    public const string DefaultRequestMessage = "The request could not be completed.";
    public const string DefaultAuthorizationRequestMessage = "The authorization request is invalid.";
    public const string DefaultGrantMessage = "The authorization grant is invalid or expired.";
    public const string DefaultExternalSignInMessage = "The external sign-in request could not be completed.";
    public const string DefaultExternalProviderMessage = "The external identity provider returned an error.";

    private static readonly HashSet<string> SafePublicMessages = new(StringComparer.Ordinal)
    {
        "A PKCE code challenge is required.",
        "A state value is required.",
        "An account already exists for this email. Sign in with an email code instead.",
        "An account already exists for this phone number.",
        "An account already exists for this phone number. Sign in with a phone code instead.",
        "Authentication is older than the requested max_age.",
        "Authorization request is invalid or expired.",
        "Client application is required.",
        "Create an account to accept this invitation.",
        "Device authorization request is invalid or expired.",
        "Device user code is required.",
        "Display name cannot exceed 200 characters.",
        "Display name is required.",
        "Email address cannot exceed 320 characters.",
        "Email address is required.",
        "Email must be verified before password login.",
        "Email sign-in is unavailable.",
        "Invitation is invalid or expired.",
        "Invitation signup without a password requires Email OTP to be enabled.",
        "Local password authentication is disabled.",
        "MFA challenge is invalid.",
        "MFA challenge is invalid or expired.",
        SqlOSAuthService.MfaChallengeFailureMessage,
        "Magic-link sign-in is unavailable.",
        "max_age must be a non-negative integer.",
        "Only authorization code requests are supported.",
        "Only S256 PKCE is supported.",
        "Organization name cannot exceed 200 characters.",
        "Password is required.",
        "Password reset token is invalid or expired.",
        "Password signup is disabled.",
        "Passwords do not match.",
        "Phone number country is not allowed.",
        "Phone number is invalid.",
        "Phone sign-in is unavailable.",
        "Phone signup is not available for email invitations.",
        "prompt cannot combine none with other values.",
        SqlOSSignupOrchestration.UnauthorizedResourceMessage,
        "Sign in before approving this device request.",
        "Sign in before changing phone numbers.",
        SqlOSAuthorizationServerService.ConsentClientMetadataChangedMessage,
        "The device authorization request is invalid or expired.",
        "The organization selection session is invalid or expired.",
        "The selected organization is not available to this user.",
        "The selected organization requires MFA.",
        "The sign-in code is invalid or expired.",
        "The sign-in link is invalid or expired.",
        SqlOSSignupOrchestration.InvitationEmailMismatchMessage,
        "This device request is no longer pending.",
        "Too many sign-in code requests. Try again later.",
        "Too many sign-in link requests. Try again later.",
        "We couldn't send a sign-in code right now.",
        "We couldn't send a sign-in link right now.",
        "We couldn't send the invitation email right now.",
        SqlOSPasswordLoginAbuseService.PublicFailureMessage,
        "Joining an existing organization requires an invitation or approved join policy."
    };

    public static SqlOSPublicAuthError Map(Exception exception, SqlOSPublicAuthErrorSurface surface)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SqlOSPublicAuthException publicException => FromPublicException(publicException),
            SqlOSDeviceAuthorizationException deviceException => new SqlOSPublicAuthError(
                deviceException.Error,
                deviceException.Message,
                StatusCodes.Status400BadRequest,
                deviceException.Error,
                deviceException.Message,
                IsDiagnosticOnly: false),
            SqlOSClientRegistrationException registrationException => new SqlOSPublicAuthError(
                registrationException.Error,
                registrationException.Message,
                registrationException.StatusCode,
                registrationException.Error,
                registrationException.Message,
                IsDiagnosticOnly: false),
            SqlOSHeadlessValidationException headlessValidationException => new SqlOSPublicAuthError(
                "invalid_request",
                headlessValidationException.GlobalErrors.FirstOrDefault() ?? headlessValidationException.Message,
                StatusCodes.Status400BadRequest,
                "headless_validation",
                headlessValidationException.Message,
                IsDiagnosticOnly: false),
            InvalidOperationException invalidOperationException => MapInvalidOperation(invalidOperationException, surface),
            _ => MapOpaque(exception, surface, GetDefaultMessage(surface))
        };
    }

    public static async Task<SqlOSPublicAuthError> MapAndAuditAsync(
        SqlOSAdminService adminService,
        HttpContext httpContext,
        Exception exception,
        SqlOSPublicAuthErrorSurface surface,
        CancellationToken cancellationToken = default)
    {
        var error = Map(exception, surface);
        await SqlOSPublicAuthErrorAudit.RecordIfDiagnosticAsync(
            adminService,
            httpContext,
            surface,
            exception,
            error,
            cancellationToken);
        return error;
    }

    public static SqlOSPublicAuthException ProviderCallbackError(string? providerError, string? providerErrorDescription)
    {
        var diagnostic = string.IsNullOrWhiteSpace(providerErrorDescription)
            ? $"provider_error={providerError}"
            : $"provider_error={providerError}; provider_description={providerErrorDescription}";

        return new SqlOSPublicAuthException(
            NormalizeProtocolError(providerError, "access_denied"),
            DefaultExternalProviderMessage,
            StatusCodes.Status400BadRequest,
            "oidc_provider_error",
            diagnostic);
    }

    private static SqlOSPublicAuthError FromPublicException(SqlOSPublicAuthException exception)
        => new(
            exception.Error,
            exception.PublicMessage,
            exception.StatusCode,
            exception.AuditReason,
            exception.DiagnosticMessage ?? exception.Message,
            !string.IsNullOrWhiteSpace(exception.DiagnosticMessage)
                && !string.Equals(exception.DiagnosticMessage, exception.PublicMessage, StringComparison.Ordinal));

    private static SqlOSPublicAuthError MapInvalidOperation(
        InvalidOperationException exception,
        SqlOSPublicAuthErrorSurface surface)
    {
        if (IsSafePublicMessage(exception.Message))
        {
            return new SqlOSPublicAuthError(
                GetDefaultError(surface),
                exception.Message,
                StatusCodes.Status400BadRequest,
                "public_auth_validation",
                exception.Message,
                IsDiagnosticOnly: false);
        }

        return MapOpaque(exception, surface, GetDefaultMessage(surface));
    }

    private static SqlOSPublicAuthError MapOpaque(
        Exception exception,
        SqlOSPublicAuthErrorSurface surface,
        string publicMessage)
        => new(
            GetDefaultError(surface),
            publicMessage,
            StatusCodes.Status400BadRequest,
            "opaque_auth_error",
            exception.Message,
            IsDiagnosticOnly: true);

    private static bool IsSafePublicMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (SafePublicMessages.Contains(message))
        {
            return true;
        }

        return message.StartsWith("Wait ", StringComparison.Ordinal)
            && (message.EndsWith(" seconds before requesting another code.", StringComparison.Ordinal)
                || message.EndsWith(" seconds before requesting another sign-in link.", StringComparison.Ordinal));
    }

    private static string GetDefaultError(SqlOSPublicAuthErrorSurface surface)
        => surface switch
        {
            SqlOSPublicAuthErrorSurface.OAuthToken => "invalid_grant",
            SqlOSPublicAuthErrorSurface.OidcCallback => "access_denied",
            SqlOSPublicAuthErrorSurface.SamlAcs => "access_denied",
            _ => "invalid_request"
        };

    private static string GetDefaultMessage(SqlOSPublicAuthErrorSurface surface)
        => surface switch
        {
            SqlOSPublicAuthErrorSurface.OAuthAuthorize => DefaultAuthorizationRequestMessage,
            SqlOSPublicAuthErrorSurface.OAuthToken => DefaultGrantMessage,
            SqlOSPublicAuthErrorSurface.OidcCallback => DefaultExternalSignInMessage,
            SqlOSPublicAuthErrorSurface.SamlAcs => DefaultExternalSignInMessage,
            _ => DefaultRequestMessage
        };

    private static string NormalizeProtocolError(string? error, string fallback)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return fallback;
        }

        return error.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-')
            ? error
            : fallback;
    }
}
