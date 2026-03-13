using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using SqlOS.Configuration;

namespace SqlOS.Dashboard;

public sealed class SqlOSDashboardSessionService
{
    private const string SessionCookieName = "SqlOS.Dashboard.Session";
    private readonly IDataProtector _protector;

    public SqlOSDashboardSessionService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("SqlOS.Dashboard.Session.v1");
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

        if (!HasActiveSession(context))
        {
            return false;
        }

        if (authorizationCallback != null)
        {
            return await authorizationCallback(context);
        }

        return true;
    }

    public void CreateSession(
        HttpContext context,
        string cookiePath,
        TimeSpan sessionLifetime,
        bool allowInsecureCookie)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(sessionLifetime);
        var payload = JsonSerializer.Serialize(new SessionTicket(expiresAt.ToUnixTimeSeconds()));
        var protectedPayload = _protector.Protect(payload);

        context.Response.Cookies.Append(SessionCookieName, protectedPayload, new CookieOptions
        {
            HttpOnly = true,
            Secure = !allowInsecureCookie,
            SameSite = SameSiteMode.Lax,
            Path = cookiePath,
            Expires = expiresAt.UtcDateTime
        });
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

    public DateTimeOffset? GetSessionExpiry(HttpContext context)
    {
        var ticket = TryReadSession(context);
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

    public bool HasActiveSession(HttpContext context)
        => GetSessionExpiry(context).HasValue;

    private SessionTicket? TryReadSession(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var rawCookie) || string.IsNullOrWhiteSpace(rawCookie))
        {
            return null;
        }

        try
        {
            var unprotected = _protector.Unprotect(rawCookie);
            return JsonSerializer.Deserialize<SessionTicket>(unprotected);
        }
        catch
        {
            return null;
        }
    }

    private sealed record SessionTicket(long ExpiresAtUnixSeconds);
}
