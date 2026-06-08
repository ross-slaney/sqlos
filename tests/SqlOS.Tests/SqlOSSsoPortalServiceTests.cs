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
using SqlOS.AuthServer.Interfaces;
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
        connection.AutoProvisionUsers.Should().BeTrue();
        connection.AutoLinkByEmail.Should().BeTrue();
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
    public async Task DomainVerificationAsync_CreatesTxtRecordAndEnablesVerifiedHomeRealmDiscovery()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Verified Org", null));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);

        var state = await harness.Portal.StartDomainVerificationAsync(
            session,
            new SqlOSSsoPortalDomainRequest("admin@Verified.TEST"),
            harness.Http);

        state.Domain.Should().NotBeNull();
        state.Domain!.Domain.Should().Be("verified.test");
        state.Domain.Status.Should().Be(SqlOSOrganizationDomainStatuses.PendingOwnership);
        state.Domain.OwnershipRecord.Should().NotBeNull();
        state.Domain.OwnershipRecord!.Type.Should().Be("TXT");
        state.Domain.OwnershipRecord.Name.Should().Be("_sqlos-verify.verified.test");
        state.Domain.OwnershipRecord.Value.Should().StartWith("sqlos-domain-verification=");

        state = await harness.Portal.ConfirmDomainOwnershipAsync(session, state.Domain.Id, harness.Http);
        state.Domain!.Status.Should().Be(SqlOSOrganizationDomainStatuses.PendingOwnership);
        state.Domain.LastError.Should().Contain("TXT record not found");

        harness.Dns.AddTxt(state.Domain.OwnershipRecord!.Name, state.Domain.OwnershipRecord.Value);
        state = await harness.Portal.ConfirmDomainOwnershipAsync(session, state.Domain.Id, harness.Http);
        state.Domain!.Status.Should().Be(SqlOSOrganizationDomainStatuses.Active);

        var metadataXml = BuildMetadata("urn:verified:idp", "https://idp.verified.test/sso");
        await harness.Portal.ImportMetadataAsync(session, new SqlOSSsoPortalMetadataRequest(metadataXml), harness.Http);
        state = await harness.Portal.ActivateAsync(session, harness.Http);
        state.Connection.SetupStatus.Should().Be("active");

        var hrd = await new SqlOSHomeRealmDiscoveryService(harness.Context)
            .DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest("user@verified.test"));
        hrd.Mode.Should().Be("sso");
        hrd.OrganizationId.Should().Be(org.Id);
        hrd.PrimaryDomain.Should().Be("verified.test");
    }

    [TestMethod]
    public async Task DomainVerificationAsync_UsesConfiguredOwnershipRecordBranding()
    {
        using var harness = await PortalHarness.CreateAsync(options =>
            options.ConfigureSsoPortal(portal =>
            {
                portal.DomainVerificationRecordPrefix = "_mcpstack-verify";
                portal.DomainVerificationRecordValuePrefix = "mcpstack-domain-verification";
            }));
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Branded Org", null));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);

        var state = await harness.Portal.StartDomainVerificationAsync(
            session,
            new SqlOSSsoPortalDomainRequest("branded.test"),
            harness.Http);

        state.Domain.Should().NotBeNull();
        state.Domain!.OwnershipRecord.Should().NotBeNull();
        state.Domain.OwnershipRecord!.Name.Should().Be("_mcpstack-verify.branded.test");
        state.Domain.OwnershipRecord.Value.Should().StartWith("mcpstack-domain-verification=");
        state.Domain.OwnershipRecord.Value.Should().NotContain("sqlos");
    }

    [TestMethod]
    public async Task ActivateAsync_BlocksPendingSelfServeDomainUntilOwnershipIsVerified()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Pending Org", null));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);
        var metadataXml = BuildMetadata("urn:pending:idp", "https://idp.pending.test/sso");

        await harness.Portal.ImportMetadataAsync(session, new SqlOSSsoPortalMetadataRequest(metadataXml), harness.Http);
        await harness.Portal.StartDomainVerificationAsync(
            session,
            new SqlOSSsoPortalDomainRequest("pending.test"),
            harness.Http);

        var action = async () => await harness.Portal.ActivateAsync(session, harness.Http);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Verify domain ownership before activating SSO for home realm discovery.");
    }

    [TestMethod]
    public async Task GetSetupActionAsync_ReturnsHeadlessSetupViewModel()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Headless Org", null, "headless.test"));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id, Provider: "okta"), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);

        var result = await harness.Portal.GetSetupActionAsync(session, "domain");

        result.Type.Should().Be("view");
        result.RedirectUrl.Should().BeNull();
        result.ViewModel.Should().NotBeNull();
        result.ViewModel!.View.Should().Be("domain");
        result.ViewModel.SetupApiBasePath.Should().Be("/sqlos/admin/auth/sso-portal/api/setup");
        result.ViewModel.Provider.Should().Be("okta");
        result.ViewModel.AllowedActions.CanStartDomainVerification.Should().BeTrue();
        result.ViewModel.ServiceProvider.AssertionConsumerServiceUrl.Should().Contain("/saml/acs/");
    }

    [TestMethod]
    public async Task TryBuildSetupUiUrl_UsesConfiguredBrowserHandoff()
    {
        using var harness = await PortalHarness.CreateAsync(options =>
        {
            options.SsoPortal.UseHostedPortal = false;
            options.SsoPortal.BuildUiUrl = ctx =>
                $"https://admin.example.test/sso/setup?session_id={ctx.SessionId}&org_id={ctx.OrganizationId}&view={ctx.View}";
        });
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Route Org", null, "route.test"));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);

        var url = harness.Portal.TryBuildSetupUiUrl(harness.Http, created.Id, org.Id, "metadata");

        url.Should().Be($"https://admin.example.test/sso/setup?session_id={created.Id}&org_id={org.Id}&view=metadata");
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

    [TestMethod]
    public void RenderShell_ReturnsPortalUi()
    {
        var html = SqlOSSsoPortalPageRenderer.RenderShell();

        html.Should().Contain("<!doctype html>");
        html.Should().Contain("SqlOS SSO Portal");
        html.Should().Contain("./api");
        html.Should().Contain("Domain Verification");
        html.Should().Contain("Confirm TXT record");
        html.Should().Contain("Validate metadata");
        html.Should().Contain("Activate connection");
        html.Should().Contain("Run test");
        html.Should().Contain("Open IdP test redirect");
    }

    [TestMethod]
    public void RenderStartError_HtmlEncodesMessage()
    {
        var html = SqlOSSsoPortalPageRenderer.RenderStartError("<script>alert('x')</script>");

        html.Should().Contain("Setup link unavailable");
        html.Should().Contain("&lt;script&gt;alert");
        html.Should().NotContain("<script>alert");
    }

    [TestMethod]
    public void ConfigureSsoPortal_AllowsPortalOptionsToBeCustomized()
    {
        var options = new SqlOSAuthServerOptions()
            .ConfigureSsoPortal(portal =>
            {
                portal.DefaultLinkLifetime = TimeSpan.FromHours(12);
                portal.SessionIdleTimeout = TimeSpan.FromMinutes(45);
                portal.CookieName = "custom_sso_portal";
                portal.EnableApi = false;
                portal.UseHostedPortal = false;
                portal.RequireVerifiedDomainForActivation = false;
                portal.AllowLocalhostDomainVerification = true;
                portal.HeadlessApiBasePath = "/custom/sso/setup";
                portal.DomainVerificationRecordPrefix = "_custom-verify";
                portal.DomainVerificationRecordValuePrefix = "custom-domain-verification";
            });

        options.SsoPortal.DefaultLinkLifetime.Should().Be(TimeSpan.FromHours(12));
        options.SsoPortal.SessionIdleTimeout.Should().Be(TimeSpan.FromMinutes(45));
        options.SsoPortal.CookieName.Should().Be("custom_sso_portal");
        options.SsoPortal.EnableApi.Should().BeFalse();
        options.SsoPortal.UseHostedPortal.Should().BeFalse();
        options.SsoPortal.RequireVerifiedDomainForActivation.Should().BeFalse();
        options.SsoPortal.AllowLocalhostDomainVerification.Should().BeTrue();
        options.SsoPortal.ResolveHeadlessApiBasePath("/sqlos/admin/auth").Should().Be("/custom/sso/setup");
        options.SsoPortal.DomainVerificationRecordPrefix.Should().Be("_custom-verify");
        options.SsoPortal.DomainVerificationRecordValuePrefix.Should().Be("custom-domain-verification");
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
        public required SqlOSOrganizationDomainService Domains { get; init; }
        public required FakeDomainDnsVerifier Dns { get; init; }
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
            var dns = new FakeDomainDnsVerifier();
            var domains = new SqlOSOrganizationDomainService(context, options, crypto, admin, dns);
            var portal = new SqlOSSsoPortalService(context, options, crypto, admin, domains);

            await crypto.EnsureActiveSigningKeyAsync();

            return new PortalHarness
            {
                Context = context,
                Crypto = crypto,
                Admin = admin,
                Domains = domains,
                Dns = dns,
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

    private sealed class FakeDomainDnsVerifier : ISqlOSDomainDnsVerifier
    {
        private readonly Dictionary<string, HashSet<string>> _records = new(StringComparer.OrdinalIgnoreCase);

        public void AddTxt(string recordName, string value)
        {
            if (!_records.TryGetValue(recordName, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                _records[recordName] = values;
            }

            values.Add(SqlOSDomainOwnershipVerification.NormalizeTxtValue(value));
        }

        public Task<bool> HasTxtRecordValueAsync(
            string recordName,
            string expectedValue,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_records.TryGetValue(recordName, out var values)
                && values.Contains(SqlOSDomainOwnershipVerification.NormalizeTxtValue(expectedValue)));
    }
}
