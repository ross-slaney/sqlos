using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
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

    public SqlOSAdminService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService)
    {
        _context = context;
        _options = options.Value;
        _cryptoService = cryptoService;
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

    public async Task<SqlOSClientApplication> EnsureDefaultClientAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.ClientId == "default", cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var client = new SqlOSClientApplication
        {
            Id = _cryptoService.GenerateId("cli"),
            ClientId = "default",
            Name = "Default Client",
            Audience = _options.DefaultAudience,
            RedirectUrisJson = "[]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSClientApplication>().Add(client);
        await _context.SaveChangesAsync(cancellationToken);
        return client;
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
        if (await _context.Set<SqlOSClientApplication>().AnyAsync(x => x.ClientId == request.ClientId, cancellationToken))
        {
            throw new InvalidOperationException($"Client '{request.ClientId}' already exists.");
        }

        var client = new SqlOSClientApplication
        {
            Id = _cryptoService.GenerateId("cli"),
            ClientId = request.ClientId,
            Name = request.Name,
            Audience = request.Audience,
            RedirectUrisJson = JsonSerializer.Serialize(request.RedirectUris),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        _context.Set<SqlOSClientApplication>().Add(client);
        await _context.SaveChangesAsync(cancellationToken);
        return client;
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

    public async Task<SqlOSClientApplication> RequireClientAsync(string? clientId, string? redirectUri, CancellationToken cancellationToken = default)
    {
        SqlOSClientApplication? client;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            client = await _context.Set<SqlOSClientApplication>().FirstOrDefaultAsync(x => x.ClientId == "default", cancellationToken);
        }
        else
        {
            client = await _context.Set<SqlOSClientApplication>().FirstOrDefaultAsync(x => x.ClientId == clientId, cancellationToken);
        }

        if (client == null || !client.IsActive)
        {
            throw new InvalidOperationException("Client application not found or inactive.");
        }

        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            var redirectUris = JsonSerializer.Deserialize<List<string>>(client.RedirectUrisJson) ?? new List<string>();
            if (redirectUris.Count > 0 && !redirectUris.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Redirect URI is not allowed for this client.");
            }
        }

        return client;
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
        var clients = await _context.Set<SqlOSClientApplication>().CountAsync(cancellationToken);
        var eventsCount = await _context.Set<SqlOSAuditEvent>().CountAsync(cancellationToken);
        return new { users, organizations = orgs, sessions, ssoConnections = connections, clients, auditEvents = eventsCount };
    }

    public async Task<List<object>> ListUsersAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSUser>()
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                x.DefaultEmail,
                x.IsActive,
                x.CreatedAt
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

    public async Task<List<object>> ListOrganizationsAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSOrganization>()
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.Slug,
                x.PrimaryDomain,
                x.IsActive,
                MembershipCount = x.Memberships.Count,
                EnabledSsoConnections = x.SsoConnections.Count(c => c.IsEnabled)
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

    public async Task<List<object>> ListMembershipsAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSMembership>()
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
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

    public async Task<List<object>> ListClientsAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSClientApplication>()
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.ClientId,
                x.Name,
                x.Audience,
                RedirectUris = x.RedirectUrisJson,
                x.IsActive
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

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

    public async Task<List<object>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSSession>()
            .Include(x => x.User)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new
            {
                x.Id,
                x.AuthenticationMethod,
                User = x.User!.DisplayName,
                x.ClientApplicationId,
                x.CreatedAt,
                x.LastSeenAt,
                x.RevokedAt,
                x.UserAgent,
                x.IpAddress
            })
            .Cast<object>()
            .ToListAsync(cancellationToken);

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
        => $"{_options.Issuer.TrimEnd('/')}/saml/acs/{connectionId}";

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

    private sealed record SqlOSFederationMetadata(
        string IdentityProviderEntityId,
        string SingleSignOnUrl,
        string X509CertificatePem);
}
