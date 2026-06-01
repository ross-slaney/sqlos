using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSSsoPortalServiceTests
{
    [TestMethod]
    public async Task CreateSessionAsync_CreatesDraftAndStoresHashedOneTimeLink()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", null, "acme.test"));

        var result = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id, Provider: "okta"),
            harness.Http);

        result.OrganizationId.Should().Be(org.Id);
        result.Provider.Should().Be("okta");
        result.SetupUrl.Should().Contain("/sqlos/admin/auth/sso-portal/start?token=");
        result.SetupUrl.Should().NotContain(result.Id);

        var rawToken = ExtractToken(result.SetupUrl!);
        var stored = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync();
        stored.LinkTokenHash.Should().Be(harness.Crypto.HashToken(rawToken));
        result.SetupUrl.Should().NotContain(stored.LinkTokenHash);

        var connection = await harness.Context.Set<SqlOSSsoConnection>().SingleAsync();
        connection.OrganizationId.Should().Be(org.Id);
        connection.IsEnabled.Should().BeFalse();
        SqlOSAdminService.GetSsoSetupStatus(connection).Should().Be("draft");

        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "sso.portal.session.created"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task OpenSessionAsync_SetsServerSideCookieAndRejectsReuse()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Open Org", null, "open.test"));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var rawToken = ExtractToken(created.SetupUrl!);

        var openHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(rawToken, openHttp);

        var cookie = openHttp.Response.Headers.SetCookie.ToString();
        cookie.Should().Contain("sqlos_sso_portal=");
        var requestCookie = cookie.Split(';', 2)[0];
        var followupHttp = PortalHarness.CreateHttpContext();
        followupHttp.Request.Headers.Cookie = requestCookie;

        var session = await harness.Portal.TryGetSessionAsync(followupHttp);
        session.Should().NotBeNull();
        session!.OrganizationId.Should().Be(org.Id);
        session.SessionTokenHash.Should().NotBeNullOrWhiteSpace();
        requestCookie.Should().NotContain(session.SessionTokenHash);

        var reuse = async () => await harness.Portal.OpenSessionAsync(rawToken, PortalHarness.CreateHttpContext());
        await reuse.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal setup token has already been used.");
    }

    [TestMethod]
    public async Task RevokeSessionAsync_PreventsPortalCookieAccess()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Revoke Org", null, "revoke.test"));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var openHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(ExtractToken(created.SetupUrl!), openHttp);
        var requestCookie = openHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        var revoked = await harness.Portal.RevokeSessionAsync(created.Id, new SqlOSRevokeSsoPortalSessionRequest("security_review"), harness.Http);
        revoked.Status.Should().Be("revoked");

        var followupHttp = PortalHarness.CreateHttpContext();
        followupHttp.Request.Headers.Cookie = requestCookie;
        (await harness.Portal.TryGetSessionAsync(followupHttp)).Should().BeNull();
        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "sso.portal.session.revoked"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ImportMetadataAndActivateAsync_EnableHomeRealmDiscoveryForOnlyPortalOrganization()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Portal Org", null, "portal.test"));
        var otherOrg = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Other Org", null, "other.test"));
        var otherConnection = await harness.Admin.CreateSsoConnectionDraftAsync(
            new SqlOSCreateSsoConnectionDraftRequest(otherOrg.Id, "Other SSO", null, true, false));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);
        session.ConnectionId = otherConnection.Id;
        await harness.Context.SaveChangesAsync();

        var metadataXml = BuildMetadata("urn:portal:idp", "https://idp.portal.test/sso");
        var state = await harness.Portal.ImportMetadataAsync(session, new SqlOSSsoPortalMetadataRequest(metadataXml), harness.Http);

        state.Organization.Id.Should().Be(org.Id);
        state.Connection.Id.Should().NotBe(otherConnection.Id);
        state.Connection.SetupStatus.Should().Be("ready_to_activate");
        state.Connection.IsEnabled.Should().BeFalse();

        state = await harness.Portal.ActivateAsync(session, harness.Http);
        state.Connection.SetupStatus.Should().Be("active");

        var hrd = await new SqlOSHomeRealmDiscoveryService(harness.Context)
            .DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest("user@portal.test"));
        hrd.Mode.Should().Be("sso");
        hrd.OrganizationId.Should().Be(org.Id);
        hrd.ConnectionId.Should().Be(state.Connection.Id);

        var otherStored = await harness.Context.Set<SqlOSSsoConnection>().SingleAsync(x => x.Id == otherConnection.Id);
        otherStored.IdentityProviderEntityId.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ValidateMetadata_ReturnsActionableErrors()
    {
        using var harness = await PortalHarness.CreateAsync();

        var result = harness.Portal.ValidateMetadata(new SqlOSSsoPortalMetadataRequest("<not-metadata />"));

        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.IdentityProviderEntityId.Should().BeNull();
    }

    private static string ExtractToken(string setupUrl)
    {
        var marker = "token=";
        var index = setupUrl.IndexOf(marker, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0);
        var token = setupUrl[(index + marker.Length)..];
        var ampersand = token.IndexOf('&');
        if (ampersand >= 0)
        {
            token = token[..ampersand];
        }

        return Uri.UnescapeDataString(token);
    }

    private static string BuildMetadata(string entityId, string singleSignOnUrl)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSPortalIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var rawCertificate = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));

        return $"""
        <EntityDescriptor xmlns="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{entityId}">
          <IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <KeyDescriptor use="signing">
              <KeyInfo xmlns="http://www.w3.org/2000/09/xmldsig#">
                <X509Data>
                  <X509Certificate>{rawCertificate}</X509Certificate>
                </X509Data>
              </KeyInfo>
            </KeyDescriptor>
            <SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect" Location="{singleSignOnUrl}" />
          </IDPSSODescriptor>
        </EntityDescriptor>
        """;
    }

    private sealed class PortalHarness : IDisposable
    {
        public required TestSqlOSInMemoryDbContext Context { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSSsoPortalService Portal { get; init; }
        public required DefaultHttpContext Http { get; init; }

        public static async Task<PortalHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions
            {
                PublicOrigin = "https://auth.example.test",
                Issuer = "https://auth.example.test/sqlos/auth"
            };
            configure?.Invoke(authOptions);
            var options = Options.Create(authOptions);
            var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var portal = new SqlOSSsoPortalService(context, options, crypto, admin);

            await crypto.EnsureActiveSigningKeyAsync();

            return new PortalHarness
            {
                Context = context,
                Crypto = crypto,
                Admin = admin,
                Portal = portal,
                Http = CreateHttpContext()
            };
        }

        public static DefaultHttpContext CreateHttpContext()
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.11");
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            return http;
        }

        public void Dispose()
            => Context.Dispose();
    }
}
