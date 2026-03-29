using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAdminService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSClientResolutionService _clientResolutionService;

    public SqlOSAdminService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService)
        : this(context, options, cryptoService, new SqlOSClientResolutionService(context, options))
    {
    }

    public SqlOSAdminService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService,
        SqlOSClientResolutionService clientResolutionService)
    {
        _context = context;
        _options = options.Value;
        _cryptoService = cryptoService;
        _clientResolutionService = clientResolutionService;
    }

    public async Task CleanupExpiredTemporaryTokensAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.ExpiresAt < DateTime.UtcNow || x.ConsumedAt != null)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        _context.Set<SqlOSTemporaryToken>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanupExpiredRefreshTokensAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _context.Set<SqlOSRefreshToken>()
            .Where(x => x.ExpiresAt < DateTime.UtcNow || x.RevokedAt != null || x.ConsumedAt != null)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        _context.Set<SqlOSRefreshToken>().RemoveRange(expired);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertSeededClientsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ClientSeeds.Count == 0)
        {
            return;
        }

        foreach (var seed in _options.ClientSeeds)
        {
            var normalized = NormalizeSeededClient(seed);
            var existing = await _context.Set<SqlOSClientApplication>()
                .FirstOrDefaultAsync(x => x.ClientId == normalized.ClientId, cancellationToken);

            if (existing == null)
            {
                _context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
                {
                    Id = _cryptoService.GenerateId("cli"),
                    ClientId = normalized.ClientId,
                    Name = normalized.Name,
                    Description = normalized.Description,
                    Audience = normalized.Audience,
                    ClientType = normalized.ClientType,
                    RegistrationSource = "seeded",
                    TokenEndpointAuthMethod = "none",
                    GrantTypesJson = JsonSerializer.Serialize(new[] { "authorization_code", "refresh_token" }),
                    ResponseTypesJson = JsonSerializer.Serialize(new[] { "code" }),
                    RequirePkce = normalized.RequirePkce,
                    AllowedScopesJson = JsonSerializer.Serialize(normalized.AllowedScopes),
                    RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris),
                    CreatedAt = DateTime.UtcNow,
                    IsFirstParty = normalized.IsFirstParty,
                    IsActive = normalized.IsActive
                });
                continue;
            }

            existing.Name = normalized.Name;
            existing.Description = normalized.Description;
            existing.Audience = normalized.Audience;
            existing.ClientType = normalized.ClientType;
            existing.RegistrationSource = "seeded";
            existing.TokenEndpointAuthMethod = string.IsNullOrWhiteSpace(existing.TokenEndpointAuthMethod) ? "none" : existing.TokenEndpointAuthMethod;
            existing.GrantTypesJson = string.IsNullOrWhiteSpace(existing.GrantTypesJson)
                ? JsonSerializer.Serialize(new[] { "authorization_code", "refresh_token" })
                : existing.GrantTypesJson;
            existing.ResponseTypesJson = string.IsNullOrWhiteSpace(existing.ResponseTypesJson)
                ? JsonSerializer.Serialize(new[] { "code" })
                : existing.ResponseTypesJson;
            existing.RequirePkce = normalized.RequirePkce;
            existing.AllowedScopesJson = JsonSerializer.Serialize(normalized.AllowedScopes);
            existing.RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris);
            existing.IsFirstParty = normalized.IsFirstParty;
            if (existing.DisabledAt != null)
            {
                existing.IsActive = false;
            }
            else
            {
                existing.IsActive = normalized.IsActive;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SqlOSUser> CreateUserAsync(SqlOSCreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(request.Email);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (existingEmail != null)
        {
            throw new InvalidOperationException($"Email '{request.Email}' already exists.");
        }

        var user = new SqlOSUser
        {
            Id = _cryptoService.GenerateId("usr"),
            DisplayName = request.DisplayName,
            DefaultEmail = request.Email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var email = new SqlOSUserEmail
        {
            Id = _cryptoService.GenerateId("eml"),
            UserId = user.Id,
            Email = request.Email,
            NormalizedEmail = normalizedEmail,
            IsPrimary = true,
            IsVerified = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<SqlOSUser>().Add(user);
        _context.Set<SqlOSUserEmail>().Add(email);

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            _context.Set<SqlOSCredential>().Add(new SqlOSCredential
            {
                Id = _cryptoService.GenerateId("cred"),
                UserId = user.Id,
                SecretHash = _cryptoService.HashPassword(request.Password),
                Type = "password",
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<SqlOSOrganization> CreateOrganizationAsync(SqlOSCreateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugify(request.Name) : Slugify(request.Slug);
        var exists = await _context.Set<SqlOSOrganization>().AnyAsync(x => x.Slug == slug, cancellationToken);
        if (exists)
        {
            slug = $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 120)];
        }

        var organization = new SqlOSOrganization
        {
            Id = _cryptoService.GenerateId("org"),
            Name = request.Name,
            Slug = slug,
            PrimaryDomain = NormalizeDomain(request.PrimaryDomain),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSOrganization>().Add(organization);
        await _context.SaveChangesAsync(cancellationToken);
        return organization;
    }

    public async Task<SqlOSOrganization> UpdateOrganizationAsync(string organizationId, SqlOSUpdateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugify(request.Name) : Slugify(request.Slug);
        var slugExists = await _context.Set<SqlOSOrganization>()
            .AnyAsync(x => x.Id != organizationId && x.Slug == slug, cancellationToken);
        if (slugExists)
        {
            slug = $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 120)];
        }

        organization.Name = request.Name.Trim();
        organization.Slug = slug;
        organization.PrimaryDomain = NormalizeDomain(request.PrimaryDomain);
        organization.IsActive = request.IsActive;

        await _context.SaveChangesAsync(cancellationToken);
        return organization;
    }

    public async Task<SqlOSMembership> CreateMembershipAsync(string organizationId, SqlOSCreateMembershipRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<SqlOSMembership>().FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.UserId == request.UserId, cancellationToken);
        if (existing != null)
        {
            existing.IsActive = true;
            existing.Role = request.Role;
            await _context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var membership = new SqlOSMembership
        {
            OrganizationId = organizationId,
            UserId = request.UserId,
            Role = request.Role,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSMembership>().Add(membership);
        await _context.SaveChangesAsync(cancellationToken);
        return membership;
    }

    public async Task<SqlOSClientApplication> CreateClientAsync(SqlOSCreateClientRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeClientRequest(request);

        if (await _context.Set<SqlOSClientApplication>().AnyAsync(x => x.ClientId == normalized.ClientId, cancellationToken))
        {
            throw new InvalidOperationException($"Client '{normalized.ClientId}' already exists.");
        }

        var client = new SqlOSClientApplication
        {
            Id = _cryptoService.GenerateId("cli"),
            ClientId = normalized.ClientId,
            Name = normalized.Name,
            Description = normalized.Description,
            Audience = normalized.Audience,
            ClientType = normalized.ClientType,
            RegistrationSource = "manual",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = JsonSerializer.Serialize(new[] { "authorization_code", "refresh_token" }),
            ResponseTypesJson = JsonSerializer.Serialize(new[] { "code" }),
            RequirePkce = normalized.RequirePkce,
            AllowedScopesJson = JsonSerializer.Serialize(normalized.AllowedScopes),
            IsFirstParty = normalized.IsFirstParty,
            RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSClientApplication>().Add(client);
        await _context.SaveChangesAsync(cancellationToken);
        return client;
    }

    public async Task<SqlOSOidcConnection> CreateOidcConnectionAsync(SqlOSCreateOidcConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProviderType != SqlOSOidcProviderType.Custom &&
            await _context.Set<SqlOSOidcConnection>().AnyAsync(x => x.ProviderType == request.ProviderType, cancellationToken))
        {
            throw new InvalidOperationException($"An OIDC connection for provider '{request.ProviderType}' already exists.");
        }

        var connectionId = _cryptoService.GenerateId("oidc");
        var callbacks = NormalizeCallbackUris(request.AllowedCallbackUris, connectionId);
        if (callbacks.Count == 0)
        {
            throw new InvalidOperationException("At least one callback URI is required.");
        }

        var normalized = NormalizeOidcConfiguration(
            request.ProviderType,
            request.UseDiscovery,
            request.DiscoveryUrl,
            request.Issuer,
            request.AuthorizationEndpoint,
            request.TokenEndpoint,
            request.UserInfoEndpoint,
            request.JwksUri,
            request.MicrosoftTenant,
            request.Scopes,
            request.ClaimMapping,
            request.ClientAuthMethod,
            request.UseUserInfo,
            request.AppleTeamId,
            request.AppleKeyId);

        var connection = new SqlOSOidcConnection
        {
            Id = connectionId,
            ProviderType = request.ProviderType,
            DisplayName = request.DisplayName,
            LogoDataUrl = SqlOSOidcProviderLogoCatalog.NormalizeCustomLogoDataUrl(request.LogoDataUrl),
            ClientId = request.ClientId.Trim(),
            ClientSecretEncrypted = string.IsNullOrWhiteSpace(request.ClientSecret) ? null : _cryptoService.ProtectSecret(request.ClientSecret.Trim()),
            AllowedCallbackUrisJson = JsonSerializer.Serialize(callbacks),
            UseDiscovery = normalized.UseDiscovery,
            DiscoveryUrl = normalized.DiscoveryUrl,
            Issuer = normalized.Issuer,
            AuthorizationEndpoint = normalized.AuthorizationEndpoint,
            TokenEndpoint = normalized.TokenEndpoint,
            UserInfoEndpoint = normalized.UserInfoEndpoint,
            JwksUri = normalized.JwksUri,
            MicrosoftTenant = normalized.MicrosoftTenant,
            ScopesJson = JsonSerializer.Serialize(normalized.Scopes),
            ClaimMappingJson = JsonSerializer.Serialize(normalized.ClaimMapping),
            ClientAuthMethod = normalized.ClientAuthMethod,
            UseUserInfo = normalized.UseUserInfo,
            AppleTeamId = normalized.AppleTeamId,
            AppleKeyId = normalized.AppleKeyId,
            ApplePrivateKeyEncrypted = string.IsNullOrWhiteSpace(request.ApplePrivateKeyPem) ? null : _cryptoService.ProtectSecret(NormalizePrivateKey(request.ApplePrivateKeyPem)),
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        ValidateOidcSecretRequirements(connection);

        _context.Set<SqlOSOidcConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSOidcConnection> UpdateOidcConnectionAsync(string connectionId, SqlOSUpdateOidcConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSOidcConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("OIDC connection not found.");

        var callbacks = NormalizeCallbackUris(request.AllowedCallbackUris, connectionId);
        if (callbacks.Count == 0)
        {
            throw new InvalidOperationException("At least one callback URI is required.");
        }

        var normalized = NormalizeOidcConfiguration(
            connection.ProviderType,
            request.UseDiscovery,
            request.DiscoveryUrl,
            request.Issuer,
            request.AuthorizationEndpoint,
            request.TokenEndpoint,
            request.UserInfoEndpoint,
            request.JwksUri,
            request.MicrosoftTenant,
            request.Scopes,
            request.ClaimMapping,
            request.ClientAuthMethod,
            request.UseUserInfo,
            request.AppleTeamId,
            request.AppleKeyId);

        connection.DisplayName = request.DisplayName;
        connection.LogoDataUrl = SqlOSOidcProviderLogoCatalog.NormalizeCustomLogoDataUrl(request.LogoDataUrl);
        connection.ClientId = request.ClientId.Trim();
        connection.AllowedCallbackUrisJson = JsonSerializer.Serialize(callbacks);
        connection.UseDiscovery = normalized.UseDiscovery;
        connection.DiscoveryUrl = normalized.DiscoveryUrl;
        connection.Issuer = normalized.Issuer;
        connection.AuthorizationEndpoint = normalized.AuthorizationEndpoint;
        connection.TokenEndpoint = normalized.TokenEndpoint;
        connection.UserInfoEndpoint = normalized.UserInfoEndpoint;
        connection.JwksUri = normalized.JwksUri;
        connection.MicrosoftTenant = normalized.MicrosoftTenant;
        connection.ScopesJson = JsonSerializer.Serialize(normalized.Scopes);
        connection.ClaimMappingJson = JsonSerializer.Serialize(normalized.ClaimMapping);
        connection.ClientAuthMethod = normalized.ClientAuthMethod;
        connection.UseUserInfo = normalized.UseUserInfo;
        connection.AppleTeamId = normalized.AppleTeamId;
        connection.AppleKeyId = normalized.AppleKeyId;
        connection.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            connection.ClientSecretEncrypted = _cryptoService.ProtectSecret(request.ClientSecret.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.ApplePrivateKeyPem))
        {
            connection.ApplePrivateKeyEncrypted = _cryptoService.ProtectSecret(NormalizePrivateKey(request.ApplePrivateKeyPem));
        }

        ValidateOidcSecretRequirements(connection);

        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSOidcConnection> SetOidcConnectionEnabledAsync(string connectionId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSOidcConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("OIDC connection not found.");

        connection.IsEnabled = isEnabled;
        connection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSSsoConnection> CreateSsoConnectionAsync(SqlOSCreateSsoConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var connection = new SqlOSSsoConnection
        {
            Id = _cryptoService.GenerateId("sso"),
            OrganizationId = request.OrganizationId,
            DisplayName = request.DisplayName,
            IdentityProviderEntityId = request.IdentityProviderEntityId,
            SingleSignOnUrl = request.SingleSignOnUrl,
            X509CertificatePem = request.X509CertificatePem,
            AutoProvisionUsers = request.AutoProvisionUsers,
            AutoLinkByEmail = request.AutoLinkByEmail,
            EmailAttributeName = request.EmailAttributeName ?? "email",
            FirstNameAttributeName = request.FirstNameAttributeName ?? "first_name",
            LastNameAttributeName = request.LastNameAttributeName ?? "last_name",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsEnabled = true
        };

        _context.Set<SqlOSSsoConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSSsoConnection> CreateSsoConnectionDraftAsync(SqlOSCreateSsoConnectionDraftRequest request, CancellationToken cancellationToken = default)
    {
        var organization = await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");

        var normalizedPrimaryDomain = NormalizeDomain(request.PrimaryDomain);
        if (!string.IsNullOrWhiteSpace(normalizedPrimaryDomain))
        {
            organization.PrimaryDomain = normalizedPrimaryDomain;
        }

        var connection = new SqlOSSsoConnection
        {
            Id = _cryptoService.GenerateId("sso"),
            OrganizationId = request.OrganizationId,
            DisplayName = request.DisplayName,
            IdentityProviderEntityId = string.Empty,
            SingleSignOnUrl = string.Empty,
            X509CertificatePem = string.Empty,
            AutoProvisionUsers = request.AutoProvisionUsers,
            AutoLinkByEmail = request.AutoLinkByEmail,
            EmailAttributeName = "email",
            FirstNameAttributeName = "first_name",
            LastNameAttributeName = "last_name",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsEnabled = false
        };

        _context.Set<SqlOSSsoConnection>().Add(connection);
        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSSsoConnection> ImportSsoMetadataAsync(
        string connectionId,
        SqlOSImportSsoMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found.");

        var metadata = ParseFederationMetadata(request.MetadataXml);
        connection.IdentityProviderEntityId = metadata.IdentityProviderEntityId;
        connection.SingleSignOnUrl = metadata.SingleSignOnUrl;
        connection.X509CertificatePem = metadata.X509CertificatePem;
        connection.IsEnabled = true;
        connection.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async Task<SqlOSClientApplication> RequireClientAsync(
        string? clientId,
        string? redirectUri,
        CancellationToken cancellationToken = default,
        HttpContext? httpContext = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Client application is required.");
        }

        var resolved = await _clientResolutionService.ResolveRequiredClientAsync(clientId, redirectUri, httpContext, cancellationToken);
        return resolved.Client;
    }

    public async Task<List<SqlOSOrganizationOption>> GetUserOrganizationsAsync(string userId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSMembership>()
            .Where(x => x.UserId == userId && x.IsActive)
            .Include(x => x.Organization)
            .Select(x => new SqlOSOrganizationOption(
                x.OrganizationId,
                x.Organization!.Slug,
                x.Organization.Name,
                x.Role))
            .ToListAsync(cancellationToken);

    public async Task<bool> UserHasMembershipAsync(string userId, string organizationId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSMembership>()
            .AnyAsync(x => x.UserId == userId && x.OrganizationId == organizationId && x.IsActive, cancellationToken);

    public async Task<object> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        var users = await _context.Set<SqlOSUser>().CountAsync(cancellationToken);
        var orgs = await _context.Set<SqlOSOrganization>().CountAsync(cancellationToken);
        var sessions = await _context.Set<SqlOSSession>().CountAsync(cancellationToken);
        var connections = await _context.Set<SqlOSSsoConnection>().CountAsync(cancellationToken);
        var oidcConnections = await _context.Set<SqlOSOidcConnection>().CountAsync(cancellationToken);
        var clients = await _context.Set<SqlOSClientApplication>().CountAsync(cancellationToken);
        var eventsCount = await _context.Set<SqlOSAuditEvent>().CountAsync(cancellationToken);
        return new { users, organizations = orgs, sessions, ssoConnections = connections, oidcConnections, clients, auditEvents = eventsCount };
    }

    public async Task<object> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSUser>()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.DefaultEmail,
                x.IsActive,
                x.CreatedAt,
                x.UpdatedAt,
                MembershipCount = x.Memberships.Count(m => m.IsActive),
                SessionCount = x.Sessions.Count(s => s.RevokedAt == null),
                ExternalIdentityCount = x.ExternalIdentities.Count
            })
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException("User not found.");

    public async Task<object> ListUsersAsync(int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSUser>()
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.DefaultEmail,
                x.IsActive,
                x.CreatedAt,
                MembershipCount = x.Memberships.Count(m => m.IsActive)
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<object> GetOrganizationAsync(string organizationId, CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSOrganization>()
            .Where(x => x.Id == organizationId)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Slug,
                x.PrimaryDomain,
                x.IsActive,
                x.CreatedAt,
                MembershipCount = x.Memberships.Count(m => m.IsActive),
                SsoConnectionCount = x.SsoConnections.Count,
                EnabledSsoConnections = x.SsoConnections.Count(c => c.IsEnabled)
            })
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException("Organization not found.");

    public async Task<object> ListOrganizationsAsync(int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Slug,
                x.PrimaryDomain,
                x.IsActive,
                MembershipCount = x.Memberships.Count(m => m.IsActive),
                EnabledSsoConnections = x.SsoConnections.Count(c => c.IsEnabled)
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<object> ListMembershipsAsync(int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.User)
            .OrderBy(x => x.Organization!.Name)
            .ThenBy(x => x.User!.DisplayName)
            .Select(x => new
            {
                x.OrganizationId,
                Organization = x.Organization!.Name,
                x.UserId,
                User = x.User!.DisplayName,
                UserEmail = x.User!.DefaultEmail,
                x.Role,
                x.IsActive,
                x.CreatedAt
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<object> ListOrganizationMembershipsAsync(string organizationId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.User)
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.User!.DisplayName)
            .Select(x => new
            {
                x.OrganizationId,
                Organization = x.Organization!.Name,
                x.UserId,
                User = x.User!.DisplayName,
                UserEmail = x.User!.DefaultEmail,
                x.Role,
                x.IsActive,
                x.CreatedAt
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<object> ListUserMembershipsAsync(string userId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.User)
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Organization!.Name)
            .Select(x => new
            {
                x.OrganizationId,
                Organization = x.Organization!.Name,
                x.UserId,
                User = x.User!.DisplayName,
                UserEmail = x.User!.DefaultEmail,
                x.Role,
                x.IsActive,
                x.CreatedAt
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<object> ListClientsAsync(
        string? source = null,
        string? status = null,
        string? search = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var managedClientIds = _options.ClientSeeds
            .Select(static seed => seed.ClientId)
            .Where(static clientId => !string.IsNullOrWhiteSpace(clientId))
            .ToHashSet(StringComparer.Ordinal);

        var clients = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var duplicateFingerprints = clients
            .Where(x => string.Equals(x.RegistrationSource, "dcr", StringComparison.OrdinalIgnoreCase))
            .Select(CalculateDuplicateFingerprint)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var filtered = clients
            .Select(x => FormatClientListItem(
                x,
                managedClientIds.Contains(x.ClientId),
                duplicateFingerprints.TryGetValue(CalculateDuplicateFingerprint(x) ?? string.Empty, out var duplicateCount)
                    ? duplicateCount
                    : 0))
            .Where(item => MatchesSourceFilter(item.RegistrationSource, source))
            .Where(item => MatchesStatusFilter(item.IsActive, item.DisabledAt, status))
            .Where(item => MatchesClientSearch(item, search))
            .ToList();

        var totalCount = filtered.Count;
        var activeCount = filtered.Count(item => item.IsActive && item.DisabledAt == null);
        var discoveredCount = filtered.Count(item => string.Equals(item.RegistrationSource, "cimd", StringComparison.OrdinalIgnoreCase));
        var registeredCount = filtered.Count(item => string.Equals(item.RegistrationSource, "dcr", StringComparison.OrdinalIgnoreCase));
        var disabledCount = filtered.Count(item => !item.IsActive || item.DisabledAt != null);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)resolvedPageSize));
        var currentPage = Math.Min(resolvedPage, totalPages);
        var data = filtered
            .Skip((currentPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .Cast<object>()
            .ToList();

        return new
        {
            Data = data,
            Page = currentPage,
            PageSize = resolvedPageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Summary = new
            {
                ActiveCount = activeCount,
                DiscoveredCount = discoveredCount,
                RegisteredCount = registeredCount,
                DisabledCount = disabledCount
            }
        };
    }

    public async Task<object> GetClientDetailAsync(string clientApplicationId, CancellationToken cancellationToken = default)
    {
        var managedClientIds = _options.ClientSeeds
            .Select(static seed => seed.ClientId)
            .Where(static clientId => !string.IsNullOrWhiteSpace(clientId))
            .ToHashSet(StringComparer.Ordinal);
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var duplicateFingerprint = CalculateDuplicateFingerprint(client);
        var duplicateCount = 0;
        if (!string.IsNullOrWhiteSpace(duplicateFingerprint))
        {
            var dcrClients = await _context.Set<SqlOSClientApplication>()
                .AsNoTracking()
                .Where(x => x.RegistrationSource == "dcr")
                .ToListAsync(cancellationToken);
            duplicateCount = dcrClients
                .Select(CalculateDuplicateFingerprint)
                .Count(value => string.Equals(value, duplicateFingerprint, StringComparison.Ordinal));
        }

        var recentAuditEvents = await _context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .Where(x => x.ActorId == client.Id || (x.DataJson != null && x.DataJson.Contains(client.ClientId)))
            .OrderByDescending(x => x.OccurredAt)
            .Take(20)
            .Select(x => new
            {
                x.Id,
                x.EventType,
                x.ActorType,
                x.ActorId,
                x.OccurredAt,
                x.DataJson
            })
            .ToListAsync(cancellationToken);

        var item = FormatClientListItem(client, managedClientIds.Contains(client.ClientId), duplicateCount);
        return new
        {
            item.Id,
            item.ClientId,
            item.Name,
            item.Description,
            item.Audience,
            item.ClientType,
            item.RegistrationSource,
            item.SourceLabel,
            item.TokenEndpointAuthMethod,
            item.RequirePkce,
            item.IsFirstParty,
            item.RedirectUris,
            item.GrantTypes,
            item.ResponseTypes,
            item.AllowedScopes,
            item.MetadataDocumentUrl,
            item.ClientUri,
            item.LogoUri,
            item.SoftwareId,
            item.SoftwareVersion,
            item.MetadataFetchedAt,
            item.MetadataExpiresAt,
            item.MetadataCacheState,
            item.LastSeenAt,
            item.IsActive,
            item.DisabledAt,
            item.DisabledReason,
            item.ManagedByStartupSeed,
            item.CoreMetadataEditable,
            item.DuplicateFingerprint,
            item.DuplicateCount,
            item.LifecycleState,
            client.MetadataJson,
            RecentAuditEvents = recentAuditEvents
        };
    }

    public async Task<SqlOSClientApplication> DisableClientAsync(string clientApplicationId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        if (!client.IsActive && client.DisabledAt != null)
        {
            return client;
        }

        client.IsActive = false;
        client.DisabledAt = DateTime.UtcNow;
        client.DisabledReason = string.IsNullOrWhiteSpace(reason) ? "disabled_by_operator" : reason.Trim();
        await RevokeClientSessionsInternalAsync(client.Id, "client_disabled", cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "client.disabled",
            "client",
            client.Id,
            ipAddress: null,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource,
                reason = client.DisabledReason
            },
            cancellationToken: cancellationToken);
        return client;
    }

    public async Task<SqlOSClientApplication> EnableClientAsync(string clientApplicationId, CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        client.IsActive = true;
        client.DisabledAt = null;
        client.DisabledReason = null;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "client.enabled",
            "client",
            client.Id,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource
            },
            cancellationToken: cancellationToken);
        return client;
    }

    public async Task<int> RevokeClientSessionsAsync(string clientApplicationId, string reason = "client_revoked", CancellationToken cancellationToken = default)
    {
        var client = await GetRequiredClientByIdAsync(clientApplicationId, cancellationToken);
        var revokedCount = await RevokeClientSessionsInternalAsync(client.Id, reason, cancellationToken);
        if (revokedCount > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        await RecordAuditAsync(
            "client.sessions-revoked",
            "client",
            client.Id,
            data: new
            {
                client_id = client.ClientId,
                source = client.RegistrationSource,
                revoked_sessions = revokedCount,
                reason
            },
            cancellationToken: cancellationToken);
        return revokedCount;
    }

    public async Task<int> CleanupStaleDynamicClientsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.ClientRegistration.Dcr.EnableAutomaticCleanup)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - _options.ClientRegistration.Dcr.StaleClientRetention;
        var candidates = await _context.Set<SqlOSClientApplication>()
            .Where(x => x.RegistrationSource == "dcr"
                && (x.LastSeenAt == null || x.LastSeenAt < cutoff)
                && x.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        var removed = 0;
        foreach (var client in candidates)
        {
            var hasAnySessions = await _context.Set<SqlOSSession>()
                .AnyAsync(x => x.ClientApplicationId == client.Id, cancellationToken);
            if (hasAnySessions)
            {
                continue;
            }

            _context.Set<SqlOSClientApplication>().Remove(client);
            await _context.SaveChangesAsync(cancellationToken);
            removed++;

            await RecordAuditAsync(
                "client.cleanup.removed",
                "client",
                client.Id,
                data: new
                {
                    client_id = client.ClientId,
                    source = client.RegistrationSource
                },
                cancellationToken: cancellationToken);
        }

        return removed;
    }

    public async Task<List<object>> ListOidcConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _context.Set<SqlOSOidcConnection>()
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        return connections
            .Select(x => new
            {
                x.Id,
                ProviderType = x.ProviderType.ToString(),
                x.DisplayName,
                x.LogoDataUrl,
                EffectiveLogoDataUrl = SqlOSOidcProviderLogoCatalog.ResolveEffectiveLogoDataUrl(x.ProviderType, x.LogoDataUrl),
                x.ClientId,
                AllowedCallbackUris = x.AllowedCallbackUrisJson,
                x.UseDiscovery,
                x.DiscoveryUrl,
                x.Issuer,
                x.AuthorizationEndpoint,
                x.TokenEndpoint,
                x.UserInfoEndpoint,
                x.JwksUri,
                x.MicrosoftTenant,
                Scopes = x.ScopesJson,
                ClaimMapping = x.ClaimMappingJson,
                ClientAuthMethod = x.ClientAuthMethod.ToString(),
                x.UseUserInfo,
                x.AppleTeamId,
                x.AppleKeyId,
                x.IsEnabled,
                x.CreatedAt,
                x.UpdatedAt
            })
            .Cast<object>()
            .ToList();
    }

    public async Task<List<object>> ListSsoConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _context.Set<SqlOSSsoConnection>()
            .Include(x => x.Organization)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        return connections
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.IdentityProviderEntityId,
                x.SingleSignOnUrl,
                x.IsEnabled,
                Organization = x.Organization!.Name,
                x.OrganizationId,
                x.Organization!.PrimaryDomain,
                x.AutoProvisionUsers,
                x.AutoLinkByEmail,
                SetupStatus = string.IsNullOrWhiteSpace(x.IdentityProviderEntityId) || string.IsNullOrWhiteSpace(x.SingleSignOnUrl) || string.IsNullOrWhiteSpace(x.X509CertificatePem)
                    ? "draft"
                    : "configured",
                ServiceProviderEntityId = GetServiceProviderEntityId(),
                AssertionConsumerServiceUrl = GetAssertionConsumerServiceUrl(x.Id)
            })
            .Cast<object>()
            .ToList();
    }

    public async Task<object> ListOrganizationSsoConnectionsAsync(string organizationId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSSsoConnection>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.IdentityProviderEntityId,
                x.SingleSignOnUrl,
                x.IsEnabled,
                OrganizationName = x.Organization!.Name,
                x.OrganizationId,
                PrimaryDomain = x.Organization!.PrimaryDomain,
                x.AutoProvisionUsers,
                x.AutoLinkByEmail,
                SetupStatus = string.IsNullOrWhiteSpace(x.IdentityProviderEntityId) || string.IsNullOrWhiteSpace(x.SingleSignOnUrl) || string.IsNullOrWhiteSpace(x.X509CertificatePem)
                    ? "draft"
                    : "configured"
            });

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)resolvedPageSize));
        var data = await query
            .Skip((resolvedPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .ToListAsync(cancellationToken);
        var serviceProviderEntityId = GetServiceProviderEntityId();

        return new
        {
            Data = data.Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.IdentityProviderEntityId,
                x.SingleSignOnUrl,
                x.IsEnabled,
                Organization = x.OrganizationName,
                x.OrganizationId,
                PrimaryDomain = x.PrimaryDomain,
                x.AutoProvisionUsers,
                x.AutoLinkByEmail,
                x.SetupStatus,
                ServiceProviderEntityId = serviceProviderEntityId,
                AssertionConsumerServiceUrl = GetAssertionConsumerServiceUrl(x.Id)
            }).ToList(),
            Page = resolvedPage,
            PageSize = resolvedPageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public async Task<object> ListSessionsAsync(int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSSession>()
            .AsNoTracking()
            .Include(x => x.User)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.AuthenticationMethod,
                User = x.User!.DisplayName,
                x.UserId,
                x.ClientApplicationId,
                x.CreatedAt,
                x.LastSeenAt,
                x.IdleExpiresAt,
                x.AbsoluteExpiresAt,
                x.RevokedAt,
                x.UserAgent,
                x.IpAddress
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<object> ListUserSessionsAsync(string userId, int? page = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSSession>()
            .AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.AuthenticationMethod,
                User = x.User!.DisplayName,
                x.UserId,
                x.ClientApplicationId,
                x.CreatedAt,
                x.LastSeenAt,
                x.IdleExpiresAt,
                x.AbsoluteExpiresAt,
                x.RevokedAt,
                x.UserAgent,
                x.IpAddress
            });

        return await PaginateAsync(query, resolvedPage, resolvedPageSize, cancellationToken);
    }

    public async Task<List<object>> ListAuditEventsAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSAuditEvent>()
            .OrderByDescending(x => x.OccurredAt)
            .Take(200)
            .Select(x => new
            {
                x.Id,
                x.EventType,
                x.ActorType,
                x.ActorId,
                x.UserId,
                x.OrganizationId,
                x.SessionId,
                x.OccurredAt,
                x.DataJson
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

    private static (int Page, int PageSize) NormalizePagination(int? page, int? pageSize)
    {
        var resolvedPage = page.GetValueOrDefault(1);
        var resolvedPageSize = pageSize.GetValueOrDefault(10);
        resolvedPage = Math.Max(1, resolvedPage);
        resolvedPageSize = Math.Clamp(resolvedPageSize, 1, 100);
        return (resolvedPage, resolvedPageSize);
    }

    private static async Task<object> PaginateAsync<T>(IQueryable<T> query, int page, int pageSize, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var data = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new
        {
            Data = data,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public async Task RecordAuditAsync(
        string eventType,
        string actorType,
        string? actorId,
        string? userId = null,
        string? organizationId = null,
        string? sessionId = null,
        string? ipAddress = null,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        _context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = _cryptoService.GenerateId("evt"),
            EventType = eventType,
            ActorType = actorType,
            ActorId = actorId,
            UserId = userId,
            OrganizationId = organizationId,
            SessionId = sessionId,
            IpAddress = ipAddress,
            DataJson = data != null ? JsonSerializer.Serialize(data) : null,
            OccurredAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static string? NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var atIndex = normalized.LastIndexOf('@');
        if (atIndex >= 0)
        {
            normalized = normalized[(atIndex + 1)..];
        }

        normalized = normalized.Trim().Trim('.').Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public string GetServiceProviderEntityId() => _options.Issuer;

    public string GetAssertionConsumerServiceUrl(string connectionId)
    {
        if (Uri.TryCreate(_options.Issuer, UriKind.Absolute, out var issuerUri))
        {
            var authority = issuerUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var basePath = _options.BasePath.Trim();
            if (!basePath.StartsWith("/", StringComparison.Ordinal))
            {
                basePath = "/" + basePath;
            }

            return $"{authority}{basePath.TrimEnd('/')}/saml/acs/{connectionId}";
        }

        return $"{_options.Issuer.TrimEnd('/')}/saml/acs/{connectionId}";
    }

    public static List<string> NormalizeCallbackUris(IEnumerable<string>? values, string? connectionId = null)
        => values?
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ReplaceConnectionIdPlaceholder(value!, connectionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    public static List<string> NormalizeScopes(IEnumerable<string>? values)
        => values?
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    public static SqlOSOidcClaimMapping NormalizeClaimMapping(SqlOSOidcClaimMapping? value)
    {
        value ??= new SqlOSOidcClaimMapping();

        return new SqlOSOidcClaimMapping
        {
            SubjectClaim = string.IsNullOrWhiteSpace(value.SubjectClaim) ? "sub" : value.SubjectClaim.Trim(),
            EmailClaim = NormalizeOptionalClaim(value.EmailClaim, "email"),
            EmailVerifiedClaim = NormalizeOptionalClaim(value.EmailVerifiedClaim, "email_verified"),
            DisplayNameClaim = NormalizeOptionalClaim(value.DisplayNameClaim, "name"),
            FirstNameClaim = NormalizeOptionalClaim(value.FirstNameClaim, "given_name"),
            LastNameClaim = NormalizeOptionalClaim(value.LastNameClaim, "family_name"),
            PreferredUsernameClaim = NormalizeOptionalClaim(value.PreferredUsernameClaim, "preferred_username")
        };
    }

    public static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    private static SqlOSFederationMetadata ParseFederationMetadata(string metadataXml)
    {
        var xml = new XmlDocument { PreserveWhitespace = false };
        xml.LoadXml(metadataXml);

        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("md", "urn:oasis:names:tc:SAML:2.0:metadata");
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");

        var entityId = xml.SelectSingleNode("/md:EntityDescriptor/@entityID", ns)?.InnerText
            ?? throw new InvalidOperationException("Federation metadata is missing the entityID attribute.");

        var ssoNode = xml.SelectSingleNode("//md:IDPSSODescriptor/md:SingleSignOnService[@Binding='urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect']", ns)
            ?? xml.SelectSingleNode("//md:IDPSSODescriptor/md:SingleSignOnService[@Binding='urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST']", ns)
            ?? throw new InvalidOperationException("Federation metadata is missing an IdP SingleSignOnService endpoint.");

        var ssoUrl = ssoNode.Attributes?["Location"]?.Value
            ?? throw new InvalidOperationException("Federation metadata SSO endpoint is missing its Location attribute.");

        var certificateNode = xml.SelectSingleNode("//md:IDPSSODescriptor/md:KeyDescriptor[@use='signing']//ds:X509Certificate", ns)
            ?? xml.SelectSingleNode("//md:IDPSSODescriptor/md:KeyDescriptor[not(@use)]//ds:X509Certificate", ns)
            ?? throw new InvalidOperationException("Federation metadata is missing an X509 signing certificate.");

        var certificateBase64 = string.Concat(certificateNode.InnerText.Where(ch => !char.IsWhiteSpace(ch)));
        if (string.IsNullOrWhiteSpace(certificateBase64))
        {
            throw new InvalidOperationException("Federation metadata certificate value is empty.");
        }

        var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateBase64));
        var certificatePem = ToPem(certificate.Export(X509ContentType.Cert));

        return new SqlOSFederationMetadata(entityId, ssoUrl, certificatePem);
    }

    private static string ToPem(byte[] rawCertificate)
    {
        var base64 = Convert.ToBase64String(rawCertificate, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN CERTIFICATE-----\n{base64}\n-----END CERTIFICATE-----";
    }

    private static string? NormalizeMicrosoftTenant(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeOptionalClaim(string? value, string? fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeRequiredUrl(string? value, string message)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private static string? NormalizeOptionalUrl(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizePrivateKey(string value)
        => value.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string ReplaceConnectionIdPlaceholder(string value, string? connectionId)
        => string.IsNullOrWhiteSpace(connectionId)
            ? value
            : value.Replace("{connectionId}", connectionId, StringComparison.OrdinalIgnoreCase);

    private static void ValidateOidcSecretRequirements(SqlOSOidcConnection connection)
    {
        if (connection.ProviderType == SqlOSOidcProviderType.Apple)
        {
            if (string.IsNullOrWhiteSpace(connection.AppleTeamId) ||
                string.IsNullOrWhiteSpace(connection.AppleKeyId) ||
                string.IsNullOrWhiteSpace(connection.ApplePrivateKeyEncrypted))
            {
                throw new InvalidOperationException("Apple OIDC connections require team ID, key ID, and a private key.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(connection.ClientSecretEncrypted))
        {
            throw new InvalidOperationException("This OIDC connection requires a client secret.");
        }
    }

    private static NormalizedOidcConfiguration NormalizeOidcConfiguration(
        SqlOSOidcProviderType providerType,
        bool useDiscovery,
        string? discoveryUrl,
        string? issuer,
        string? authorizationEndpoint,
        string? tokenEndpoint,
        string? userInfoEndpoint,
        string? jwksUri,
        string? microsoftTenant,
        IEnumerable<string>? scopes,
        SqlOSOidcClaimMapping? claimMapping,
        SqlOSOidcClientAuthMethod? clientAuthMethod,
        bool? useUserInfo,
        string? appleTeamId,
        string? appleKeyId)
    {
        var normalizedScopes = NormalizeScopes(scopes);
        var normalizedClaimMapping = NormalizeClaimMapping(claimMapping);
        var normalizedTenant = providerType == SqlOSOidcProviderType.Microsoft ? NormalizeMicrosoftTenant(microsoftTenant) : null;
        var effectiveUseDiscovery = providerType != SqlOSOidcProviderType.Custom || useDiscovery;
        var effectiveClientAuthMethod = clientAuthMethod ?? SqlOSOidcClientAuthMethod.ClientSecretPost;
        var effectiveUseUserInfo = useUserInfo ?? providerType != SqlOSOidcProviderType.Apple;

        if (providerType == SqlOSOidcProviderType.Google)
        {
            return new NormalizedOidcConfiguration(
                true,
                "https://accounts.google.com/.well-known/openid-configuration",
                null,
                null,
                null,
                null,
                null,
                null,
                normalizedScopes,
                normalizedClaimMapping,
                effectiveClientAuthMethod,
                true,
                null,
                null);
        }

        if (providerType == SqlOSOidcProviderType.Microsoft)
        {
            var tenant = normalizedTenant ?? "common";
            return new NormalizedOidcConfiguration(
                true,
                $"https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration",
                null,
                null,
                null,
                null,
                null,
                tenant,
                normalizedScopes,
                normalizedClaimMapping,
                effectiveClientAuthMethod,
                true,
                null,
                null);
        }

        if (providerType == SqlOSOidcProviderType.Apple)
        {
            return new NormalizedOidcConfiguration(
                true,
                "https://appleid.apple.com/.well-known/openid-configuration",
                null,
                null,
                null,
                null,
                null,
                null,
                normalizedScopes,
                new SqlOSOidcClaimMapping
                {
                    SubjectClaim = "sub",
                    EmailClaim = "email",
                    EmailVerifiedClaim = "email_verified",
                    DisplayNameClaim = null,
                    FirstNameClaim = "given_name",
                    LastNameClaim = "family_name",
                    PreferredUsernameClaim = null
                },
                SqlOSOidcClientAuthMethod.ClientSecretPost,
                false,
                string.IsNullOrWhiteSpace(appleTeamId) ? null : appleTeamId.Trim(),
                string.IsNullOrWhiteSpace(appleKeyId) ? null : appleKeyId.Trim());
        }

        if (effectiveUseDiscovery)
        {
            return new NormalizedOidcConfiguration(
                true,
                NormalizeRequiredUrl(discoveryUrl, "A discovery URL is required for custom OIDC connections when discovery mode is enabled."),
                null,
                null,
                null,
                null,
                null,
                null,
                normalizedScopes,
                normalizedClaimMapping,
                effectiveClientAuthMethod,
                effectiveUseUserInfo,
                null,
                null);
        }

        return new NormalizedOidcConfiguration(
            false,
            null,
            NormalizeRequiredUrl(issuer, "An issuer is required for manual OIDC connections."),
            NormalizeRequiredUrl(authorizationEndpoint, "An authorization endpoint is required for manual OIDC connections."),
            NormalizeRequiredUrl(tokenEndpoint, "A token endpoint is required for manual OIDC connections."),
            NormalizeOptionalUrl(userInfoEndpoint),
            NormalizeRequiredUrl(jwksUri, "A JWKS URI is required for manual OIDC connections."),
            null,
            normalizedScopes,
            normalizedClaimMapping,
            effectiveClientAuthMethod,
            effectiveUseUserInfo,
            null,
            null);
    }

    private NormalizedClientDefinition NormalizeSeededClient(SqlOSClientSeedOptions seed)
        => NormalizeClientDefinition(
            seed.ClientId,
            seed.Name,
            seed.Audience,
            seed.RedirectUris,
            seed.Description,
            seed.AllowedScopes,
            seed.RequirePkce,
            seed.IsFirstParty,
            seed.ClientType,
            seed.IsActive);

    private static ClientAdminView FormatClientListItem(SqlOSClientApplication client, bool managedByStartupSeed, int duplicateCount)
    {
        var redirectUris = DeserializeJsonList(client.RedirectUrisJson);
        var grantTypes = DeserializeJsonList(client.GrantTypesJson);
        var responseTypes = DeserializeJsonList(client.ResponseTypesJson);
        var allowedScopes = DeserializeJsonList(client.AllowedScopesJson);
        var duplicateFingerprint = CalculateDuplicateFingerprint(client);

        return new ClientAdminView(
            client.Id,
            client.ClientId,
            client.Name,
            client.Description,
            client.Audience,
            client.ClientType,
            client.RegistrationSource,
            GetSourceLabel(client.RegistrationSource),
            client.TokenEndpointAuthMethod,
            client.RequirePkce,
            client.IsFirstParty,
            redirectUris,
            grantTypes,
            responseTypes,
            allowedScopes,
            client.MetadataDocumentUrl,
            client.ClientUri,
            client.LogoUri,
            client.SoftwareId,
            client.SoftwareVersion,
            client.MetadataFetchedAt,
            client.MetadataExpiresAt,
            GetMetadataCacheState(client),
            client.LastSeenAt,
            client.IsActive,
            client.DisabledAt,
            client.DisabledReason,
            managedByStartupSeed,
            string.Equals(client.RegistrationSource, "manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(client.RegistrationSource, "seeded", StringComparison.OrdinalIgnoreCase),
            duplicateFingerprint,
            duplicateCount,
            client.DisabledAt != null || !client.IsActive ? "disabled" : "active");
    }

    private static bool MatchesSourceFilter(string registrationSource, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(registrationSource, filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesStatusFilter(bool isActive, DateTime? disabledAt, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filter.Trim().ToLowerInvariant() switch
        {
            "active" => isActive && disabledAt == null,
            "disabled" => !isActive || disabledAt != null,
            _ => true
        };
    }

    private static bool MatchesClientSearch(ClientAdminView item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var normalized = search.Trim();
        return (item.Name?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.ClientId.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || item.SourceLabel.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || (item.Description?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.Audience.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || (item.SoftwareId?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.SoftwareVersion?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.MetadataDocumentUrl?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetSourceLabel(string registrationSource)
        => registrationSource?.Trim().ToLowerInvariant() switch
        {
            "seeded" => "Seeded",
            "manual" => "Manual",
            "cimd" => "Discovered",
            "dcr" => "Registered",
            _ => "Unknown"
        };

    private static string? GetMetadataCacheState(SqlOSClientApplication client)
    {
        if (!string.Equals(client.RegistrationSource, "cimd", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (client.MetadataExpiresAt == null)
        {
            return "unknown";
        }

        return client.MetadataExpiresAt <= DateTime.UtcNow
            ? "stale"
            : "fresh";
    }

    private static string? CalculateDuplicateFingerprint(SqlOSClientApplication client)
    {
        if (!string.Equals(client.RegistrationSource, "dcr", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var redirectUris = DeserializeJsonList(client.RedirectUrisJson);
        var softwareId = client.SoftwareId ?? string.Empty;
        var softwareVersion = client.SoftwareVersion ?? string.Empty;
        var clientUri = client.ClientUri ?? string.Empty;
        return string.Join("|", redirectUris.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
            + $"|{softwareId}|{softwareVersion}|{clientUri}";
    }

    private async Task<SqlOSClientApplication> GetRequiredClientByIdAsync(string clientApplicationId, CancellationToken cancellationToken)
    {
        var client = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.Id == clientApplicationId, cancellationToken);
        if (client != null)
        {
            return client;
        }

        client = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.ClientId == clientApplicationId, cancellationToken);
        return client ?? throw new InvalidOperationException("Client application was not found.");
    }

    private async Task<int> RevokeClientSessionsInternalAsync(string clientApplicationId, string reason, CancellationToken cancellationToken)
    {
        var sessions = await _context.Set<SqlOSSession>()
            .Where(x => x.ClientApplicationId == clientApplicationId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var sessionIds = sessions.Select(x => x.Id).ToList();
        var refreshTokens = await _context.Set<SqlOSRefreshToken>()
            .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }

        foreach (var refreshToken in refreshTokens)
        {
            refreshToken.RevokedAt = now;
        }

        return sessions.Count;
    }

    private NormalizedClientDefinition NormalizeClientRequest(SqlOSCreateClientRequest request)
        => NormalizeClientDefinition(
            request.ClientId,
            request.Name,
            request.Audience,
            request.RedirectUris,
            request.Description,
            request.AllowedScopes,
            request.RequirePkce,
            request.IsFirstParty,
            request.ClientType,
            true);

    private NormalizedClientDefinition NormalizeClientDefinition(
        string clientId,
        string name,
        string? audience,
        IEnumerable<string>? redirectUris,
        string? description,
        IEnumerable<string>? allowedScopes,
        bool requirePkce,
        bool isFirstParty,
        string? clientType,
        bool isActive)
    {
        var normalizedClientId = RequireText(clientId, nameof(clientId));
        var normalizedName = RequireText(name, nameof(name));
        var normalizedAudience = string.IsNullOrWhiteSpace(audience)
            ? _options.DefaultAudience
            : audience.Trim();
        var normalizedClientType = string.IsNullOrWhiteSpace(clientType)
            ? "public_pkce"
            : clientType.Trim();
        var normalizedRedirectUris = (redirectUris ?? [])
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Select(static uri => uri.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedRedirectUris.Count == 0)
        {
            throw new InvalidOperationException($"Client '{normalizedClientId}' must define at least one redirect URI.");
        }

        var normalizedAllowedScopes = (allowedScopes ?? [])
            .Where(static scope => !string.IsNullOrWhiteSpace(scope))
            .Select(static scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new NormalizedClientDefinition(
            normalizedClientId,
            normalizedName,
            normalizedAudience,
            normalizedRedirectUris,
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            normalizedAllowedScopes,
            normalizedClientType,
            requirePkce,
            isFirstParty,
            isActive);
    }

    private static string RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    internal static List<string> DeserializeJsonList(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record ClientAdminView(
        string Id,
        string ClientId,
        string Name,
        string? Description,
        string Audience,
        string ClientType,
        string RegistrationSource,
        string SourceLabel,
        string TokenEndpointAuthMethod,
        bool RequirePkce,
        bool IsFirstParty,
        List<string> RedirectUris,
        List<string> GrantTypes,
        List<string> ResponseTypes,
        List<string> AllowedScopes,
        string? MetadataDocumentUrl,
        string? ClientUri,
        string? LogoUri,
        string? SoftwareId,
        string? SoftwareVersion,
        DateTime? MetadataFetchedAt,
        DateTime? MetadataExpiresAt,
        string? MetadataCacheState,
        DateTime? LastSeenAt,
        bool IsActive,
        DateTime? DisabledAt,
        string? DisabledReason,
        bool ManagedByStartupSeed,
        bool CoreMetadataEditable,
        string? DuplicateFingerprint,
        int DuplicateCount,
        string LifecycleState);

    private sealed record SqlOSFederationMetadata(
        string IdentityProviderEntityId,
        string SingleSignOnUrl,
        string X509CertificatePem);

    private sealed record NormalizedOidcConfiguration(
        bool UseDiscovery,
        string? DiscoveryUrl,
        string? Issuer,
        string? AuthorizationEndpoint,
        string? TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        string? MicrosoftTenant,
        List<string> Scopes,
        SqlOSOidcClaimMapping ClaimMapping,
        SqlOSOidcClientAuthMethod ClientAuthMethod,
        bool UseUserInfo,
        string? AppleTeamId,
        string? AppleKeyId);

    private sealed record NormalizedClientDefinition(
        string ClientId,
        string Name,
        string Audience,
        List<string> RedirectUris,
        string? Description,
        List<string> AllowedScopes,
        string ClientType,
        bool RequirePkce,
        bool IsFirstParty,
        bool IsActive);
}
