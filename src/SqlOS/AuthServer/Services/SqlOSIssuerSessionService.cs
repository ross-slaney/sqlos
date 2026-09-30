using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSIssuerSessionService
{
    private const string CookieName = "sqlos_auth_page";
    private const string FamilyItemKey = "SqlOS.IssuerSessionFamilyId";
    private const string CookieClearedItemKey = "SqlOS.IssuerSessionCookieCleared";

    public const string SessionNoLongerActiveMessage = "Authentication session is no longer active.";

    internal const string LogoutReason = "logout";
    internal const string LegacyUnlinkedReason = "legacy_unlinked";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;

    public SqlOSIssuerSessionService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService)
    {
        _context = context;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
    }

    public async Task<SqlOSIssuerSession?> TryGetSessionAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var rawToken = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSAuthLifecyclePolicy.IssuerSessionPurpose, rawToken, cancellationToken);
        if (token?.UserId == null)
        {
            return null;
        }

        if (!await EnsureFamilyIsReusableAsync(token, cancellationToken))
        {
            return null;
        }

        var lifecycle = await SqlOSAuthLifecyclePolicy.EvaluateAsync(
            _context,
            token.UserId,
            token.OrganizationId,
            cancellationToken);
        if (!lifecycle.IsActive)
        {
            var now = DateTime.UtcNow;
            await SqlOSAuthLifecyclePolicy.RevokeForDenialAsync(
                _context,
                token.UserId,
                token.OrganizationId,
                lifecycle,
                now,
                cancellationToken);
            token.ConsumedAt = now;
            SqlOSAuthLifecyclePolicy.AddDeniedAudit(
                _context,
                _cryptoService.GenerateId("aud"),
                "auth_page_session_reuse",
                lifecycle,
                token.UserId,
                token.OrganizationId);
            await _context.SaveChangesAsync(cancellationToken);
            return null;
        }

        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == token.UserId && x.IsActive, cancellationToken);
        if (user == null)
        {
            return null;
        }

        var payload = _cryptoService.DeserializePayload<IssuerSessionPayload>(token);
        var authenticatedAt = payload is { AuthenticatedAt: var stamped } && stamped != default
            ? stamped
            : token.CreatedAt;
        return new SqlOSIssuerSession(
            rawToken,
            user,
            token.OrganizationId,
            payload?.AuthenticationMethod ?? "password",
            authenticatedAt);
    }

    public Task SignInAsync(
        HttpContext httpContext,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        CancellationToken cancellationToken = default)
        => SignInAsync(httpContext, user, organizationId, authenticationMethod, authenticatedAt: null, cancellationToken);

    public Task SignInAsync(
        HttpContext httpContext,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        DateTime? authenticatedAt,
        CancellationToken cancellationToken = default)
        => SignInAsync(httpContext, user, organizationId, authenticationMethod, authenticatedAt, continueExistingSession: false, cancellationToken);

    public async Task SignInAsync(
        HttpContext httpContext,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        DateTime? authenticatedAt,
        bool continueExistingSession,
        CancellationToken cancellationToken = default)
    {
        var lifecycle = await SqlOSAuthLifecyclePolicy.EvaluateAsync(
            _context,
            user.Id,
            organizationId,
            cancellationToken);
        if (!lifecycle.IsActive)
        {
            await SqlOSAuthLifecyclePolicy.RevokeForDenialAsync(
                _context,
                user.Id,
                organizationId,
                lifecycle,
                DateTime.UtcNow,
                cancellationToken);
            SqlOSAuthLifecyclePolicy.AddDeniedAudit(
                _context,
                _cryptoService.GenerateId("aud"),
                "auth_page_session_issue",
                lifecycle,
                user.Id,
                organizationId);
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(SessionNoLongerActiveMessage);
        }

        var family = await ResolveFamilyForSignInAsync(httpContext, user.Id, organizationId, continueExistingSession, cancellationToken);
        var securitySettings = await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken);
        var rawToken = await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSAuthLifecyclePolicy.IssuerSessionPurpose,
            user.Id,
            null,
            organizationId,
            new IssuerSessionPayload(authenticationMethod, authenticatedAt ?? DateTime.UtcNow),
            securitySettings.SessionIdleTimeout,
            cancellationToken,
            family.Id);

        var revokedAt = await _context.Set<SqlOSIssuerSessionFamily>()
            .AsNoTracking()
            .Where(x => x.Id == family.Id)
            .Select(x => x.RevokedAt)
            .FirstAsync(cancellationToken);
        if (revokedAt != null)
        {
            await _cryptoService.ConsumeTemporaryTokenAsync(
                SqlOSAuthLifecyclePolicy.IssuerSessionPurpose,
                rawToken,
                cancellationToken);
            throw new InvalidOperationException(SessionNoLongerActiveMessage);
        }

        httpContext.Items[FamilyItemKey] = family.Id;
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
            var token = await FindAuthPageTokenByRawAsync(rawToken, unconsumedOnly: false, cancellationToken);
            if (token != null)
            {
                var now = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(token.IssuerSessionFamilyId))
                {
                    await RevokeFamilyAsync(token.IssuerSessionFamilyId, LogoutReason, now, cancellationToken);
                }
                else
                {
                    await ConsumeLegacyUnlinkedAsync(token, now, cancellationToken);
                }
            }
        }

        DeleteSessionCookie(httpContext);
    }

    /// <summary>
    /// Deletes the issuer session cookie the request presented when it no longer resolves to
    /// a live session (revoked family, cleaned-up token row, inactive user). A dead cookie
    /// counts as signed out, so the browser should stop presenting it instead of carrying it
    /// until the idle timeout. No-op when the request carried no issuer cookie.
    /// </summary>
    internal void ClearPresentedSessionCookie(HttpContext httpContext)
    {
        if (!string.IsNullOrWhiteSpace(httpContext.Request.Cookies[CookieName]))
        {
            DeleteSessionCookie(httpContext);
        }
    }

    private static void DeleteSessionCookie(HttpContext httpContext)
    {
        if (httpContext.Items.ContainsKey(CookieClearedItemKey))
        {
            return;
        }

        httpContext.Items[CookieClearedItemKey] = true;
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Path = "/"
        });
    }

    /// <summary>
    /// Whether a sign-in that rests on the presented issuer session (silent reuse or a
    /// renewal) may still be issued. A cookie whose token row no longer exists presents no
    /// session, exactly like no cookie. A cookie from a revoked family fails closed.
    /// </summary>
    internal async Task<bool> CanContinuePresentingSessionAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.Items[FamilyItemKey] is string issuedFamilyId
            && !string.IsNullOrWhiteSpace(issuedFamilyId))
        {
            var issuedFamily = await _context.Set<SqlOSIssuerSessionFamily>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == issuedFamilyId, cancellationToken);
            return issuedFamily is { RevokedAt: null };
        }

        var rawToken = httpContext.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return true;
        }

        var token = await FindAuthPageTokenByRawAsync(rawToken, unconsumedOnly: false, cancellationToken);
        if (token == null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token.IssuerSessionFamilyId))
        {
            return false;
        }

        var family = await _context.Set<SqlOSIssuerSessionFamily>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == token.IssuerSessionFamilyId, cancellationToken);
        return family is { RevokedAt: null };
    }

    private async Task<SqlOSIssuerSessionFamily> ResolveFamilyForSignInAsync(
        HttpContext httpContext,
        string userId,
        string? organizationId,
        bool continueExistingSession,
        CancellationToken cancellationToken)
    {
        if (httpContext.Items[FamilyItemKey] is string issuedFamilyId
            && !string.IsNullOrWhiteSpace(issuedFamilyId))
        {
            var issuedFamily = await _context.Set<SqlOSIssuerSessionFamily>()
                .FirstOrDefaultAsync(x => x.Id == issuedFamilyId, cancellationToken);
            if (issuedFamily != null && issuedFamily.RevokedAt == null)
            {
                return issuedFamily;
            }

            if (continueExistingSession)
            {
                throw new InvalidOperationException(SessionNoLongerActiveMessage);
            }
        }

        var rawToken = httpContext.Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            var presenting = await FindAuthPageTokenByRawAsync(rawToken, unconsumedOnly: true, cancellationToken);
            if (presenting != null)
            {
                if (string.IsNullOrWhiteSpace(presenting.IssuerSessionFamilyId))
                {
                    await ConsumeLegacyUnlinkedAsync(presenting, DateTime.UtcNow, cancellationToken);
                    if (continueExistingSession)
                    {
                        throw new InvalidOperationException(SessionNoLongerActiveMessage);
                    }
                }
                else
                {
                    var existing = await _context.Set<SqlOSIssuerSessionFamily>()
                        .FirstOrDefaultAsync(x => x.Id == presenting.IssuerSessionFamilyId, cancellationToken);
                    if (existing == null || existing.RevokedAt != null)
                    {
                        if (continueExistingSession)
                        {
                            throw new InvalidOperationException(SessionNoLongerActiveMessage);
                        }
                    }
                    else
                    {
                        return existing;
                    }
                }
            }
            else if (continueExistingSession)
            {
                // A consumed or expired row fails a renewal closed. A row that no longer
                // exists (startup cleanup deletes consumed and expired rows) presents no
                // session at all: the sign-in starts a new family below and never revives
                // the family the deleted row belonged to.
                var consumed = await FindAuthPageTokenByRawAsync(rawToken, unconsumedOnly: false, cancellationToken);
                if (consumed != null)
                {
                    throw new InvalidOperationException(SessionNoLongerActiveMessage);
                }
            }
        }

        var family = new SqlOSIssuerSessionFamily
        {
            Id = _cryptoService.GenerateId("aps"),
            UserId = userId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow
        };
        _context.Set<SqlOSIssuerSessionFamily>().Add(family);
        await _context.SaveChangesAsync(cancellationToken);
        return family;
    }

    private async Task<bool> EnsureFamilyIsReusableAsync(SqlOSTemporaryToken token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.IssuerSessionFamilyId))
        {
            await ConsumePresentingTokenAsync(token, cancellationToken);
            return false;
        }

        var family = await _context.Set<SqlOSIssuerSessionFamily>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == token.IssuerSessionFamilyId, cancellationToken);
        if (family == null || family.RevokedAt != null)
        {
            await ConsumePresentingTokenAsync(token, cancellationToken);
            return false;
        }

        return true;
    }

    private async Task RevokeFamilyAsync(
        string familyId,
        string reason,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var family = await _context.Set<SqlOSIssuerSessionFamily>()
            .FirstOrDefaultAsync(x => x.Id == familyId, cancellationToken);
        if (family != null && family.RevokedAt == null)
        {
            family.RevokedAt = now;
            family.RevocationReason = reason;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        var tokens = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == SqlOSAuthLifecyclePolicy.IssuerSessionPurpose
                && x.IssuerSessionFamilyId == familyId
                && x.ConsumedAt == null)
            .ToListAsync(cancellationToken);
        if (tokens.Count == 0)
        {
            return;
        }

        foreach (var sibling in tokens)
        {
            sibling.ConsumedAt = now;
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private async Task ConsumeLegacyUnlinkedAsync(
        SqlOSTemporaryToken presenting,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var unlinked = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == SqlOSAuthLifecyclePolicy.IssuerSessionPurpose
                && x.ConsumedAt == null
                && x.IssuerSessionFamilyId == null
                && x.UserId == presenting.UserId)
            .ToListAsync(cancellationToken);
        foreach (var token in unlinked)
        {
            token.ConsumedAt = now;
        }

        if (unlinked.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ConsumePresentingTokenAsync(SqlOSTemporaryToken token, CancellationToken cancellationToken)
    {
        if (token.ConsumedAt != null)
        {
            return;
        }

        token.ConsumedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private async Task<SqlOSTemporaryToken?> FindAuthPageTokenByRawAsync(
        string rawToken,
        bool unconsumedOnly,
        CancellationToken cancellationToken)
    {
        var hash = _cryptoService.HashToken(rawToken);
        var query = _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == SqlOSAuthLifecyclePolicy.IssuerSessionPurpose && x.TokenHash == hash);
        if (unconsumedOnly)
        {
            var now = DateTime.UtcNow;
            query = query.Where(x => x.ConsumedAt == null && x.ExpiresAt >= now);
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    // AuthenticatedAt defaults so cookies minted before the field existed still
    // deserialize; a default value means "unknown" and falls back to the
    // temporary token's CreatedAt.
    private sealed record IssuerSessionPayload(string AuthenticationMethod, DateTime AuthenticatedAt = default);
}

public sealed record SqlOSIssuerSession(
    string RawToken,
    SqlOSUser User,
    string? OrganizationId,
    string AuthenticationMethod,
    DateTime AuthenticatedAt);
