using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
    public async Task SignOutAsync_RevokesSessionAndRejectsPredecessorLookupAndMutations()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Sign Out Org", null, "sign-out.test"));
        var opened = await OpenPortalCookieAsync(harness, org.Id, "microsoft-entra");
        var session = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == opened.SessionId);
        var connectionId = session.ConnectionId;
        var tokenHash = session.SessionTokenHash;

        var signOut = PortalHarness.CreateHttpContext();
        signOut.Request.Headers.Cookie = opened.Cookie;
        await harness.Portal.SignOutAsync(signOut);

        var cleared = signOut.Response.Headers.SetCookie.ToString();
        cleared.Should().Contain("sqlos_sso_portal=");
        cleared.ToLowerInvariant().Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax");
        cleared.Should().Contain("path=/sqlos/admin/auth/sso-portal");
        cleared.Should().NotContain(opened.RawToken);
        cleared.Should().NotContain(tokenHash);

        var stored = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == opened.SessionId);
        stored.RevokedAt.Should().NotBeNull();
        stored.RevokedReason.Should().Be(SqlOSSsoPortalService.SignedOutReason);
        stored.SessionTokenHash.Should().Be(tokenHash);
        stored.Provider.Should().Be("microsoft-entra");

        var replay = PortalHarness.CreateHttpContext();
        replay.Request.Headers.Cookie = opened.Cookie;
        (await harness.Portal.TryGetSessionAsync(replay)).Should().BeNull();
        var required = async () => await harness.Portal.GetRequiredSessionAsync(replay);
        await required.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal session is invalid or expired.");

        await AssertPortalMutationsRejectedAsync(harness, stored);

        var connection = await harness.Context.Set<SqlOSSsoConnection>().SingleAsync(x => x.Id == connectionId);
        connection.IsEnabled.Should().BeFalse();
        connection.AutoProvisionUsers.Should().BeFalse();
        stored.Provider.Should().Be("microsoft-entra");
        stored.LastTestedAt.Should().BeNull();

        var listed = JsonSerializer.Serialize(
            await harness.Portal.ListOrganizationSessionsAsync(org.Id));
        listed.Should().Contain("revoked").And.Contain("signed_out");
        listed.Should().NotContain(opened.RawToken).And.NotContain(tokenHash);

        var audit = await harness.Context.Set<SqlOSAuditEvent>()
            .SingleAsync(x => x.EventType == "sso.portal.session.closed");
        audit.OrganizationId.Should().Be(org.Id);
        audit.MetadataJson.Should().Contain(opened.SessionId).And.Contain("signed_out");
        audit.MetadataJson.Should().NotContain(opened.RawToken).And.NotContain(tokenHash);
        audit.DataJson.Should().NotContain(opened.RawToken).And.NotContain(tokenHash);
    }

    [TestMethod]
    public async Task SignOutAsync_IsIdempotentForRepeatedMissingInvalidExpiredAndAlreadyRevokedCookies()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Idempotent Sign Out Org", null, "idempotent-sign-out.test"));
        var opened = await OpenPortalCookieAsync(harness, org.Id);

        var first = PortalHarness.CreateHttpContext();
        first.Request.Headers.Cookie = opened.Cookie;
        await harness.Portal.SignOutAsync(first);
        var second = PortalHarness.CreateHttpContext();
        second.Request.Headers.Cookie = opened.Cookie;
        await harness.Portal.SignOutAsync(second);

        second.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_sso_portal=");
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "sso.portal.session.closed"))
            .Should().Be(1);
        (await harness.Portal.TryGetSessionAsync(CookieContext(opened.Cookie))).Should().BeNull();

        var missing = PortalHarness.CreateHttpContext();
        await harness.Portal.SignOutAsync(missing);
        missing.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_sso_portal=");

        var unrelated = await OpenPortalCookieAsync(harness, org.Id, "okta");
        var invalid = PortalHarness.CreateHttpContext();
        invalid.Request.Headers.Cookie = "sqlos_sso_portal=not-a-portal-token";
        await harness.Portal.SignOutAsync(invalid);
        invalid.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_sso_portal=");
        (await harness.Portal.TryGetSessionAsync(CookieContext(unrelated.Cookie))).Should().NotBeNull();
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "sso.portal.session.closed"))
            .Should().Be(1);

        var expired = await OpenPortalCookieAsync(harness, org.Id);
        var expiredSession = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == expired.SessionId);
        expiredSession.ExpiresAt = DateTime.UtcNow.AddMinutes(-5);
        await harness.Context.SaveChangesAsync();
        var expiredSignOut = PortalHarness.CreateHttpContext();
        expiredSignOut.Request.Headers.Cookie = expired.Cookie;
        await harness.Portal.SignOutAsync(expiredSignOut);
        expiredSession.RevokedAt.Should().NotBeNull();
        expiredSession.RevokedReason.Should().Be(SqlOSSsoPortalService.SignedOutReason);
        expiredSession.ExpiresAt = DateTime.UtcNow.AddHours(1);
        expiredSession.LastSeenAt = DateTime.UtcNow;
        await harness.Context.SaveChangesAsync();
        (await harness.Portal.TryGetSessionAsync(CookieContext(expired.Cookie))).Should().BeNull();

        var revoked = await OpenPortalCookieAsync(harness, org.Id);
        await harness.Portal.RevokeSessionAsync(
            revoked.SessionId,
            new SqlOSRevokeSsoPortalSessionRequest("security_review"),
            harness.Http);
        var alreadyRevoked = PortalHarness.CreateHttpContext();
        alreadyRevoked.Request.Headers.Cookie = revoked.Cookie;
        await harness.Portal.SignOutAsync(alreadyRevoked);
        alreadyRevoked.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_sso_portal=");
        var storedRevoked = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == revoked.SessionId);
        storedRevoked.RevokedReason.Should().Be("security_review");
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x =>
            x.EventType == "sso.portal.session.closed" && x.MetadataJson != null && x.MetadataJson.Contains(revoked.SessionId)))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task SignOutAsync_LeavesUnrelatedPortalSessionActive()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Two Sessions Org", null, "two-sessions.test"));
        var otherOrg = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Other Sessions Org", null, "other-sessions.test"));
        var first = await OpenPortalCookieAsync(harness, org.Id, "okta");
        var second = await OpenPortalCookieAsync(harness, org.Id, "google-workspace");
        var other = await OpenPortalCookieAsync(harness, otherOrg.Id, "microsoft-entra");

        var signOut = PortalHarness.CreateHttpContext();
        signOut.Request.Headers.Cookie = first.Cookie;
        await harness.Portal.SignOutAsync(signOut);

        (await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == first.SessionId))
            .RevokedReason.Should().Be(SqlOSSsoPortalService.SignedOutReason);
        (await harness.Portal.TryGetSessionAsync(CookieContext(second.Cookie))).Should().NotBeNull();
        (await harness.Portal.TryGetSessionAsync(CookieContext(other.Cookie))).Should().NotBeNull();

        var secondSession = await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == second.SessionId);
        var state = await harness.Portal.SetProviderAsync(
            secondSession,
            new SqlOSUpdateSsoPortalProviderRequest("okta"),
            harness.Http);
        state.Provider.Should().Be("okta");
        (await harness.Context.Set<SqlOSSsoPortalSession>().SingleAsync(x => x.Id == other.SessionId))
            .RevokedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task OrganizationDeactivation_RevokesPortalCapabilitiesAndReactivationDoesNotReviveThem()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Deactivated Portal Org", null, "deactivated-portal.test"));
        var pending = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id, Provider: "okta"),
            harness.Http);
        var opened = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id, Provider: "microsoft-entra"),
            harness.Http);
        var openHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(ExtractToken(opened.SetupUrl!), openHttp);
        var openedCookie = openHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        await harness.Admin.UpdateOrganizationAsync(
            org.Id,
            new SqlOSUpdateOrganizationRequest(
                org.Name,
                org.Slug,
                org.PrimaryDomain,
                IsActive: false));

        var revoked = await harness.Context.Set<SqlOSSsoPortalSession>()
            .Where(x => x.OrganizationId == org.Id)
            .ToListAsync();
        revoked.Should().HaveCount(2);
        revoked.Should().OnlyContain(x =>
            x.RevokedAt != null && x.RevokedReason == "organization_deactivated");
        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "sso.portal.sessions.revoked"
            && x.OrganizationId == org.Id
            && x.MetadataJson != null
            && x.MetadataJson.Contains("\"reason\":\"organization_deactivated\"", StringComparison.Ordinal)
            && x.MetadataJson.Contains("\"revokedSessions\":2", StringComparison.Ordinal)))
            .Should().BeTrue();

        var pendingOpen = async () => await harness.Portal.OpenSessionAsync(
            ExtractToken(pending.SetupUrl!),
            PortalHarness.CreateHttpContext());
        await pendingOpen.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal setup token is invalid or expired.");

        var openedRequest = PortalHarness.CreateHttpContext();
        openedRequest.Request.Headers.Cookie = openedCookie;
        (await harness.Portal.TryGetSessionAsync(openedRequest)).Should().BeNull();

        var legacyPending = revoked.Single(x => x.Id == pending.Id);
        legacyPending.RevokedAt = null;
        legacyPending.RevokedReason = null;
        await harness.Context.SaveChangesAsync();

        await harness.Admin.UpdateOrganizationAsync(
            org.Id,
            new SqlOSUpdateOrganizationRequest(
                org.Name,
                org.Slug,
                org.PrimaryDomain,
                IsActive: true));

        await pendingOpen.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal setup token is invalid or expired.");
        var reactivatedRequest = PortalHarness.CreateHttpContext();
        reactivatedRequest.Request.Headers.Cookie = openedCookie;
        (await harness.Portal.TryGetSessionAsync(reactivatedRequest)).Should().BeNull();
        legacyPending.RevokedAt.Should().NotBeNull();
        legacyPending.RevokedReason.Should().Be("organization_reactivated");
        (await harness.Context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "sso.portal.sessions.revoked"
            && x.OrganizationId == org.Id
            && x.MetadataJson != null
            && x.MetadataJson.Contains("\"reason\":\"organization_reactivated\"", StringComparison.Ordinal)
            && x.MetadataJson.Contains("\"revokedSessions\":1", StringComparison.Ordinal)))
            .Should().BeTrue();

        var replacement = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id),
            harness.Http);
        replacement.SetupUrl.Should().NotBeNullOrWhiteSpace();
        replacement.Id.Should().NotBe(pending.Id).And.NotBe(opened.Id);
    }

    [TestMethod]
    public async Task InactiveOrganization_FailsClosedEvenWhenPortalSessionsWereNotRevoked()
    {
        using var harness = await PortalHarness.CreateAsync();
        var org = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Defense In Depth Org", null, "defense-in-depth.test"));
        var pending = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id, Provider: "okta"),
            harness.Http);
        var opened = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id, Provider: "microsoft-entra"),
            harness.Http);
        var openHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(ExtractToken(opened.SetupUrl!), openHttp);
        var openedCookie = openHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        var openedSession = await harness.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == opened.Id);

        org.IsActive = false;
        await harness.Context.SaveChangesAsync();
        (await harness.Context.Set<SqlOSSsoPortalSession>()
            .Where(x => x.OrganizationId == org.Id)
            .AllAsync(x => x.RevokedAt == null)).Should().BeTrue();

        var pendingOpen = async () => await harness.Portal.OpenSessionAsync(
            ExtractToken(pending.SetupUrl!),
            PortalHarness.CreateHttpContext());
        await pendingOpen.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal setup token is invalid or expired.");

        var openedRequest = PortalHarness.CreateHttpContext();
        openedRequest.Request.Headers.Cookie = openedCookie;
        (await harness.Portal.TryGetSessionAsync(openedRequest)).Should().BeNull();

        var read = async () => await harness.Portal.GetStateAsync(openedSession);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal session is invalid or expired.");
        var mutation = async () => await harness.Portal.SetProviderAsync(
            openedSession,
            new SqlOSUpdateSsoPortalProviderRequest("google-workspace"),
            harness.Http);
        await mutation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Portal session is invalid or expired.");
        openedSession.Provider.Should().Be("microsoft-entra");
    }

    [TestMethod]
    public async Task TryGetSessionAsync_RejectsIdleAndAbsoluteExpiry()
    {
        using var harness = await PortalHarness.CreateAsync(options =>
            options.SsoPortal.SessionIdleTimeout = TimeSpan.FromMinutes(1));
        var org = await harness.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Expired Portal Org", null, "expired-portal.test"));
        var idle = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id),
            harness.Http);
        var idleOpenHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(ExtractToken(idle.SetupUrl!), idleOpenHttp);
        var idleCookie = idleOpenHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        var idleSession = await harness.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == idle.Id);
        idleSession.LastSeenAt = DateTime.UtcNow.AddMinutes(-2);

        var absolute = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(org.Id),
            harness.Http);
        var absoluteOpenHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(ExtractToken(absolute.SetupUrl!), absoluteOpenHttp);
        var absoluteCookie = absoluteOpenHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        var absoluteSession = await harness.Context.Set<SqlOSSsoPortalSession>()
            .SingleAsync(x => x.Id == absolute.Id);
        absoluteSession.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await harness.Context.SaveChangesAsync();

        var idleRequest = PortalHarness.CreateHttpContext();
        idleRequest.Request.Headers.Cookie = idleCookie;
        (await harness.Portal.TryGetSessionAsync(idleRequest)).Should().BeNull();
        var absoluteRequest = PortalHarness.CreateHttpContext();
        absoluteRequest.Request.Headers.Cookie = absoluteCookie;
        (await harness.Portal.TryGetSessionAsync(absoluteRequest)).Should().BeNull();
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
        html.Should().Contain("await request(\"/signout\", { method: \"POST\", body: \"{}\" });");
        html.Should().Contain("\"X-SqlOS-Request\": \"1\"");
        html.Should().NotContain("localStorage");
        html.Should().NotContain("sessionStorage");
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

    private static DefaultHttpContext CookieContext(string cookie)
    {
        var http = PortalHarness.CreateHttpContext();
        http.Request.Headers.Cookie = cookie;
        return http;
    }

    private static async Task<(string SessionId, string Cookie, string RawToken)> OpenPortalCookieAsync(
        PortalHarness harness,
        string organizationId,
        string? provider = null)
    {
        var created = await harness.Portal.CreateSessionAsync(
            new SqlOSCreateSsoPortalSessionRequest(organizationId, Provider: provider),
            harness.Http);
        var openHttp = PortalHarness.CreateHttpContext();
        await harness.Portal.OpenSessionAsync(ExtractToken(created.SetupUrl!), openHttp);
        var cookie = openHttp.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        return (created.Id, cookie, cookie["sqlos_sso_portal=".Length..]);
    }

    private static async Task AssertPortalMutationsRejectedAsync(PortalHarness harness, SqlOSSsoPortalSession session)
    {
        var http = harness.Http;
        var mutations = new Func<Task>[]
        {
            () => harness.Portal.GetStateAsync(session),
            () => harness.Portal.GetSetupActionAsync(session),
            () => harness.Portal.SetProviderAsync(session, new SqlOSUpdateSsoPortalProviderRequest("okta"), http),
            () => harness.Portal.SetProviderActionAsync(session, new SqlOSUpdateSsoPortalProviderRequest("okta"), http),
            () => harness.Portal.UpdateEnrollmentPolicyAsync(session, new SqlOSSsoPortalEnrollmentPolicyRequest(false, true), http),
            () => harness.Portal.UpdateEnrollmentPolicyActionAsync(session, new SqlOSSsoPortalEnrollmentPolicyRequest(false, true), http),
            () => harness.Portal.StartDomainVerificationAsync(session, new SqlOSSsoPortalDomainRequest("sign-out.test"), http),
            () => harness.Portal.StartDomainVerificationActionAsync(session, new SqlOSSsoPortalDomainRequest("sign-out.test"), http),
            () => harness.Portal.ConfirmDomainOwnershipAsync(session, "dom_missing", http),
            () => harness.Portal.ConfirmDomainOwnershipActionAsync(session, "dom_missing", http),
            () => harness.Portal.ImportMetadataAsync(session, new SqlOSSsoPortalMetadataRequest("<EntityDescriptor />"), http),
            () => harness.Portal.ImportMetadataActionAsync(session, new SqlOSSsoPortalMetadataRequest("<EntityDescriptor />"), http),
            () => harness.Portal.ActivateAsync(session, http),
            () => harness.Portal.ActivateActionAsync(session, http),
            () => harness.Portal.DisableAsync(session, http),
            () => harness.Portal.DisableActionAsync(session, http),
            () => harness.Portal.RevokeOrganizationSessionsAsync(session, new SqlOSSsoPortalRevokeOrganizationSessionsRequest(true), http),
            () => harness.Portal.RecordTestAsync(session, "ready", "should not persist", null, http),
            () => harness.Portal.RecordTestActionAsync(session, new SqlOSSsoPortalTestRequest(null, null, null, null, null), null!, http)
        };

        foreach (var mutation in mutations)
        {
            var act = async () => await mutation();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Portal session is invalid or expired.");
        }
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
