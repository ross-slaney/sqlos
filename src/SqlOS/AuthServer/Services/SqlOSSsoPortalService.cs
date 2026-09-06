using System.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Database;
using SqlOS.Pagination;

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
    private readonly SqlOSOrganizationDomainService _domainService;

    public SqlOSSsoPortalService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService,
        SqlOSAdminService adminService,
        SqlOSOrganizationDomainService domainService)
    {
        _context = context;
        _options = options.Value;
        _portalOptions = _options.SsoPortal;
        _cryptoService = cryptoService;
        _adminService = adminService;
        _domainService = domainService;
    }

    public async Task<SqlOSSsoPortalSessionResult> CreateSessionAsync(
        SqlOSCreateSsoPortalSessionRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var expiresAt = request.ExpiresAt ?? now.Add(_portalOptions.DefaultLinkLifetime);
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("Portal session expiration must be in the future.");
        }

        var provider = NormalizeProvider(request.Provider);
        var rawLinkToken = _cryptoService.GenerateOpaqueToken();
        var linkTokenHash = _cryptoService.HashToken(rawLinkToken);
        var sessionId = _cryptoService.GenerateId("ssp");

        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            return await CreateSessionCoreAsync(
                request,
                sessionId,
                rawLinkToken,
                linkTokenHash,
                provider,
                now,
                expiresAt,
                httpContext,
                cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(
                SqlOSDatabase.ExclusiveWorkIsolationLevel(_context.Database),
                cancellationToken);
            var created = await CreateSessionCoreAsync(
                request,
                sessionId,
                rawLinkToken,
                linkTokenHash,
                provider,
                now,
                expiresAt,
                httpContext,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return created;
        });
    }

    public async Task<object> ListOrganizationSessionsAsync(
        string organizationId,
        string? cursor = null,
        int? pageSize = null,
        int? page = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        _ = await _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        SqlOSCursorPagination.RejectLegacyOffset(page);
        var size = SqlOSCursorPagination.NormalizePageSize(pageSize, 20);
        var pageResult = await SqlOSCursorPagination.ToPageAsync(
            _context.Set<SqlOSSsoPortalSession>().AsNoTracking().Include(x => x.Organization).Where(x => x.OrganizationId == organizationId),
            SqlOSKeyset<SqlOSSsoPortalSession>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "auth.sso-portal-sessions",
            SqlOSCursorCodec.Fingerprint(organizationId),
            cursor,
            size,
            cancellationToken);
        return pageResult.ToResponse(x => ToSessionResult(x, setupUrl: null));
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

        var tokenHash = _cryptoService.HashToken(rawLinkToken.Trim());
        var organizationId = await _context.Set<SqlOSSsoPortalSession>()
            .AsNoTracking()
            .Where(x => x.LinkTokenHash == tokenHash)
            .Select(x => x.OrganizationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            throw new InvalidOperationException("Portal setup token is invalid or expired.");
        }

        var rawSessionToken = _cryptoService.GenerateOpaqueToken();
        var sessionTokenHash = _cryptoService.HashToken(rawSessionToken);

        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            return await OpenSessionCoreAsync(
                tokenHash,
                organizationId,
                rawSessionToken,
                sessionTokenHash,
                httpContext,
                cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(
                SqlOSDatabase.ExclusiveWorkIsolationLevel(_context.Database),
                cancellationToken);
            var opened = await OpenSessionCoreAsync(
                tokenHash,
                organizationId,
                rawSessionToken,
                sessionTokenHash,
                httpContext,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return opened;
        });
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
            .FirstOrDefaultAsync(
                x => x.SessionTokenHash == tokenHash
                    && x.Organization != null
                    && x.Organization.IsActive
                    && x.RevokedAt == null
                    && x.ExpiresAt > now,
                cancellationToken);
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

    public Task<SqlOSSsoPortalStateResult> GetStateAsync(
        SqlOSSsoPortalSession session,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => GetStateCoreAsync(lockedSession, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> GetStateCoreAsync(
        SqlOSSsoPortalSession session,
        CancellationToken cancellationToken)
    {
        var organization = session.Organization!;
        var connection = await EnsurePortalConnectionAsync(organization, cancellationToken, session);
        var domain = await _domainService.GetPreferredDomainAsync(organization.Id, cancellationToken);

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
                    session.LastTestedAt.Value),
            domain,
            BuildAllowedActions(organization, connection, domain));
    }

    public Task<SqlOSSsoPortalStateResult> SetProviderAsync(
        SqlOSSsoPortalSession session,
        SqlOSUpdateSsoPortalProviderRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => SetProviderCoreAsync(lockedSession, request, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> SetProviderCoreAsync(
        SqlOSSsoPortalSession session,
        SqlOSUpdateSsoPortalProviderRequest request,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
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

    public Task<SqlOSSsoPortalStateResult> UpdateEnrollmentPolicyAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalEnrollmentPolicyRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => UpdateEnrollmentPolicyCoreAsync(lockedSession, request, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> UpdateEnrollmentPolicyCoreAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalEnrollmentPolicyRequest request,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var connection = await RequirePortalConnectionAsync(session, cancellationToken);
        connection.AutoLinkByEmail = request.RequireSsoForExistingMembers;
        connection.AutoProvisionUsers = request.AllowJitProvisioning;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        await RecordPortalAuditAsync(
            "sso.portal.enrollment_policy.updated",
            session,
            httpContext,
            new
            {
                connectionId = connection.Id,
                request.RequireSsoForExistingMembers,
                request.AllowJitProvisioning
            },
            cancellationToken);

        return await GetStateAsync(session, cancellationToken);
    }

    public SqlOSSsoMetadataValidationResult ValidateMetadata(SqlOSSsoPortalMetadataRequest request)
        => _adminService.ValidateSsoMetadata(new SqlOSImportSsoMetadataRequest(request.MetadataXml));

    public Task<SqlOSSsoPortalStateResult> StartDomainVerificationAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalDomainRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => StartDomainVerificationCoreAsync(lockedSession, request, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> StartDomainVerificationCoreAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalDomainRequest request,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        await _domainService.StartVerificationAsync(
            session.OrganizationId,
            request,
            httpContext,
            session.CreatedByUserId,
            cancellationToken);
        return await GetStateAsync(session, cancellationToken);
    }

    public async Task<SqlOSSsoPortalStateResult> ConfirmDomainOwnershipAsync(
        SqlOSSsoPortalSession session,
        string domainId,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        _ = await ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            _ => Task.FromResult(true),
            cancellationToken);

        var ownershipCheck = await _domainService.CheckOwnershipAsync(
            session.OrganizationId,
            domainId,
            cancellationToken);

        return await ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => ConfirmDomainOwnershipCoreAsync(
                lockedSession,
                ownershipCheck,
                httpContext,
                cancellationToken),
            cancellationToken);
    }

    private async Task<SqlOSSsoPortalStateResult> ConfirmDomainOwnershipCoreAsync(
        SqlOSSsoPortalSession session,
        SqlOSOrganizationDomainOwnershipCheck ownershipCheck,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        await _domainService.ApplyOwnershipCheckAsync(
            ownershipCheck,
            httpContext,
            cancellationToken);
        return await GetStateAsync(session, cancellationToken);
    }

    public Task<SqlOSSsoPortalStateResult> ImportMetadataAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalMetadataRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => ImportMetadataCoreAsync(lockedSession, request, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> ImportMetadataCoreAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalMetadataRequest request,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
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

    public Task<SqlOSSsoPortalStateResult> ActivateAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => ActivateCoreAsync(lockedSession, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> ActivateCoreAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var connection = await RequirePortalConnectionAsync(session, cancellationToken);
        EnsureConnectionHasMetadata(connection);
        await EnsureDomainCanActivateAsync(session, cancellationToken);
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

    public Task<SqlOSSsoPortalStateResult> DisableAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => DisableCoreAsync(lockedSession, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalStateResult> DisableCoreAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
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

    public Task<SqlOSSsoPortalRevokeOrganizationSessionsResult> RevokeOrganizationSessionsAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalRevokeOrganizationSessionsRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => RevokeOrganizationSessionsCoreAsync(lockedSession, request, httpContext, cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalRevokeOrganizationSessionsResult> RevokeOrganizationSessionsCoreAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalRevokeOrganizationSessionsRequest request,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (!request.Confirm)
        {
            throw new InvalidOperationException("Confirm session revocation before signing out existing sessions.");
        }

        var connection = await RequirePortalConnectionAsync(session, cancellationToken);
        if (!connection.IsEnabled)
        {
            throw new InvalidOperationException("Activate the SSO connection before signing out existing sessions.");
        }

        var domain = await _domainService.GetPreferredDomainAsync(session.OrganizationId, cancellationToken);
        if (domain == null || domain.Status != SqlOSOrganizationDomainStatuses.Active)
        {
            throw new InvalidOperationException("Verify an SSO domain before signing out existing sessions.");
        }

        var now = DateTime.UtcNow;
        var normalizedDomainSuffix = "@" + domain.Domain.ToUpperInvariant();
        var eligibleUserIds = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .Where(x => x.IsVerified && x.NormalizedEmail.EndsWith(normalizedDomainSuffix))
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Refresh-based organization switching keeps the session's original
        // organization as its default. Consumed refresh rows are therefore the
        // durable lineage proving that this session also issued tokens for the
        // SSO organization and must be included in its cutoff.
        var sessions = eligibleUserIds.Count == 0
            ? []
            : await _context.Set<SqlOSSession>()
                .Where(x => x.RevokedAt == null
                    && eligibleUserIds.Contains(x.UserId)
                    && (x.OrganizationId == session.OrganizationId
                        || _context.Set<SqlOSRefreshToken>().Any(refreshToken =>
                            refreshToken.SessionId == x.Id
                            && refreshToken.ReplacementOrganizationId == session.OrganizationId)))
                .ToListAsync(cancellationToken);

        var sessionIds = sessions.Select(x => x.Id).ToList();
        var refreshTokens = sessionIds.Count == 0
            ? []
            : await _context.Set<SqlOSRefreshToken>()
                .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
        var authPageSessions = eligibleUserIds.Count == 0
            ? []
            : await _context.Set<SqlOSTemporaryToken>()
                .Where(x => x.Purpose == SqlOSAuthLifecyclePolicy.AuthPageSessionPurpose
                    && x.OrganizationId == session.OrganizationId
                    && x.ConsumedAt == null
                    && eligibleUserIds.Contains(x.UserId!))
                .ToListAsync(cancellationToken);

        foreach (var activeSession in sessions)
        {
            activeSession.RevokedAt = now;
            activeSession.RevocationReason = "sso_required";
        }

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }

        foreach (var authPageSession in authPageSessions)
        {
            authPageSession.ConsumedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await RecordPortalAuditAsync(
            "sso.portal.organization_sessions.revoked",
            session,
            httpContext,
            new
            {
                connectionId = connection.Id,
                domain = domain.Domain,
                revokedSessions = sessions.Count,
                invalidatedAuthPageSessions = authPageSessions.Count
            },
            cancellationToken);

        return new SqlOSSsoPortalRevokeOrganizationSessionsResult(
            session.OrganizationId,
            connection.Id,
            domain.Domain,
            sessions.Count,
            now);
    }

    public Task<SqlOSSsoPortalTestResult> RecordTestAsync(
        SqlOSSsoPortalSession session,
        string status,
        string message,
        string? authorizationUrl,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => ExecutePortalOperationAsync(
            session.Id,
            session.OrganizationId,
            lockedSession => RecordTestCoreAsync(
                lockedSession,
                status,
                message,
                authorizationUrl,
                httpContext,
                cancellationToken),
            cancellationToken);

    private async Task<SqlOSSsoPortalTestResult> RecordTestCoreAsync(
        SqlOSSsoPortalSession session,
        string status,
        string message,
        string? authorizationUrl,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
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

    public bool IsApiEnabled => _portalOptions.EnableApi;

    public bool IsHostedPortalEnabled => _portalOptions.UseHostedPortal;

    public string GetSetupApiBasePath()
        => _portalOptions.ResolveHeadlessApiBasePath(GetAdminPrefix());

    public string? TryBuildSetupUiUrl(
        HttpContext httpContext,
        string sessionId,
        string organizationId,
        string view)
    {
        if (_portalOptions.BuildUiUrl == null)
        {
            return null;
        }

        return _portalOptions.BuildUiUrl(
            new SqlOSSsoSetupUiRouteContext(
                httpContext,
                sessionId,
                organizationId,
                NormalizeView(view)));
    }

    public async Task<SqlOSSsoSetupViewModel> GetSetupViewAsync(
        SqlOSSsoPortalSession session,
        string? view = null,
        string? error = null,
        IReadOnlyDictionary<string, string>? fieldErrors = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(session, cancellationToken);
        return new SqlOSSsoSetupViewModel(
            NormalizeView(view),
            GetSetupApiBasePath(),
            BuildPortalUrl(),
            state.Organization,
            state.Connection,
            state.Domain,
            state.Provider,
            new SqlOSSsoSetupServiceProvider(
                state.ServiceProviderEntityId,
                state.AssertionConsumerServiceUrl),
            state.Providers,
            state.LatestTest,
            state.AllowedActions ?? BuildAllowedActions(state.Organization, state.Connection, state.Domain),
            error,
            fieldErrors ?? new Dictionary<string, string>());
    }

    public async Task<SqlOSSsoSetupActionResult> GetSetupActionAsync(
        SqlOSSsoPortalSession session,
        string? view = null,
        CancellationToken cancellationToken = default)
        => await ViewActionAsync(session, view, null, null, cancellationToken);

    public async Task<SqlOSSsoSetupActionResult> SetProviderActionAsync(
        SqlOSSsoPortalSession session,
        SqlOSUpdateSsoPortalProviderRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await SetProviderAsync(session, request, httpContext, cancellationToken);
            return await ViewActionAsync(session, "domain", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(
                session,
                "provider",
                ex.Message,
                new Dictionary<string, string> { ["provider"] = ex.Message },
                cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> UpdateEnrollmentPolicyActionAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalEnrollmentPolicyRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await UpdateEnrollmentPolicyAsync(session, request, httpContext, cancellationToken);
            return await ViewActionAsync(session, "policy", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(session, "policy", ex.Message, null, cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> StartDomainVerificationActionAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalDomainRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await StartDomainVerificationAsync(session, request, httpContext, cancellationToken);
            return await ViewActionAsync(session, "domain", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(
                session,
                "domain",
                ex.Message,
                new Dictionary<string, string> { ["domain"] = ex.Message },
                cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> ConfirmDomainOwnershipActionAsync(
        SqlOSSsoPortalSession session,
        string domainId,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await ConfirmDomainOwnershipAsync(session, domainId, httpContext, cancellationToken);
            var domain = state.Domain;
            return await ViewActionAsync(
                session,
                domain?.Status == SqlOSOrganizationDomainStatuses.Active ? "metadata" : "domain",
                domain?.Status == SqlOSOrganizationDomainStatuses.Active ? null : domain?.LastError,
                null,
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(session, "domain", ex.Message, null, cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> ImportMetadataActionAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalMetadataRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ImportMetadataAsync(session, request, httpContext, cancellationToken);
            return await ViewActionAsync(session, "activate", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(
                session,
                "metadata",
                ex.Message,
                new Dictionary<string, string> { ["metadataXml"] = ex.Message },
                cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> ActivateActionAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ActivateAsync(session, httpContext, cancellationToken);
            return await ViewActionAsync(session, "test", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(session, "activate", ex.Message, null, cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> DisableActionAsync(
        SqlOSSsoPortalSession session,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DisableAsync(session, httpContext, cancellationToken);
            return await ViewActionAsync(session, "activate", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(session, "activate", ex.Message, null, cancellationToken);
        }
    }

    public async Task<SqlOSSsoSetupActionResult> RecordTestActionAsync(
        SqlOSSsoPortalSession session,
        SqlOSSsoPortalTestRequest request,
        SqlOSSamlService samlService,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await GetStateAsync(session, cancellationToken);
            if (!state.Connection.IsEnabled)
            {
                await RecordTestAsync(
                    session,
                    "blocked",
                    "Activate the SSO connection before starting a test sign-in.",
                    null,
                    httpContext,
                    cancellationToken);
                return await ViewActionAsync(session, "test", null, null, cancellationToken);
            }

            string? authorizationUrl = null;
            if (!string.IsNullOrWhiteSpace(request.ClientId) && !string.IsNullOrWhiteSpace(request.RedirectUri))
            {
                authorizationUrl = await samlService.CreateAuthorizationUrlAsync(
                    new SqlOSAuthorizationUrlRequest(
                        state.Connection.Id,
                        request.ClientId,
                        request.RedirectUri,
                        request.State ?? string.Empty,
                        request.CodeChallenge ?? string.Empty,
                        request.CodeChallengeMethod ?? string.Empty),
                    cancellationToken);
            }

            await RecordTestAsync(
                session,
                authorizationUrl == null ? "ready" : "started",
                authorizationUrl == null
                    ? "Connection is active and ready for a SAML sign-in test."
                    : "SAML sign-in test redirect created.",
                authorizationUrl,
                httpContext,
                cancellationToken);
            return await ViewActionAsync(session, "test", null, null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not SqlOSSsoPortalSessionUnavailableException)
        {
            return await ViewActionAsync(session, "test", ex.Message, null, cancellationToken);
        }
    }

    private async Task<SqlOSSsoSetupActionResult> ViewActionAsync(
        SqlOSSsoPortalSession session,
        string? view,
        string? error,
        IReadOnlyDictionary<string, string>? fieldErrors,
        CancellationToken cancellationToken)
        => new("view", null, await GetSetupViewAsync(session, view, error, fieldErrors, cancellationToken));

    private async Task<SqlOSSsoPortalSessionResult> OpenSessionCoreAsync(
        string linkTokenHash,
        string organizationId,
        string rawSessionToken,
        string sessionTokenHash,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        await SqlOSSsoPortalOrganizationLock.AcquireAsync(_context, organizationId, cancellationToken);

        var now = DateTime.UtcNow;
        var session = await _context.Set<SqlOSSsoPortalSession>()
            .Include(x => x.Organization)
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(
                x => x.LinkTokenHash == linkTokenHash
                    && x.OrganizationId == organizationId
                    && x.Organization != null
                    && x.Organization.IsActive
                    && x.RevokedAt == null
                    && x.ExpiresAt > now,
                cancellationToken)
            ?? throw new InvalidOperationException("Portal setup token is invalid or expired.");

        if (session.OpenedAt != null
            && string.Equals(session.SessionTokenHash, sessionTokenHash, StringComparison.Ordinal))
        {
            SetPortalCookie(httpContext, rawSessionToken, session.ExpiresAt);
            return ToSessionResult(session, setupUrl: null);
        }

        EnsureSessionCanOpen(session, now);

        session.SessionTokenHash = sessionTokenHash;
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

    private async Task<SqlOSSsoPortalSessionResult> CreateSessionCoreAsync(
        SqlOSCreateSsoPortalSessionRequest request,
        string sessionId,
        string rawLinkToken,
        string linkTokenHash,
        string? provider,
        DateTime now,
        DateTime expiresAt,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        await SqlOSSsoPortalOrganizationLock.AcquireAsync(
            _context,
            request.OrganizationId,
            cancellationToken);

        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(
                x => x.Id == request.OrganizationId && x.IsActive,
                cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var existing = await _context.Set<SqlOSSsoPortalSession>()
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (existing != null)
        {
            if (!string.Equals(existing.LinkTokenHash, linkTokenHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Portal session identifier collision.");
            }

            existing.Organization = organization;
            return ToSessionResult(existing, BuildSetupUrl(rawLinkToken, httpContext));
        }

        var connection = await EnsurePortalConnectionAsync(organization, cancellationToken);
        var session = new SqlOSSsoPortalSession
        {
            Id = sessionId,
            OrganizationId = organization.Id,
            ConnectionId = connection.Id,
            LinkTokenHash = linkTokenHash,
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

    private async Task<TResult> ExecutePortalOperationAsync<TResult>(
        string sessionId,
        string organizationId,
        Func<SqlOSSsoPortalSession, Task<TResult>> mutation,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            return await ExecutePortalOperationCoreAsync(
                sessionId,
                organizationId,
                mutation,
                cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(
                SqlOSDatabase.ExclusiveWorkIsolationLevel(_context.Database),
                cancellationToken);
            var result = await ExecutePortalOperationCoreAsync(
                sessionId,
                organizationId,
                mutation,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private async Task<TResult> ExecutePortalOperationCoreAsync<TResult>(
        string sessionId,
        string organizationId,
        Func<SqlOSSsoPortalSession, Task<TResult>> mutation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            throw new SqlOSSsoPortalSessionUnavailableException();
        }

        await SqlOSSsoPortalOrganizationLock.AcquireAsync(_context, organizationId, cancellationToken);
        var session = await RequireAvailableSessionAsync(
            sessionId,
            organizationId,
            cancellationToken);
        return await mutation(session);
    }

    private async Task<SqlOSSsoPortalSession> RequireAvailableSessionAsync(
        string sessionId,
        string organizationId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var session = await _context.Set<SqlOSSsoPortalSession>()
            .Include(x => x.Organization)
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(
                x => x.Id == sessionId
                    && x.OrganizationId == organizationId
                    && x.Organization != null
                    && x.Organization.IsActive
                    && x.RevokedAt == null
                    && x.ExpiresAt > now,
                cancellationToken);
        if (session == null || !IsSessionAvailableForOperation(session, now))
        {
            throw new SqlOSSsoPortalSessionUnavailableException();
        }

        return session;
    }

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
            AutoProvisionUsers = false,
            AutoLinkByEmail = true,
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
            connection.UpdatedAt,
            new SqlOSSsoPortalEnrollmentPolicyResult(
                connection.AutoLinkByEmail,
                connection.AutoProvisionUsers));

    private SqlOSSsoSetupAllowedActions BuildAllowedActions(
        SqlOSOrganization organization,
        SqlOSSsoConnection connection,
        SqlOSOrganizationDomainResult? domain)
        => BuildAllowedActions(
            new SqlOSSsoPortalOrganizationResult(
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.PrimaryDomain),
            ToConnectionResult(connection),
            domain);

    private SqlOSSsoSetupAllowedActions BuildAllowedActions(
        SqlOSSsoPortalOrganizationResult organization,
        SqlOSSsoPortalConnectionResult connection,
        SqlOSOrganizationDomainResult? domain)
    {
        var hasMetadata = connection.SetupStatus is "ready_to_activate" or "active";
        var domainReady = IsDomainReadyForActivation(organization.PrimaryDomain, domain);
        return new SqlOSSsoSetupAllowedActions(
            CanSelectProvider: true,
            CanStartDomainVerification: true,
            CanConfirmDomainVerification: domain?.Status == SqlOSOrganizationDomainStatuses.PendingOwnership,
            CanValidateMetadata: true,
            CanImportMetadata: true,
            CanActivate: !connection.IsEnabled && hasMetadata && domainReady,
            CanDisable: connection.IsEnabled,
            CanTest: connection.IsEnabled,
            CanSignOut: true,
            CanUpdateEnrollmentPolicy: true,
            CanRevokeOrganizationSessions: connection.IsEnabled && domain?.Status == SqlOSOrganizationDomainStatuses.Active);
    }

    private bool IsDomainReadyForActivation(string? primaryDomain, SqlOSOrganizationDomainResult? domain)
    {
        if (!_portalOptions.RequireVerifiedDomainForActivation)
        {
            return true;
        }

        if (domain?.Status == SqlOSOrganizationDomainStatuses.Active)
        {
            return true;
        }

        return domain == null && !string.IsNullOrWhiteSpace(primaryDomain);
    }

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
        => SqlOSPublicOriginResolver.Resolve(_options);

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
        if (session.Organization?.IsActive != true
            || session.RevokedAt != null
            || session.ExpiresAt <= now)
        {
            throw new InvalidOperationException("Portal setup token is invalid or expired.");
        }

        if (session.OpenedAt != null || !string.IsNullOrWhiteSpace(session.SessionTokenHash))
        {
            throw new InvalidOperationException("Portal setup token has already been used.");
        }
    }

    private bool IsSessionUsable(SqlOSSsoPortalSession session, DateTime now)
        => session.Organization?.IsActive == true
           && session.RevokedAt == null
           && session.ExpiresAt > now
           && !string.IsNullOrWhiteSpace(session.SessionTokenHash)
           && (session.LastSeenAt == null || session.LastSeenAt.Value.Add(_portalOptions.SessionIdleTimeout) > now);

    private bool IsSessionAvailableForOperation(SqlOSSsoPortalSession session, DateTime now)
        => session.Organization?.IsActive == true
           && session.RevokedAt == null
           && session.ExpiresAt > now
           && (string.IsNullOrWhiteSpace(session.SessionTokenHash)
               || session.LastSeenAt == null
               || session.LastSeenAt.Value.Add(_portalOptions.SessionIdleTimeout) > now);

    private static void EnsureConnectionHasMetadata(SqlOSSsoConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.IdentityProviderEntityId)
            || string.IsNullOrWhiteSpace(connection.SingleSignOnUrl)
            || string.IsNullOrWhiteSpace(connection.X509CertificatePem))
        {
            throw new InvalidOperationException("Import valid SAML metadata before activating the connection.");
        }
    }

    private async Task EnsureDomainCanActivateAsync(
        SqlOSSsoPortalSession session,
        CancellationToken cancellationToken)
    {
        if (!_portalOptions.RequireVerifiedDomainForActivation)
        {
            return;
        }

        var organization = session.Organization
            ?? await _context.Set<SqlOSOrganization>().AsNoTracking().FirstAsync(x => x.Id == session.OrganizationId, cancellationToken);
        var domain = await _domainService.GetPreferredDomainAsync(session.OrganizationId, cancellationToken);
        if (IsDomainReadyForActivation(organization.PrimaryDomain, domain))
        {
            return;
        }

        throw new InvalidOperationException("Verify domain ownership before activating SSO for home realm discovery.");
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

    private static string NormalizeView(string? view)
        => string.IsNullOrWhiteSpace(view)
            ? "provider"
            : view.Trim().ToLowerInvariant() switch
            {
                "provider" => "provider",
                "domain" or "domains" => "domain",
                "metadata" => "metadata",
                "activate" or "activation" => "activate",
                "test" => "test",
                _ => "provider"
            };
}
