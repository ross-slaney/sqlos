using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// SCIM user provisioning (RFC 7644 section 3) at <c>/scim/v2</c>: listing and filtering, replace,
/// patch, deactivation, deletion, and reprovisioning, with the sync events and audit records each
/// write leaves behind.
/// </summary>
[TestClass]
public sealed class ScimUserLifecycleScenarios
{
    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("GET /scim/v2/Users")]
    [Covers("GET /scim/v2/Users/{id}")]
    public async Task A_directory_lists_filters_pages_and_projects_its_users()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var ann = t.Unique.Email("ann", domain);
        var bob = t.Unique.Email("bob", domain);
        var cy = t.Unique.Email("cy", domain);

        var annId = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(ann, "directory-ann", "Ann", "Archer", ann), connection.Token),
            "provision Ann").JsonString("id");
        var bobId = t.Discard(await directory.PostAsync($"{Scim.Root}/Users", Scim.User(bob, "directory-bob", "Bob", "Baker", bob), connection.Token)).JsonString("id");
        t.Discard(await directory.PostAsync($"{Scim.Root}/Users", Scim.User(cy, "directory-cy", "Cy", "Cole", cy, active: false), connection.Token));
        t.Scrub(annId, "usr", "ann");
        t.Scrub(bobId, "usr", "bob");
        t.Note("Bob and Cy were provisioned the same way; Cy arrived inactive.");

        t.Observe(await directory.GetAsync($"{Scim.Root}/Users", connection.Token), "list every user, ordered by userName");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter($"userName eq \"{bob}\"")}", connection.Token), "filter by userName");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter("externalId eq \"directory-ann\"")}", connection.Token), "filter by externalId");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter($"emails[type eq \"work\"].value eq \"{ann}\"")}", connection.Token), "filter by work email, as Entra sends it");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter($"emails.value eq \"{bob}\"")}", connection.Token), "filter by emails.value");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter($"id eq \"{annId}\"")}", connection.Token), "filter by id");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter("userName eq \"nobody@example.test\"")}", connection.Token), "a filter that matches nobody");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?startIndex=2&count=1", connection.Token), "the second page of one");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?count=0", connection.Token), "count=0 returns only the total");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?startIndex=0&count=500", connection.Token), "out-of-range paging is clamped");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?attributes=userName,emails.value", connection.Token), "project userName and email values");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?excludedAttributes=meta,groups,name", connection.Token), "exclude meta, groups, and name");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users/{annId}?attributes={Uri.EscapeDataString($"{Scim.UserSchema}:displayName")}", connection.Token), "read one user with a schema-qualified projection");

        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter("displayName eq \"Ann Archer\"")}", connection.Token), "filtering on an unsupported attribute");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter("userName co \"ann\"")}", connection.Token), "an operator other than eq");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter($"userName eq \"{new string('a', 520)}\"")}", connection.Token), "a filter longer than 512 characters");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?attributes=userName&excludedAttributes=emails", connection.Token), "attributes and excludedAttributes together");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users/usr_00000000000000000000000000000000", connection.Token), "a user ID this connection never provisioned");

        await t.ObserveAuditAsync("provisioning events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("PUT /scim/v2/Users/{id}")]
    [Covers("PATCH /scim/v2/Users/{id}")]
    [Covers("DELETE /scim/v2/Users/{id}")]
    [Covers("GET /scim/v2/Users/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    public async Task A_directory_replaces_patches_deactivates_deletes_and_reprovisions_a_user()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var judy = t.Unique.Email("judy", domain);
        var hopps = t.Unique.Email("hopps", domain);

        var created = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), connection.Token),
            "provision Judy");
        var id = created.JsonString("id");
        t.Scrub(id, "usr", "judy");
        var user = $"{Scim.Root}/Users/{id}";

        t.Observe(
            await directory.PutAsync(user, Scim.User(hopps, "directory-judy", "Judith", "Hopps", hopps), connection.Token),
            "replace Judy: new userName, given name, and work email at the verified domain");
        t.Observe(
            await directory.PatchAsync(user, Scim.Patch(
                ("replace", "name.givenName", JsonValue.Create("Jude")),
                ("replace", "displayName", JsonValue.Create("Jude Hopps")),
                ("add", "externalId", JsonValue.Create("directory-jude"))), connection.Token),
            "patch the given name, display name, and externalId");
        t.Observe(
            await directory.PatchAsync(user, Scim.Patch(("replace", null, new JsonObject { ["active"] = false })), connection.Token),
            "deactivate with a pathless replace, as Entra sends it");
        t.Observe(await directory.GetAsync(user, connection.Token), "the deactivated user reads back inactive");
        t.Observe(
            await directory.PatchAsync($"{user}?attributes=active,userName", Scim.Patch(("replace", "active", JsonValue.Create(true))), connection.Token),
            "reactivate, asking for a projected response");
        t.Observe(
            await directory.PatchAsync(user, Scim.Patch(("remove", "name.familyName", null)), connection.Token),
            "remove the family name");

        t.Observe(await directory.DeleteAsync(user, connection.Token), "delete Judy");
        t.Observe(await directory.GetAsync(user, connection.Token), "a deleted user is gone");
        t.Observe(await directory.DeleteAsync(user, connection.Token), "deleting again");
        t.Observe(await directory.PutAsync(user, Scim.User(hopps, "directory-jude", "Jude", "Hopps", hopps), connection.Token), "replacing a deleted user");
        t.Observe(await directory.PatchAsync(user, Scim.Patch(("replace", "active", JsonValue.Create(true))), connection.Token), "patching a deleted user");

        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(hopps, "directory-jude", "Jude", "Hopps", hopps), connection.Token),
            "provisioning the same externalId again reactivates the same person");
        t.Observe(await directory.DeleteAsync(user, connection.Token), "delete again");
        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(hopps, "directory-someone-else", "Jude", "Hopps", hopps), connection.Token),
            "the deleted userName cannot be claimed by a different externalId");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/sync-events?pageSize=50", "dashboard: the connection's sync events, newest first");
        await t.ObserveAuditAsync("user lifecycle events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("GET /scim/v2/Users")]
    [Covers("GET /scim/v2/Users/{id}")]
    [Covers("PUT /scim/v2/Users/{id}")]
    [Covers("PATCH /scim/v2/Users/{id}")]
    [Covers("DELETE /scim/v2/Users/{id}")]
    [Covers("POST /scim/v2/Groups")]
    public async Task One_organizations_directory_cannot_see_or_change_another_organizations_users()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var acmeDomain = await t.VerifyDomainAsync(acme, "acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.VerifyDomainAsync(globex, "globex");
        var acmeDirectory = await t.CreateDirectoryAsync(acme, "Acme directory");
        var globexDirectory = await t.CreateDirectoryAsync(globex, "Globex directory");
        var acmeClient = t.NewClient("acme-directory");
        var globexClient = t.NewClient("globex-directory");
        var judy = t.Unique.Email("judy", acmeDomain);

        var id = t.Observe(
            await acmeClient.PostAsync($"{Scim.Root}/Users", Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), acmeDirectory.Token),
            "Acme provisions Judy").JsonString("id");
        t.Scrub(id, "usr", "judy");
        var user = $"{Scim.Root}/Users/{id}";

        t.Observe(await globexClient.GetAsync($"{Scim.Root}/Users", globexDirectory.Token), "Globex lists its users: Judy is not among them");
        t.Observe(await globexClient.GetAsync($"{Scim.Root}/Users?filter={Scim.Filter($"userName eq \"{judy}\"")}", globexDirectory.Token), "Globex filters for Judy's userName");
        t.Observe(await globexClient.GetAsync(user, globexDirectory.Token), "Globex reads Judy by ID");
        t.Observe(await globexClient.PutAsync(user, Scim.User(judy, "directory-judy", "Mallory", "Hopps", judy), globexDirectory.Token), "Globex replaces Judy");
        t.Observe(await globexClient.PatchAsync(user, Scim.Patch(("replace", "active", JsonValue.Create(false))), globexDirectory.Token), "Globex deactivates Judy");
        t.Observe(await globexClient.DeleteAsync(user, globexDirectory.Token), "Globex deletes Judy");
        t.Observe(
            await globexClient.PostAsync($"{Scim.Root}/Groups", Scim.Group("Globex admins", "globex-admins", id), globexDirectory.Token),
            "Globex puts Judy's ID in one of its groups");
        t.Observe(await acmeClient.GetAsync(user, acmeDirectory.Token), "Judy is unchanged in Acme");

        await t.ObserveAuditAsync("only Acme's provisioning was recorded");
        await t.ApproveAsync();
    }
}
