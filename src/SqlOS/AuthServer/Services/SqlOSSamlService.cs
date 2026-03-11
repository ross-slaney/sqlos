using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Security;
using System.IO.Compression;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSSamlService
{
    private const string SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string SamlAssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;

    public SqlOSSamlService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService)
    {
        _context = context;
        _options = options.Value;
        _adminService = adminService;
        _cryptoService = cryptoService;
    }

    public async Task<string> CreateAuthorizationUrlAsync(SqlOSAuthorizationUrlRequest request, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>()
            .FirstOrDefaultAsync(x => x.Id == request.ConnectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found or disabled.");
        var client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken);

        var requestToken = await _cryptoService.CreateTemporaryTokenAsync(
            "sso_request",
            null,
            client.Id,
            connection.OrganizationId,
            new SsoRequestPayload(client.ClientId, request.RedirectUri, connection.Id),
            TimeSpan.FromMinutes(10),
            cancellationToken);

        return $"{_options.BasePath.TrimEnd('/')}/saml/login/{connection.Id}?requestToken={Uri.EscapeDataString(requestToken)}";
    }

    public async Task<string> BuildIdentityProviderRedirectAsync(string connectionId, string requestToken, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>().FirstOrDefaultAsync(x => x.Id == connectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found or disabled.");
        var requestState = await _cryptoService.FindTemporaryTokenAsync("sso_request", requestToken, cancellationToken)
            ?? throw new InvalidOperationException("SSO request token is invalid or expired.");

        _ = requestState;
        return BuildIdentityProviderRedirectUrl(connection, requestToken);
    }

    public async Task<string> BuildIdentityProviderRedirectForAuthorizationRequestAsync(string authorizationRequestId, CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _context.Set<SqlOSAuthorizationRequest>()
            .Include(x => x.Connection)
            .FirstOrDefaultAsync(x => x.Id == authorizationRequestId, cancellationToken)
            ?? throw new InvalidOperationException("Authorization request was not found.");

        if (authorizationRequest.CancelledAt != null || authorizationRequest.CompletedAt != null || authorizationRequest.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Authorization request is no longer active.");
        }

        var connection = authorizationRequest.Connection;
        if (connection == null || !connection.IsEnabled)
        {
            throw new InvalidOperationException("SAML connection not found or disabled.");
        }

        return BuildIdentityProviderRedirectUrl(connection, authorizationRequest.Id);
    }

    public async Task<string> HandleAcsAsync(string connectionId, string samlResponse, string relayState, CancellationToken cancellationToken = default)
    {
        var connection = await _context.Set<SqlOSSsoConnection>().FirstOrDefaultAsync(x => x.Id == connectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("SAML connection not found or disabled.");
        var principal = ParseAndValidateAssertion(samlResponse, connection);
        var authorizationRequest = await _context.Set<SqlOSAuthorizationRequest>()
            .FirstOrDefaultAsync(x => x.Id == relayState && x.ConnectionId == connectionId, cancellationToken);

        if (authorizationRequest != null)
        {
            return await HandleAuthorizationRequestAcsAsync(connection, authorizationRequest, principal, cancellationToken);
        }

        var requestToken = await _cryptoService.ConsumeTemporaryTokenAsync("sso_request", relayState, cancellationToken)
            ?? throw new InvalidOperationException("SSO request token is invalid or expired.");
        var requestPayload = _cryptoService.DeserializePayload<SsoRequestPayload>(requestToken)
            ?? throw new InvalidOperationException("SSO request payload is invalid.");

        return await HandleLegacyAcsAsync(connection, requestToken, requestPayload, principal, cancellationToken);
    }

    private async Task<string> HandleAuthorizationRequestAcsAsync(
        SqlOSSsoConnection connection,
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSSamlPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (authorizationRequest.CancelledAt != null || authorizationRequest.CompletedAt != null || authorizationRequest.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Authorization request is no longer active.");
        }

        var email = principal.Attributes.TryGetValue(connection.EmailAttributeName, out var emailValue) ? emailValue : authorizationRequest.LoginHintEmail;
        var user = await ResolveUserAsync(connection, principal, email, cancellationToken)
            ?? throw new InvalidOperationException("No user could be resolved from the SAML assertion.");

        if (!await _adminService.UserHasMembershipAsync(user.Id, authorizationRequest.OrganizationId, cancellationToken))
        {
            await _adminService.CreateMembershipAsync(authorizationRequest.OrganizationId, new SqlOSCreateMembershipRequest(user.Id, "member"), cancellationToken);
        }

        var rawCode = _cryptoService.GenerateOpaqueToken();
        _context.Set<SqlOSAuthorizationCode>().Add(new SqlOSAuthorizationCode
        {
            Id = _cryptoService.GenerateId("acd"),
            AuthorizationRequestId = authorizationRequest.Id,
            UserId = user.Id,
            ClientApplicationId = authorizationRequest.ClientApplicationId,
            OrganizationId = authorizationRequest.OrganizationId,
            RedirectUri = authorizationRequest.RedirectUri,
            State = authorizationRequest.State,
            CodeHash = _cryptoService.HashToken(rawCode),
            CodeChallenge = authorizationRequest.CodeChallenge,
            CodeChallengeMethod = authorizationRequest.CodeChallengeMethod,
            AuthenticationMethod = "saml",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        authorizationRequest.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.login.saml", "user", user.Id, userId: user.Id, organizationId: authorizationRequest.OrganizationId, cancellationToken: cancellationToken);
        var separator = authorizationRequest.RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{authorizationRequest.RedirectUri}{separator}code={Uri.EscapeDataString(rawCode)}&state={Uri.EscapeDataString(authorizationRequest.State)}";
    }

    private async Task<string> HandleLegacyAcsAsync(
        SqlOSSsoConnection connection,
        SqlOSTemporaryToken requestToken,
        SsoRequestPayload requestPayload,
        SqlOSSamlPrincipal principal,
        CancellationToken cancellationToken)
    {
        var email = principal.Attributes.TryGetValue(connection.EmailAttributeName, out var emailValue) ? emailValue : null;
        var user = await ResolveUserAsync(connection, principal, email, cancellationToken)
            ?? throw new InvalidOperationException("No user could be resolved from the SAML assertion.");

        if (!await _adminService.UserHasMembershipAsync(user.Id, connection.OrganizationId, cancellationToken))
        {
            await _adminService.CreateMembershipAsync(connection.OrganizationId, new SqlOSCreateMembershipRequest(user.Id, "member"), cancellationToken);
        }

        var code = await _cryptoService.CreateTemporaryTokenAsync(
            "auth_code",
            user.Id,
            requestToken.ClientApplicationId,
            connection.OrganizationId,
            new AuthCodePayload(requestPayload.ClientId, requestPayload.RedirectUri, "saml"),
            TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        await _adminService.RecordAuditAsync("user.login.saml", "user", user.Id, userId: user.Id, organizationId: connection.OrganizationId, cancellationToken: cancellationToken);
        var separator = requestPayload.RedirectUri.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{requestPayload.RedirectUri}{separator}code={Uri.EscapeDataString(code)}";
    }

    private string BuildAuthnRequest(SqlOSSsoConnection connection, string acsUrl)
    {
        var requestId = $"_{Guid.NewGuid():N}";
        var issueInstant = DateTime.UtcNow.ToString("o");

        var xml = $"""
        <samlp:AuthnRequest xmlns:samlp="{SamlProtocolNs}" xmlns:saml="{SamlAssertionNs}" ID="{requestId}" Version="2.0" IssueInstant="{issueInstant}" Destination="{connection.SingleSignOnUrl}" AssertionConsumerServiceURL="{acsUrl}">
          <saml:Issuer>{SecurityElement.Escape(_options.Issuer)}</saml:Issuer>
        </samlp:AuthnRequest>
        """;

        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            deflater.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private string BuildIdentityProviderRedirectUrl(SqlOSSsoConnection connection, string relayState)
    {
        var authnRequest = BuildAuthnRequest(connection, _adminService.GetAssertionConsumerServiceUrl(connection.Id));
        var query = $"SAMLRequest={Uri.EscapeDataString(authnRequest)}&RelayState={Uri.EscapeDataString(relayState)}";
        var separator = connection.SingleSignOnUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{connection.SingleSignOnUrl}{separator}{query}";
    }

    private SqlOSSamlPrincipal ParseAndValidateAssertion(string base64Response, SqlOSSsoConnection connection)
    {
        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(base64Response));
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xml);

        var ns = new XmlNamespaceManager(xmlDoc.NameTable);
        ns.AddNamespace("samlp", SamlProtocolNs);
        ns.AddNamespace("saml", SamlAssertionNs);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        ValidateSignature(xmlDoc, connection.X509CertificatePem, ns);

        var issuer = xmlDoc.SelectSingleNode("/samlp:Response/saml:Issuer", ns)?.InnerText
            ?? xmlDoc.SelectSingleNode("/samlp:Response/saml:Assertion/saml:Issuer", ns)?.InnerText
            ?? throw new InvalidOperationException("SAML response issuer missing.");
        if (!string.Equals(issuer, connection.IdentityProviderEntityId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SAML response issuer mismatch.");
        }

        var nameId = xmlDoc.SelectSingleNode("//saml:Assertion/saml:Subject/saml:NameID", ns)?.InnerText
            ?? throw new InvalidOperationException("SAML assertion NameID missing.");

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attributeNodes = xmlDoc.SelectNodes("//saml:AttributeStatement/saml:Attribute", ns);
        if (attributeNodes != null)
        {
            foreach (XmlNode attribute in attributeNodes)
            {
                var name = attribute.Attributes?["Name"]?.Value;
                var value = attribute.SelectSingleNode("saml:AttributeValue", ns)?.InnerText;
                if (!string.IsNullOrWhiteSpace(name) && value != null)
                {
                    attributes[name] = value;
                }
            }
        }

        return new SqlOSSamlPrincipal(issuer, nameId, attributes);
    }

    private static void ValidateSignature(XmlDocument xmlDocument, string certificatePem, XmlNamespaceManager ns)
    {
        var signatureNode = xmlDocument.SelectSingleNode("/samlp:Response/ds:Signature", ns)
            ?? xmlDocument.SelectSingleNode("//saml:Assertion/ds:Signature", ns);
        if (signatureNode is not XmlElement signatureElement)
        {
            throw new InvalidOperationException("SAML response signature missing.");
        }

        var signedElement = signatureElement.ParentNode as XmlElement
            ?? throw new InvalidOperationException("Signed SAML element missing.");
        var signedXml = new SignedXml(signedElement);
        signedXml.LoadXml(signatureElement);
        var certificate = X509Certificate2.CreateFromPem(certificatePem);
        if (!signedXml.CheckSignature(certificate, true))
        {
            throw new InvalidOperationException("SAML response signature is invalid.");
        }
    }

    private async Task<SqlOSUser?> ResolveUserAsync(
        SqlOSSsoConnection connection,
        SqlOSSamlPrincipal principal,
        string? email,
        CancellationToken cancellationToken)
    {
        var externalIdentity = await _context.Set<SqlOSExternalIdentity>()
            .FirstOrDefaultAsync(x => x.SsoConnectionId == connection.Id && x.Subject == principal.Subject, cancellationToken);
        if (externalIdentity != null)
        {
            return await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == externalIdentity.UserId, cancellationToken);
        }

        SqlOSUser? user = null;
        if (connection.AutoLinkByEmail && !string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
            var existingEmail = await _context.Set<SqlOSUserEmail>().FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
            if (existingEmail != null)
            {
                user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == existingEmail.UserId, cancellationToken);
            }
        }

        if (user == null && connection.AutoProvisionUsers && !string.IsNullOrWhiteSpace(email))
        {
            var displayName = $"{principal.Attributes.GetValueOrDefault(connection.FirstNameAttributeName, string.Empty)} {principal.Attributes.GetValueOrDefault(connection.LastNameAttributeName, string.Empty)}".Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = email;
            }

            user = new SqlOSUser
            {
                Id = _cryptoService.GenerateId("usr"),
                DisplayName = displayName,
                DefaultEmail = email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Set<SqlOSUser>().Add(user);
            _context.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
            {
                Id = _cryptoService.GenerateId("eml"),
                UserId = user.Id,
                Email = email,
                NormalizedEmail = SqlOSAdminService.NormalizeEmail(email),
                IsPrimary = true,
                IsVerified = true,
                VerifiedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (user == null)
        {
            return null;
        }

        _context.Set<SqlOSExternalIdentity>().Add(new SqlOSExternalIdentity
        {
            Id = _cryptoService.GenerateId("ext"),
            UserId = user.Id,
            SsoConnectionId = connection.Id,
            Issuer = principal.Issuer,
            Subject = principal.Subject,
            Email = email,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private sealed record SsoRequestPayload(string ClientId, string RedirectUri, string ConnectionId);
    private sealed record AuthCodePayload(string ClientId, string RedirectUri, string AuthenticationMethod);
    private sealed record SqlOSSamlPrincipal(string Issuer, string Subject, Dictionary<string, string> Attributes);
}
