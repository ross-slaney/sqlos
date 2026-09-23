using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using SqlOS.Configuration;
using SqlOS.Security;

namespace SqlOS.Dashboard;

/// <summary>
/// Issues and validates the password-mode dashboard operator session cookie.
/// </summary>
/// <remarks>
/// The session ticket is protected with a Data Protection purpose derived from a
/// non-reversible fingerprint of the currently configured dashboard password. Rotating
/// <see cref="SqlOSDashboardOptions.Password"/> therefore invalidates every outstanding
/// operator cookie: a ticket protected under the previous password can no longer be
/// unprotected. The password itself is never written to the cookie, logs, or audit data.
/// </remarks>
public sealed class SqlOSDashboardSessionService
{
    private const string SessionCookieName = SqlOSCookieMutationCsrf.DashboardSessionCookieName;
    private const string SessionProtectorPurpose = "SqlOS.Dashboard.Session.v2";
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public SqlOSDashboardSessionService(IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    public bool IsPasswordMode(SqlOSDashboardAuthMode authMode)
        => authMode == SqlOSDashboardAuthMode.Password;

    public bool IsPasswordConfigured(string? configuredPassword)
        => !string.IsNullOrWhiteSpace(configuredPassword);

    public bool VerifyPassword(string configuredPassword, string providedPassword)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(configuredPassword);
        var providedBytes = Encoding.UTF8.GetBytes(providedPassword);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        bool isDevelopment,
        SqlOSDashboardAuthMode authMode,
        string? configuredPassword,
        Func<HttpContext, Task<bool>>? authorizationCallback)
    {
        // Preserve existing behavior in DevelopmentOnly mode:
        // callback overrides environment check.
        if (authMode == SqlOSDashboardAuthMode.DevelopmentOnly)
        {
            if (authorizationCallback != null)
            {
                return await authorizationCallback(context);
            }

            return isDevelopment;
        }

        if (!HasActiveSession(context, configuredPassword))
        {
            return false;
        }

        if (authorizationCallback != null)
        {
            return await authorizationCallback(context);
        }

        return true;
    }

    public DateTimeOffset CreateSession(
        HttpContext context,
        string cookiePath,
        TimeSpan sessionLifetime,
        bool allowInsecureCookie,
        string configuredPassword)
    {
        if (!IsPasswordConfigured(configuredPassword))
        {
            throw new InvalidOperationException("A dashboard session requires a configured dashboard password.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(sessionLifetime);
        var payload = JsonSerializer.Serialize(new SessionTicket(expiresAt.ToUnixTimeSeconds()));
        var protectedPayload = CreateProtector(configuredPassword).Protect(payload);

        context.Response.Cookies.Append(SessionCookieName, protectedPayload, new CookieOptions
        {
            HttpOnly = true,
            Secure = !allowInsecureCookie,
            SameSite = SameSiteMode.Lax,
            Path = cookiePath,
            Expires = expiresAt.UtcDateTime
        });

        return expiresAt;
    }

    public void ClearSession(HttpContext context, string cookiePath)
    {
        context.Response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            Path = cookiePath,
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax
        });
    }

    /// <summary>
    /// Returns the expiry of the operator session cookie when it was issued under the
    /// currently configured password and has not expired; otherwise <c>null</c>.
    /// </summary>
    public DateTimeOffset? GetSessionExpiry(HttpContext context, string? configuredPassword)
    {
        var ticket = TryReadSession(context, configuredPassword);
        if (ticket == null)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnixSeconds);
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return expiresAt;
    }

    public bool HasActiveSession(HttpContext context, string? configuredPassword)
        => GetSessionExpiry(context, configuredPassword).HasValue;

    private SessionTicket? TryReadSession(HttpContext context, string? configuredPassword)
    {
        // Fail closed: without a configured password there is no valid password-mode session.
        if (!IsPasswordConfigured(configuredPassword))
        {
            return null;
        }

        if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var rawCookie) || string.IsNullOrWhiteSpace(rawCookie))
        {
            return null;
        }

        try
        {
            var unprotected = CreateProtector(configuredPassword!).Unprotect(rawCookie);
            return JsonSerializer.Deserialize<SessionTicket>(unprotected);
        }
        catch
        {
            // Covers tampering, expired Data Protection keys, and tickets issued under a
            // previous dashboard password. All are treated identically as "no session".
            return null;
        }
    }

    private IDataProtector CreateProtector(string configuredPassword)
        => _dataProtectionProvider.CreateProtector(SessionProtectorPurpose, ComputePasswordFingerprint(configuredPassword));

    private static string ComputePasswordFingerprint(string configuredPassword)
    {
        // The fingerprint only ever lives in-process as a Data Protection purpose string.
        // It is never persisted, placed in the cookie, logged, or audited.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredPassword));
        return Convert.ToHexString(hash);
    }

    private sealed record SessionTicket(long ExpiresAtUnixSeconds);
}
