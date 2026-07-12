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
        connection.AutoProvisionUsers.Should().BeFalse();
        connection.AutoLinkByEmail.Should().BeTrue();
        SqlOSAdminService.GetSsoSetupStatus(connection).Should().Be("draft");

        var state = await harness.Portal.GetStateAsync(session: stored);
        state.Connection.EnrollmentPolicy.Should().NotBeNull();
        state.Connection.EnrollmentPolicy!.RequireSsoForExistingMembers.Should().BeTrue();
        state.Connection.EnrollmentPolicy.AllowJitProvisioning.Should().BeFalse();

        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "sso.portal.session.created"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task UpdateEnrollmentPolicyAsync_PersistsConnectionFlagsAndReturnsState()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Policy Org", null, "policy.test"));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);

        var state = await harness.Portal.UpdateEnrollmentPolicyAsync(
            session,
            new SqlOSSsoPortalEnrollmentPolicyRequest(false, true),
            harness.Http);

        state.Connection.AutoLinkByEmail.Should().BeFalse();
        state.Connection.AutoProvisionUsers.Should().BeTrue();
        state.Connection.EnrollmentPolicy.Should().NotBeNull();
        state.Connection.EnrollmentPolicy!.RequireSsoForExistingMembers.Should().BeFalse();
        state.Connection.EnrollmentPolicy.AllowJitProvisioning.Should().BeTrue();

        var stored = await harness.Context.Set<SqlOSSsoConnection>().SingleAsync(x => x.Id == state.Connection.Id);
        stored.AutoLinkByEmail.Should().BeFalse();
        stored.AutoProvisionUsers.Should().BeTrue();
        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "sso.portal.enrollment_policy.updated"))
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

        var user = await CreateVerifiedUserAsync(harness, "Portal User", "user@portal.test");
        harness.Context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await harness.Context.SaveChangesAsync();

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

        var user = await CreateVerifiedUserAsync(harness, "Verified User", "user@verified.test");
        harness.Context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = org.Id,
            UserId = user.Id,
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await harness.Context.SaveChangesAsync();

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
    public async Task RevokeOrganizationSessionsAsync_RevokesOnlyActiveSessionsForOrgAndDomain()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Revoke Sessions Org", null));
        var otherOrg = await harness.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Other Sessions Org", null));
        var created = await harness.Portal.CreateSessionAsync(new SqlOSCreateSsoPortalSessionRequest(org.Id), harness.Http);
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == created.Id);
        var connection = await harness.Context.Set<SqlOSSsoConnection>().SingleAsync(x => x.OrganizationId == org.Id);
        connection.IsEnabled = true;
        connection.IdentityProviderEntityId = "urn:revoke:idp";
        connection.SingleSignOnUrl = "https://idp.revoke.test/sso";
        connection.X509CertificatePem = "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----";
        harness.Context.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
        {
            Id = "dom_revoke",
            OrganizationId = org.Id,
            Domain = "revoke.test",
            Status = SqlOSOrganizationDomainStatuses.Active,
            VerificationToken = "verified",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow
        });

        var matching = await CreateVerifiedUserAsync(harness, "Matching User", "member@revoke.test");
        var unrelatedDomain = await CreateVerifiedUserAsync(harness, "Other Domain User", "member@else.test");
        var otherOrganizationUser = await CreateVerifiedUserAsync(harness, "Other Org User", "other@revoke.test");
        AddSession(harness, "sess_revoke_match", matching.Id, org.Id);
        AddSession(harness, "sess_revoke_domain_miss", unrelatedDomain.Id, org.Id);
        AddSession(harness, "sess_revoke_other_org", otherOrganizationUser.Id, otherOrg.Id);
        await harness.Context.SaveChangesAsync();
        var matchingAuthPage = await harness.Crypto.CreateTemporaryTokenAsync(
            "auth_page_session",
            matching.Id,
            clientApplicationId: null,
            organizationId: org.Id,
            payload: new { AuthenticationMethod = "saml" },
            lifetime: TimeSpan.FromHours(1));
        var unrelatedAuthPage = await harness.Crypto.CreateTemporaryTokenAsync(
            "auth_page_session",
            unrelatedDomain.Id,
            clientApplicationId: null,
            organizationId: org.Id,
            payload: new { AuthenticationMethod = "password" },
            lifetime: TimeSpan.FromHours(1));
        var otherOrgAuthPage = await harness.Crypto.CreateTemporaryTokenAsync(
            "auth_page_session",
            otherOrganizationUser.Id,
            clientApplicationId: null,
            organizationId: otherOrg.Id,
            payload: new { AuthenticationMethod = "saml" },
            lifetime: TimeSpan.FromHours(1));

        var blocked = async () => await harness.Portal.RevokeOrganizationSessionsAsync(
            session,
            new SqlOSSsoPortalRevokeOrganizationSessionsRequest(false),
            harness.Http);
        await blocked.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Confirm session revocation before signing out existing sessions.");

        var result = await harness.Portal.RevokeOrganizationSessionsAsync(
            session,
            new SqlOSSsoPortalRevokeOrganizationSessionsRequest(true),
            harness.Http);

        result.OrganizationId.Should().Be(org.Id);
        result.ConnectionId.Should().Be(connection.Id);
        result.Domain.Should().Be("revoke.test");
        result.RevokedSessions.Should().Be(1);

        var matchingSession = await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_revoke_match");
        matchingSession.RevokedAt.Should().NotBeNull();
        matchingSession.RevocationReason.Should().Be("sso_required");
        (await harness.Context.Set<SqlOSRefreshToken>().SingleAsync(x => x.SessionId == matchingSession.Id)).RevokedAt
            .Should().NotBeNull();

        (await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_revoke_domain_miss")).RevokedAt
            .Should().BeNull();
        (await harness.Context.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_revoke_other_org")).RevokedAt
            .Should().BeNull();
        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", matchingAuthPage)).Should().BeNull();
        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", unrelatedAuthPage)).Should().NotBeNull();
        (await harness.Crypto.FindTemporaryTokenAsync("auth_page_session", otherOrgAuthPage)).Should().NotBeNull();
        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "sso.portal.organization_sessions.revoked"
            && x.OrganizationId == org.Id
            && x.MetadataJson != null
            && x.MetadataJson.Contains("\"revokedSessions\":1", StringComparison.Ordinal)
            && x.MetadataJson.Contains("\"invalidatedAuthPageSessions\":1", StringComparison.Ordinal)))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task RevokeOrganizationSessionsAsync_RevokesSessionThatRefreshedIntoOrganization()
    {
        using var harness = await PortalHarness.CreateAsync();
        var sourceOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Refresh Source Org", null));
        var targetOrganization = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Refresh Target Org", null));
        var created = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(targetOrganization.Id),
            harness.Http);
        var portalSession = await harness.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == created.Id);
        var connection = await harness.Context.Set<SqlOSSsoConnection>()
            .SingleAsync(x => x.OrganizationId == targetOrganization.Id);
        connection.IsEnabled = true;
        connection.IdentityProviderEntityId = "urn:refresh-switch:idp";
        connection.SingleSignOnUrl = "https://idp.refresh-switch.test/sso";
        connection.X509CertificatePem = "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----";
        harness.Context.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
        {
            Id = $"dom_{Guid.NewGuid():N}"[..28],
            OrganizationId = targetOrganization.Id,
            Domain = "refresh-switch.test",
            Status = SqlOSOrganizationDomainStatuses.Active,
            VerificationToken = "verified",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow
        });

        var switchedUser = await CreateVerifiedUserAsync(
            harness,
            "Switched User",
            "switched@refresh-switch.test");
        var unrelatedUser = await CreateVerifiedUserAsync(
            harness,
            "Unrelated Source User",
            "unrelated@refresh-switch.test");
        await harness.Admin.CreateMembershipAsync(
            sourceOrganization.Id,
            new SqlOSCreateMembershipRequest(switchedUser.Id, "member"));
        await harness.Admin.CreateMembershipAsync(
            targetOrganization.Id,
            new SqlOSCreateMembershipRequest(switchedUser.Id, "member"));
        await harness.Admin.CreateMembershipAsync(
            sourceOrganization.Id,
            new SqlOSCreateMembershipRequest(unrelatedUser.Id, "member"));
        var client = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == "sso-switch-client");
        var switchedSourceTokens = await harness.Auth.CreateSessionTokensForUserAsync(
            switchedUser,
            client,
            sourceOrganization.Id,
            "password",
            "SqlOSSsoPortalServiceTests",
            "203.0.113.40");
        var switchedTargetTokens = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(switchedSourceTokens.RefreshToken, targetOrganization.Id));
        var unrelatedSourceTokens = await harness.Auth.CreateSessionTokensForUserAsync(
            unrelatedUser,
            client,
            sourceOrganization.Id,
            "password",
            "SqlOSSsoPortalServiceTests",
            "203.0.113.41");

        (await harness.Context.Set<SqlOSSession>()
            .SingleAsync(x => x.Id == switchedTargetTokens.SessionId))
            .OrganizationId.Should().Be(sourceOrganization.Id);
        (await harness.Context.Set<SqlOSRefreshToken>()
            .AnyAsync(x => x.SessionId == switchedTargetTokens.SessionId
                && x.ReplacementOrganizationId == targetOrganization.Id)).Should().BeTrue();

        var result = await harness.Portal.RevokeOrganizationSessionsAsync(
            portalSession,
            new SqlOSSsoPortalRevokeOrganizationSessionsRequest(true),
            harness.Http);

        result.RevokedSessions.Should().Be(1);
        (await harness.Auth.ValidateAccessTokenAsync(switchedTargetTokens.AccessToken, client.Audience))
            .Should().BeNull();
        var switchedRefresh = async () => await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(switchedTargetTokens.RefreshToken, targetOrganization.Id));
        await switchedRefresh.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Refresh token is no longer valid.");

        (await harness.Auth.ValidateAccessTokenAsync(unrelatedSourceTokens.AccessToken, client.Audience))
            .Should().NotBeNull();
        var unrelatedRefresh = await harness.Auth.RefreshAsync(
            new SqlOSRefreshRequest(unrelatedSourceTokens.RefreshToken, sourceOrganization.Id));
        unrelatedRefresh.OrganizationId.Should().Be(sourceOrganization.Id);
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
        html.Should().Contain("Access Policy");
        html.Should().Contain("Sign out existing sessions");
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

    private static async Task<SqlOSUser> CreateVerifiedUserAsync(PortalHarness harness, string displayName, string email)
    {
        var user = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest(displayName, email, "P@ssword123!"));
        var userEmail = await harness.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id);
        userEmail.IsVerified = true;
        userEmail.VerifiedAt = DateTime.UtcNow;
        await harness.Context.SaveChangesAsync();
        return user;
    }

    private static void AddSession(PortalHarness harness, string sessionId, string userId, string organizationId)
    {
        harness.Context.Set<SqlOSSession>().Add(new SqlOSSession
        {
            Id = sessionId,
            UserId = userId,
            OrganizationId = organizationId,
            AuthenticationMethod = "password",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(8)
        });
        harness.Context.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
        {
            Id = $"rt_{sessionId}",
            SessionId = sessionId,
            TokenHash = $"hash_{sessionId}",
            FamilyId = $"family_{sessionId}",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
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
        public required SqlOSAuthService Auth { get; init; }
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
            authOptions.SeedBrowserClient(
                "sso-switch-client",
                "SSO Switch Client",
                "https://client.example.test/callback");
            configure?.Invoke(authOptions);
            var options = Options.Create(authOptions);
            var crypto = TestCryptoService.Create(context, options, new EphemeralDataProtectionProvider());
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var dns = new FakeDomainDnsVerifier();
            var domains = new SqlOSOrganizationDomainService(context, options, crypto, admin, dns);
            var portal = new SqlOSSsoPortalService(context, options, crypto, admin, domains);

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.EnsureDefaultSettingsAsync();

            return new PortalHarness
            {
                Context = context,
                Crypto = crypto,
                Admin = admin,
                Auth = auth,
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
