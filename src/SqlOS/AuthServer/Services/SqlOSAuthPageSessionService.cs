using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAuthPageSessionService
{
    private const string CookieName = "sqlos_auth_page";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;

    public SqlOSAuthPageSessionService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService)
    {
        _context = context;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
    }

    public async Task<SqlOSAuthPageSession?> TryGetSessionAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var rawToken = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var token = await _cryptoService.FindTemporaryTokenAsync("auth_page_session", rawToken, cancellationToken);
        if (token?.UserId == null)
        {
            return null;
        }

        var user = await _context.Set<SqlOSUser>()
            .FirstOrDefaultAsync(x => x.Id == token.UserId && x.IsActive, cancellationToken);
        if (user == null)
        {
            return null;
        }

        var payload = _cryptoService.DeserializePayload<AuthPageSessionPayload>(token);
        return new SqlOSAuthPageSession(
            rawToken,
            user,
            token.OrganizationId,
            payload?.AuthenticationMethod ?? "password");
    }

    public async Task SignInAsync(
        HttpContext httpContext,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        CancellationToken cancellationToken = default)
    {
        var securitySettings = await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken);
        var rawToken = await _cryptoService.CreateTemporaryTokenAsync(
            "auth_page_session",
            user.Id,
            null,
            organizationId,
            new AuthPageSessionPayload(authenticationMethod),
            securitySettings.SessionIdleTimeout,
            cancellationToken);

        httpContext.Response.Cookies.Append(CookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.Add(securitySettings.SessionIdleTimeout),
            Path = "/"
        });
    }

    public async Task SignOutAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var rawToken = httpContext.Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            await _cryptoService.ConsumeTemporaryTokenAsync("auth_page_session", rawToken, cancellationToken);
        }

        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Path = "/"
        });
    }

    private sealed record AuthPageSessionPayload(string AuthenticationMethod);
}

public sealed record SqlOSAuthPageSession(
    string RawToken,
    SqlOSUser User,
    string? OrganizationId,
    string AuthenticationMethod);
