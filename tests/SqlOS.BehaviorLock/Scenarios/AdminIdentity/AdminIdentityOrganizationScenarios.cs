using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// The operator's organization administration: creating organizations with derived and explicit
/// slugs and primary domains, listing and searching them, updating and deactivating them, and what
/// deactivation does to members' sessions.
/// </summary>
[TestClass]
public sealed class AdminIdentityOrganizationScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("GET /sqlos/admin/auth/api/organizations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}")]
    public async Task Operator_creates_organizations_with_derived_and_explicit_slugs_and_domains()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var organizations = AdminIdentity.Api + "/organizations";

        var acme = t.Observe(await t.Operator.PostJsonAsync(organizations, new { name = "Acme" }), "the slug is derived from the name");
        var second = t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "Acme Second", slug = "ACME" }),
            "a slug already in use gets a random eight-character suffix");
        AdminIdentity.ScrubSlugSuffix(t, second.JsonString("slug"), "acme");
        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "Globex", slug = "  Globex Industries!  ", primaryDomain = "  Globex.EXAMPLE.test. " }),
            "an explicit slug is slugified and the primary domain trimmed and lower-cased");
        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "Initech", primaryDomain = "it-admin@Initech.Example.Test" }),
            "an email address as the primary domain keeps only its domain");
        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "Hooli", primaryDomain = "globex.example.test" }),
            "a primary domain another organization already has escapes the unique index as a 500");
        t.Observe(await t.Operator.GetAsync(organizations), "organizations are listed by name");
        t.Observe(await t.Operator.GetAsync(organizations + "?search=globex-ind"), "search matches the slug");
        t.Observe(await t.Operator.GetAsync(organizations + "?search=initech.example"), "and the primary domain");
        var page = t.Observe(await t.Operator.GetAsync(organizations + "?pageSize=2"), "two to a page");
        t.Observe(
            await t.Operator.GetAsync(organizations + "?pageSize=2&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "the next page");
        t.Observe(await t.Operator.GetAsync($"{organizations}/{acme.JsonString("id")}"), "the organization detail with its counts");

        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "  Padded Name  " }),
            "a name is stored as sent on create, surrounding spaces included");
        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "<img src=x onerror=alert(1)>" }),
            "markup in a name is stored and returned verbatim");
        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { name = "Ünïcödé Örg" }),
            "non-ASCII letters survive slugification");
        t.Observe(
            await t.Operator.PostJsonAsync(organizations, new { primaryDomain = "nameless.example.test" }),
            "a missing name escapes as a 500");
        t.Observe(await t.Operator.GetAsync(organizations + "/org_missing"), "an unknown organization's detail escapes 'Organization not found.' as a 500");
        await t.ObserveAuditAsync("organization creation writes no audit event");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}")]
    public async Task Operator_renames_moves_and_deactivates_an_organization()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var target = $"{AdminIdentity.Api}/organizations/{acme.Id}";

        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "  Acme Renamed  " }),
            "renaming trims the name and derives a new slug when none is sent");
        var moved = t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = globex.Slug }),
            "moving onto another organization's slug gets a random suffix");
        AdminIdentity.ScrubSlugSuffix(t, moved.JsonString("slug"), globex.Slug);
        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = "acme", primaryDomain = "Acme.Example.Test" }),
            "set a slug and a primary domain");
        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = "acme" }),
            "an update without primaryDomain clears it");
        await t.ObserveAuditAsync("updates that keep the organization active write no audit event");

        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = "acme", isActive = false }),
            "deactivate the organization");
        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = "acme", isActive = false }),
            "deactivating it again is a plain save");
        await t.ObserveAuditAsync("only the SSO-portal session revocation is audited");
        t.Observe(await t.Operator.GetAsync(target), "the inactive organization");
        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = "acme" }),
            "an update that omits isActive reactivates the organization");
        await t.ObserveAuditAsync("reactivation");

        t.Observe(
            await t.Operator.PutJsonAsync($"{AdminIdentity.Api}/organizations/org_missing", new { name = "Ghost" }),
            "an unknown organization escapes 'Organization not found.' as a 500");
        t.Observe(
            await t.Operator.PutJsonAsync(target, new { slug = "acme" }),
            "a missing name escapes as a 500");
        t.Discard(await t.Operator.PutJsonAsync($"{AdminIdentity.Api}/organizations/{globex.Id}", new { name = globex.Name, slug = globex.Slug, primaryDomain = "globex.example.test" }));
        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = "Acme", slug = "acme", primaryDomain = "GLOBEX.example.test" }),
            "a primary domain another organization has escapes the unique index as a 500");
        t.Observe(await t.Operator.GetAsync(target), "the organization is unchanged by the failures");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Deactivating_an_organization_ends_its_members_sessions_until_it_is_reactivated()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice, "admin");
        var aliceSession = await AdminIdentity.PasswordLoginAsync(t, alice, acme.Id);
        var bobSession = await AdminIdentity.PasswordLoginAsync(t, bob);
        var target = $"{AdminIdentity.Api}/organizations/{acme.Id}";

        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = acme.Name, slug = acme.Slug, isActive = false }),
            "deactivate acme");
        await t.ObserveAuditAsync("deactivation");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{alice.Id}/sessions"), "alice's acme session is revoked");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{bob.Id}/sessions"), "bob, not a member, keeps his session");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(aliceSession.RefreshToken)),
            "alice's refresh token is rejected");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(bobSession.RefreshToken)),
            "bob's still refreshes");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId, organizationId = acme.Id }),
            "alice cannot sign in to the inactive organization (the public login lets the refusal escape as a 500)");

        t.Observe(
            await t.Operator.PutJsonAsync(target, new { name = acme.Name, slug = acme.Slug, isActive = true }),
            "reactivate acme");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId, organizationId = acme.Id }),
            "alice signs in to acme again");
        await t.ObserveAuditAsync("reactivation and sign-in");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("POST /__probe/auth/validate")]
    public async Task Deactivating_an_organization_misses_a_session_refreshed_into_it_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice);
        var session = await AdminIdentity.PasswordLoginAsync(t, alice, acme.Id);

        t.Note("Known defect #427, recorded as it behaves today: organization deactivation revokes sessions by the organization they started in, so a session that refreshed into the deactivated organization is not revoked.");
        var switched = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session.RefreshToken, organizationId = globex.Id, clientId = BehaviorLockConstants.AppClientId }),
            "alice's acme session refreshes into globex");
        t.Observe(
            await t.Operator.PutJsonAsync($"{AdminIdentity.Api}/organizations/{globex.Id}", new { name = globex.Name, slug = globex.Slug, isActive = false }),
            "deactivate globex");
        await t.ObserveAuditAsync("deactivation");
        t.Observe(
            await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{alice.Id}/sessions"),
            "the session that switched into globex is still active");
        t.Observe(
            await t.NewClient("probe").PostJsonAsync("/__probe/auth/validate", new { token = switched.JsonString("accessToken"), audience = BehaviorLockConstants.ApiAudience }),
            "its globex access token, as ValidateAccessTokenAsync sees it");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = switched.JsonString("refreshToken"), organizationId = globex.Id, clientId = BehaviorLockConstants.AppClientId }),
            "refreshing into globex again");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = switched.JsonString("refreshToken"), clientId = BehaviorLockConstants.AppClientId }),
            "refreshing without an organization keeps the session alive");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("POST /sqlos/admin/auth/api/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    public async Task Identity_lifecycle_changes_through_the_admin_api_write_no_audit_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);

        t.Note("Known defect #415, recorded as it behaves today: creating a user (with a password), an organization, or a membership, renaming or deactivating an organization, and re-adding a member write no lifecycle audit event, and re-adding a member silently changes the role and reactivates the membership.");
        var user = t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/users", new { displayName = "Alice", email = t.Unique.Email("alice"), password = t.Unique.Password("alice") }),
            "create a user with a password");
        var created = t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/organizations", new { name = "Acme" }),
            "create an organization");
        var organization = $"{AdminIdentity.Api}/organizations/{created.JsonString("id")}";
        t.Observe(await t.Operator.PutJsonAsync(organization, new { name = "Acme Renamed" }), "rename it");
        t.Observe(
            await t.Operator.PostJsonAsync(organization + "/memberships", new { userId = user.JsonString("id"), role = "admin" }),
            "add alice as an admin");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/memberships", new { organizationId = created.JsonString("id"), userId = user.JsonString("id"), role = "member" }),
            "adding her again as a member overwrites the role");
        t.Observe(await t.Operator.GetAsync(organization + "/memberships"), "alice is now a member, not an admin");
        await t.ObserveAuditAsync("none of these changes is audited");
        t.Observe(
            await t.Operator.PutJsonAsync(organization, new { name = "Acme Renamed", isActive = false }),
            "deactivate the organization");
        await t.ObserveAuditAsync("deactivation records only the SSO-portal session revocation, not the deactivation");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/organizations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/applications")]
    public async Task Organization_views_count_its_saml_connections()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var organization = $"{AdminIdentity.Api}/organizations/{acme.Id}";

        t.Observe(await t.Operator.GetAsync(organization), "an organization with one enabled SAML connection");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/organizations"), "the list counts enabled connections");
        await t.Setup.OperatorPostAsync($"{AdminIdentity.Api}/sso-connections/{connectionId}/disable", new { });
        t.Observe(await t.Operator.GetAsync(organization), "a disabled connection still counts, but not as enabled");
        t.Observe(await t.Operator.GetAsync(organization + "/applications"), "the organization's applications");
        await t.ApproveAsync();
    }
}
