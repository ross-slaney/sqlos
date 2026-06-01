using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSSsoPortalService
{
    private static readonly IReadOnlyList<SqlOSSsoProviderGuide> ProviderGuides =
    [
        new(
            "microsoft-entra",
            "Microsoft Entra",
            "Federation Metadata XML",
            "Identifier (Entity ID)",
            "Reply URL (ACS URL)",
            [
                "Create an Enterprise Application, then choose SAML as the single sign-on method.",
                "Paste the SP Entity ID into Identifier and the ACS URL into Reply URL.",
                "Download or copy the Federation Metadata XML and import it here.",
                "Review the IdP Entity ID and SSO URL, activate the connection, then test sign-in."
            ]),
        new(
            "okta",
            "Okta",
            "IdP metadata",
            "Audience URI (SP Entity ID)",
            "Single sign-on URL",
            [
                "Create a SAML 2.0 application integration in Okta.",
                "Use the ACS URL as Single sign-on URL and the SP Entity ID as Audience URI.",
                "Set Name ID format to EmailAddress and map email, first_name, and last_name attributes.",
                "Copy the IdP metadata XML into this portal, activate, and run a test."
            ]),
        new(
            "google-workspace",
            "Google Workspace",
            "IdP metadata",
            "Entity ID",
            "ACS URL",
            [
                "Create a custom SAML app in Google Admin Console.",
                "Paste the ACS URL and Entity ID from this page into the service provider details.",
                "Download Google IdP metadata and import it here.",
                "Activate the connection and verify that a user in the primary domain routes to SSO."
            ]),
        new(
            "generic-saml",
            "Generic SAML",
            "SAML metadata XML",
            "SP Entity ID",
            "ACS URL",
            [
                "Create a SAML application in your identity provider.",
                "Use the SP Entity ID and ACS URL shown here as the service provider values.",
                "Export IdP metadata with a signing certificate and HTTP-Redirect or HTTP-POST SSO endpoint.",
                "Import metadata, activate the connection, and run a test from a matching email domain."
            ])
    ];

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSSsoPortalOptions _portalOptions;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAdminService _adminService;

    public SqlOSSsoPortalService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService,
        SqlOSAdminService adminService)
    {
        _context = context;
        _options = options.Value;
        _portalOptions = _options.SsoPortal;
        _cryptoService = cryptoService;
        _adminService = adminService;
    }

    public async Task<SqlOSSsoPortalSessionResult> CreateSessionAsync(
        SqlOSCreateSsoPortalSessionRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == request.OrganizationId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var now = DateTime.UtcNow;
        var expiresAt = request.ExpiresAt ?? now.Add(_portalOptions.DefaultLinkLifetime);
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("Portal session expiration must be in the future.");
        }

        var provider = NormalizeProvider(request.Provider);
        var connection = await EnsurePortalConnectionAsync(organization, cancellationToken);
        var rawLinkToken = _cryptoService.GenerateOpaqueToken();
        var session = new SqlOSSsoPortalSession
        {
            Id = _cryptoService.GenerateId("ssp"),
            OrganizationId = organization.Id,
            ConnectionId = connection.Id,
            LinkTokenHash = _cryptoService.HashToken(rawLinkToken),
            Provider = provider,
            ReturnUrl = NormalizeOptional(request.ReturnUrl),
            ActorType = "platform_admin",
            CreatedByUserId = NormalizeOptional(request.CreatedByUserId),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            IpAddress = httpContext?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString()
        };

        _context.Set<SqlOSSsoPortalSession>().Add(session);
        await _context.SaveChangesAsync(cancellationToken);

        await RecordPortalAuditAsync(
            "sso.portal.session.created",
            session,
            httpContext,
            new { session.Id, connectionId = connection.Id, provider, expiresAt },
            cancellationToken);

        session.Organization = organization;
        session.Connection = connection;
        return ToSessionResult(session, BuildSetupUrl(rawLinkToken, httpContext));
    }

    public async Task<object> ListOrganizationSessionsAsync(
        string organizationId,
        int? page = null,
        int? pageSize = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        _ = await _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var resolvedPage = Math.Max(1, page.GetValueOrDefault(1));
        var resolvedPageSize = Math.Clamp(pageSize.GetValueOrDefault(20), 1, 100);
        var query = _context.Set<SqlOSSsoPortalSession>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt);

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)resolvedPageSize));
        var data = await query
            .Skip((resolvedPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .ToListAsync(cancellationToken);

        return new
        {
            Data = data.Select(x => ToSessionResult(x, setupUrl: null)).ToList(),
            Page = resolvedPage,
            PageSize = resolvedPageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public async Task<SqlOSSsoPortalSessionResult> RevokeSessionAsync(
        string sessionId,
        SqlOSRevokeSsoPortalSessionRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadSessionByIdAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Portal session not found.");

        if (session.RevokedAt == null)
        {
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = string.IsNullOrWhiteSpace(request.Reason) ? "revoked" : request.Reason.Trim();
            await _context.SaveChangesAsync(cancellationToken);
            await RecordPortalAuditAsync(
                "sso.portal.session.revoked",
                session,
                httpContext,
                new { session.Id, session.RevokedReason },
                cancellationToken);
        }

        return ToSessionResult(session, setupUrl: null);
    }

    public async Task<SqlOSSsoPortalSessionResult> OpenSessionAsync(
        string rawLinkToken,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawLinkToken))
        {
            throw new InvalidOperationException("Portal setup token is required.");
        }

        var now = DateTime.UtcNow;
        var tokenHash = _cryptoService.HashToken(rawLinkToken.Trim());
        var session = await _context.Set<SqlOSSsoPortalSession>()
            .Include(x => x.Organization)
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.LinkTokenHash == tokenHash, cancellationToken)
            ?? throw new InvalidOperationException("Portal setup token is invalid or expired.");

        EnsureSessionCanOpen(session, now);

        var rawSessionToken = _cryptoService.GenerateOpaqueToken();
        session.SessionTokenHash = _cryptoService.HashToken(rawSessionToken);
        session.OpenedAt = now;
        session.LastSeenAt = now;
        session.IpAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        session.UserAgent = httpContext.Request.Headers.UserAgent.ToString();
        await _context.SaveChangesAsync(cancellationToken);

        SetPortalCookie(httpContext, rawSessionToken, session.ExpiresAt);
        await RecordPortalAuditAsync(
            "sso.portal.session.opened",
            session,
            httpContext,
            new { session.Id, session.ConnectionId },
            cancellationToken);

        return ToSessionResult(session, setupUrl: null);
    }

    public async Task<SqlOSSsoPortalSession?> TryGetSessionAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        if (!httpContext.Request.Cookies.TryGetValue(GetCookieName(), out var rawSessionToken)
            || string.IsNullOrWhiteSpace(rawSessionToken))
        {
            return null;
        }

        var tokenHash = _cryptoService.HashToken(rawSessionToken.Trim());
        var now = DateTime.UtcNow;
        var session = await _context.Set<SqlOSSsoPortalSession>()
            .Include(x => x.Organization)
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.SessionTokenHash == tokenHash, cancellationToken);
        if (session == null || !IsSessionUsable(session, now))
        {
            ClearPortalCookie(httpContext);
            return null;
        }

        if (session.LastSeenAt == null || session.LastSeenAt.Value.AddMinutes(1) < now)
        {
            session.LastSeenAt = now;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return session;
    }

    public async Task<SqlOSSsoPortalSession> GetRequiredSessionAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
        => await TryGetSessionAsync(httpContext, cancellationToken)
           ?? throw new InvalidOperationException("Portal session is invalid or expired.");

    public async Task SignOutAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var session = await TryGetSessionAsync(httpContext, cancellationToken);
        if (session != null)
        {
            await RecordPortalAuditAsync(
                "sso.portal.session.closed",
                session,
                httpContext,
                new { session.Id },
                cancellationToken);
        }

        ClearPortalCookie(httpContext);
    }

    public async Task<SqlOSSsoPortalStateResult> GetStateAsync(
        SqlOSSsoPortalSession session,
        CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .FirstAsync(x => x.Id == session.OrganizationId, cancellationToken);
        var connection = await EnsurePortalConnectionAsync(organization, cancellationToken, session);

        return new SqlOSSsoPortalStateResult(
            new SqlOSSsoPortalOrganizationResult(
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain),
            ToConnectionResult(connection),
            session.Provider,
            _adminService.GetServiceProviderEntityId(),
            _adminService.GetAssertionConsumerServiceUrl(connection.Id),
            ProviderGuides,
            session.LastTestedAt == null
                ? null
                : new SqlOSSsoPortalTestResult(
                    session.LastTestStatus ?? "unknown",
                    session.LastTestMessage ?? "No test details recorded.",
                    null,
                    session.LastTestedAt.Value));
    }

    public async Task<SqlOSSsoPortalStateResult> SetProviderAsync(
        SqlOSSsoPortalSession session,
        SqlOSUpdateSsoPortalProviderRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        session.Provider = NormalizeProvider(request.Provider)
            ?? throw new InvalidOperationException("Provider is required.");
        await _context.SaveChangesAsync(cancellationToken);
        await RecordPortalAuditAsync(
            "sso.portal.provider.selected",
            session,
            httpContext,
            new { session.Provider },
            cancellationToken);

        return await GetStateAsync(session, cancellationToken);
    }

    public SqlOSSsoMetadataValidationResult ValidateMetadata(SqlOSSsoPortalMetadataRequest request)
        => _adminService.ValidateSsoMetadata(new SqlOSImportSsoMetadataRequest(request.MetadataXml));

    public async Task<SqlOSSsoPortalStateResult> ImportMetadataAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalMetadataRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequirePortalConnectionAsync(session, cancellationToken);
        var updated = await _adminService.ImportSsoMetadataAsync(
            connection.Id,
            new SqlOSImportSsoMetadataRequest(request.MetadataXml),
            enableConnection: false,
            cancellationToken);

        await RecordPortalAuditAsync(
            "sso.portal.metadata.imported",
            session,
            httpContext,
            new
            {
                connectionId = updated.Id,
                identityProviderEntityId = updated.IdentityProviderEntityId,
                singleSignOnUrl = updated.SingleSignOnUrl
            },
            cancellationToken);

        return await GetStateAsync(session, cancellationToken);
    }

    public async Task<SqlOSSsoPortalStateResult> ActivateAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequirePortalConnectionAsync(session, cancellationToken);
        EnsureConnectionHasMetadata(connection);
        connection.IsEnabled = true;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        await RecordPortalAuditAsync(
            "sso.portal.connection.activated",
            session,
            httpContext,
            new { connectionId = connection.Id },
            cancellationToken);

        return await GetStateAsync(session, cancellationToken);
    }

    public async Task<SqlOSSsoPortalStateResult> DisableAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequirePortalConnectionAsync(session, cancellationToken);
        connection.IsEnabled = false;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        await RecordPortalAuditAsync(
            "sso.portal.connection.disabled",
            session,
            httpContext,
            new { connectionId = connection.Id },
            cancellationToken);

        return await GetStateAsync(session, cancellationToken);
    }

    public async Task<SqlOSSsoPortalTestResult> RecordTestAsync(
        SqlOSSsoPortalSession session,
        string status,
        string message,
        string? authorizationUrl,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        session.LastTestedAt = now;
        session.LastTestStatus = status;
        session.LastTestMessage = message;
        await _context.SaveChangesAsync(cancellationToken);

        await RecordPortalAuditAsync(
            "sso.portal.connection.tested",
            session,
            httpContext,
            new { status, message, hasAuthorizationUrl = !string.IsNullOrWhiteSpace(authorizationUrl) },
            cancellationToken);

        return new SqlOSSsoPortalTestResult(status, message, authorizationUrl, now);
    }

    public string BuildPortalUrl(HttpContext? httpContext = null)
        => $"{GetOrigin(httpContext)}{GetPortalPath()}";

    private async Task<SqlOSSsoConnection> EnsurePortalConnectionAsync(
        SqlOSOrganization organization,
        CancellationToken cancellationToken,
        SqlOSSsoPortalSession? session = null)
    {
        SqlOSSsoConnection? connection = null;
        if (!string.IsNullOrWhiteSpace(session?.ConnectionId))
        {
            connection = await _context.Set<SqlOSSsoConnection>()
                .FirstOrDefaultAsync(x => x.Id == session.ConnectionId && x.OrganizationId == organization.Id, cancellationToken);
        }

        connection ??= await _context.Set<SqlOSSsoConnection>()
            .Where(x => x.OrganizationId == organization.Id)
            .OrderByDescending(x => x.IsEnabled)
            .ThenByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (connection != null)
        {
            if (session != null && session.ConnectionId != connection.Id)
            {
                session.ConnectionId = connection.Id;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return connection;
        }

        connection = new SqlOSSsoConnection
        {
            Id = _cryptoService.GenerateId("sso"),
            OrganizationId = organization.Id,
            DisplayName = $"{organization.Name} SSO",
            IdentityProviderEntityId = string.Empty,
            SingleSignOnUrl = string.Empty,
            X509CertificatePem = string.Empty,
            AutoProvisionUsers = true,
            AutoLinkByEmail = false,
            EmailAttributeName = "email",
            FirstNameAttributeName = "first_name",
            LastNameAttributeName = "last_name",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsEnabled = false
        };

        _context.Set<SqlOSSsoConnection>().Add(connection);
        if (session != null)
        {
            session.ConnectionId = connection.Id;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    private async Task<SqlOSSsoConnection> RequirePortalConnectionAsync(
        SqlOSSsoPortalSession session,
        CancellationToken cancellationToken)
    {
        var organization = session.Organization
            ?? await _context.Set<SqlOSOrganization>().FirstAsync(x => x.Id == session.OrganizationId, cancellationToken);
        return await EnsurePortalConnectionAsync(organization, cancellationToken, session);
    }

    private async Task<SqlOSSsoPortalSession?> LoadSessionByIdAsync(string sessionId, CancellationToken cancellationToken)
        => await _context.Set<SqlOSSsoPortalSession>()
            .Include(x => x.Organization)
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

    private SqlOSSsoPortalSessionResult ToSessionResult(SqlOSSsoPortalSession session, string? setupUrl)
    {
        var organization = session.Organization;
        return new SqlOSSsoPortalSessionResult(
            session.Id,
            session.OrganizationId,
            organization?.Name ?? string.Empty,
            organization?.PrimaryDomain,
            GetSessionStatus(session, DateTime.UtcNow),
            session.Provider,
            session.ConnectionId,
            setupUrl,
            BuildPortalUrl(),
            session.CreatedAt,
            session.ExpiresAt,
            session.OpenedAt,
            session.LastSeenAt,
            session.RevokedAt,
            session.RevokedReason);
    }

    private static SqlOSSsoPortalConnectionResult ToConnectionResult(SqlOSSsoConnection connection)
        => new(
            connection.Id,
            connection.DisplayName,
            connection.IsEnabled,
            SqlOSAdminService.GetSsoSetupStatus(connection),
            NullIfWhiteSpace(connection.IdentityProviderEntityId),
            NullIfWhiteSpace(connection.SingleSignOnUrl),
            connection.AutoProvisionUsers,
            connection.AutoLinkByEmail,
            connection.CreatedAt,
            connection.UpdatedAt);

    private async Task RecordPortalAuditAsync(
        string eventType,
        SqlOSSsoPortalSession session,
        HttpContext? httpContext,
        object? data,
        CancellationToken cancellationToken)
    {
        await _adminService.RecordAuditAsync(
            eventType,
            "sso_portal",
            session.Id,
            userId: session.CreatedByUserId,
            organizationId: session.OrganizationId,
            ipAddress: httpContext?.Connection.RemoteIpAddress?.ToString(),
            data: data,
            cancellationToken: cancellationToken);
    }

    private string BuildSetupUrl(string rawLinkToken, HttpContext? httpContext)
        => $"{BuildPortalUrl(httpContext)}/start?token={Uri.EscapeDataString(rawLinkToken)}";

    private string GetPortalPath() => $"{GetAdminPrefix()}/sso-portal";

    private string GetAdminPrefix()
    {
        var authPrefix = _options.BasePath.TrimEnd('/');
        return authPrefix.EndsWith("/auth", StringComparison.OrdinalIgnoreCase)
            ? $"{authPrefix[..^5]}/admin/auth"
            : $"{authPrefix}/admin";
    }

    private string GetOrigin(HttpContext? httpContext)
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicOrigin))
        {
            return _options.PublicOrigin.TrimEnd('/');
        }

        if (httpContext != null && httpContext.Request.Host.HasValue)
        {
            return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        }

        return Uri.TryCreate(_options.Issuer, UriKind.Absolute, out var issuer)
            ? issuer.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : string.Empty;
    }

    private void SetPortalCookie(HttpContext httpContext, string rawSessionToken, DateTime expiresAt)
        => httpContext.Response.Cookies.Append(GetCookieName(), rawSessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = GetPortalPath(),
            Expires = new DateTimeOffset(expiresAt, TimeSpan.Zero)
        });

    private void ClearPortalCookie(HttpContext httpContext)
        => httpContext.Response.Cookies.Delete(GetCookieName(), new CookieOptions { Path = GetPortalPath() });

    private string GetCookieName()
        => string.IsNullOrWhiteSpace(_portalOptions.CookieName)
            ? "sqlos_sso_portal"
            : _portalOptions.CookieName.Trim();

    private static void EnsureSessionCanOpen(SqlOSSsoPortalSession session, DateTime now)
    {
        if (session.RevokedAt != null || session.ExpiresAt <= now)
        {
            throw new InvalidOperationException("Portal setup token is invalid or expired.");
        }

        if (session.OpenedAt != null || !string.IsNullOrWhiteSpace(session.SessionTokenHash))
        {
            throw new InvalidOperationException("Portal setup token has already been used.");
        }
    }

    private bool IsSessionUsable(SqlOSSsoPortalSession session, DateTime now)
        => session.RevokedAt == null
           && session.ExpiresAt > now
           && !string.IsNullOrWhiteSpace(session.SessionTokenHash)
           && (session.LastSeenAt == null || session.LastSeenAt.Value.Add(_portalOptions.SessionIdleTimeout) > now);

    private static void EnsureConnectionHasMetadata(SqlOSSsoConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.IdentityProviderEntityId)
            || string.IsNullOrWhiteSpace(connection.SingleSignOnUrl)
            || string.IsNullOrWhiteSpace(connection.X509CertificatePem))
        {
            throw new InvalidOperationException("Import valid SAML metadata before activating the connection.");
        }
    }

    private static string GetSessionStatus(SqlOSSsoPortalSession session, DateTime now)
    {
        if (session.RevokedAt != null)
        {
            return "revoked";
        }

        if (session.ExpiresAt <= now)
        {
            return "expired";
        }

        return session.OpenedAt == null ? "pending" : "opened";
    }

    private static string? NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "microsoft" or "entra" or "azure-ad" or "microsoft-entra" => "microsoft-entra",
            "okta" => "okta",
            "google" or "google-workspace" => "google-workspace",
            "generic" or "saml" or "generic-saml" => "generic-saml",
            var value => throw new InvalidOperationException($"Unsupported SSO provider '{value}'.")
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
