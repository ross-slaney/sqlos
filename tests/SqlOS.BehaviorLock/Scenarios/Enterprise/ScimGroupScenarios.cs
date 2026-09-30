using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// SCIM groups at <c>/scim/v2/Groups</c>: SqlOS mirrors each directory group into an FGA user group
/// and keeps its members in step with create, replace, and PatchOp member operations.
/// </summary>
[TestClass]
public sealed class ScimGroupScenarios
{
    /// <summary>
    /// SqlOS returns a group's members, and records the subject IDs of a membership change, in no
    /// defined order (an unordered query; subject IDs are random). So that the transcript is
    /// deterministic, every observed member list and every membership change here holds one member;
    /// multi-member states are read with <c>excludedAttributes=members</c> and through each user's
    /// <c>groups</c>.
    /// </summary>
    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("GET /scim/v2/Groups")]
    [Covers("GET /scim/v2/Groups/{id}")]
    [Covers("PUT /scim/v2/Groups/{id}")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    [Covers("DELETE /scim/v2/Groups/{id}")]
    [Covers("GET /scim/v2/Users/{id}")]
    [Covers("GET /sqlos/admin/fga/api/user-groups")]
    public async Task A_directory_creates_lists_replaces_patches_and_deletes_a_group()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var ann = await ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        var bob = await ProvisionAsync(t, directory, connection, domain, "bob", "Bob", "Baker");
        var cy = await ProvisionAsync(t, directory, connection, domain, "cy", "Cy", "Cole");
        t.Note("Ann, Bob, and Cy were provisioned first; their creates are not shown.");

        var created = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-engineering", ann), connection.Token),
            "create Engineering with Ann");
        var id = created.JsonString("id");
        t.Scrub(id, "grp", "engineering");
        var group = $"{Scim.Root}/Groups/{id}";
        t.Observe(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Design", "directory-design"), connection.Token), "create Design with no members");

        t.Observe(await directory.GetAsync($"{Scim.Root}/Groups", connection.Token), "list groups, ordered by displayName");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Groups?filter={Scim.Filter("displayName eq \"Engineering\"")}", connection.Token), "filter by displayName");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Groups?filter={Scim.Filter("externalId eq \"directory-design\"")}", connection.Token), "filter by externalId");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Groups?filter={Scim.Filter($"id eq \"{id}\"")}&excludedAttributes=members", connection.Token), "filter by id without members");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Groups?filter={Scim.Filter($"members eq \"{ann}\"")}", connection.Token), "filtering on members is not supported");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Groups?startIndex=2&count=1", connection.Token), "the second page of one");
        t.Observe(await directory.GetAsync(group, connection.Token), "read Engineering");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users/{ann}?attributes=groups", connection.Token), "Ann's groups");

        t.Observe(
            await directory.PatchAsync(group, Scim.Patch(("add", "members", Scim.Members(bob))), connection.Token),
            "add Bob (Okta style)");
        t.Observe(await directory.GetAsync($"{group}?excludedAttributes=members", connection.Token), "read Engineering without its two members");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users/{bob}?attributes=groups,userName", connection.Token), "Bob's groups");
        t.Observe(
            await directory.PutAsync($"{group}?excludedAttributes=members", Scim.Group("Platform", "directory-engineering", bob, cy), connection.Token),
            "replace: rename to Platform with Bob and Cy, so Ann leaves and Cy joins");
        t.Observe(
            await directory.PatchAsync(group, Scim.Patch(("remove", $"members[value eq \"{bob}\"]", null)), connection.Token),
            "remove Bob with a filtered path (Entra style)");
        t.Observe(
            await directory.PatchAsync(group, Scim.Patch(("remove", "members", Scim.Members(cy))), connection.Token),
            "remove Cy by value");
        t.Observe(
            await directory.PatchAsync(group, Scim.Patch(("add", "members", Scim.Members(ann))), connection.Token),
            "add Ann back");
        t.Observe(
            await directory.PatchAsync($"{group}?attributes=displayName,members", Scim.Patch(("replace", null, new JsonObject { ["displayName"] = "Platform Team" })), connection.Token),
            "a pathless rename that asks for the resource back");
        t.Observe(
            await directory.PatchAsync(group, Scim.Patch(("replace", "members", Scim.Members(bob)), ("replace", "externalId", JsonValue.Create("directory-platform"))), connection.Token),
            "replace the member list and the externalId");
        t.Observe(
            await directory.PatchAsync(group, Scim.Patch(("remove", "members", null)), connection.Token),
            "remove every member");
        t.Observe(await directory.GetAsync(group, connection.Token), "the renamed, emptied group");

        t.Observe(await directory.DeleteAsync(group, connection.Token), "delete the group");
        t.Observe(await directory.GetAsync(group, connection.Token), "a deleted group is gone");
        t.Observe(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Platform Team", "directory-platform", ann), connection.Token), "recreating it with the same externalId");

        await t.ObserveStateAsync("/sqlos/admin/fga/api/user-groups", "dashboard: SCIM groups are FGA user groups");
        await t.ObserveAuditAsync("group and membership events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("GET /scim/v2/Groups/{id}")]
    [Covers("PUT /scim/v2/Groups/{id}")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    [Covers("DELETE /scim/v2/Groups/{id}")]
    public async Task Group_writes_that_break_scim_rules_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var ann = await ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        var gone = await ProvisionAsync(t, directory, connection, domain, "gone", "Gone", "Away");
        t.Discard(await directory.DeleteAsync($"{Scim.Root}/Users/{gone}", connection.Token));
        var id = t.Discard(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-engineering", ann), connection.Token)).JsonString("id");
        t.Scrub(id, "grp", "engineering");
        await t.SkipAuditAsync();
        var group = $"{Scim.Root}/Groups/{id}";
        var missing = $"{Scim.Root}/Groups/grp_ffffffffffffffffffffffffffffffff";
        t.Note("Ann exists; Gone was provisioned and then deleted; Engineering holds Ann.");
        // Not locked: a displayName that differs only in case is refused (409) on SQL Server, whose
        // default collation is case-insensitive, but creates a second group on PostgreSQL, because
        // the duplicate lookup compares in the database; one approval cannot hold both answers.

        Task<HttpExchange> Raw(string body)
            => directory.SendAsync(HttpMethod.Post, $"{Scim.Root}/Groups", new StringContent(body, Encoding.UTF8, "application/scim+json"), options => options.Bearer(connection.Token));

        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:Group\"]}"), "no displayName");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"displayName\": \"Design\"}"), "the User schema on a group");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:Group\"], \"displayName\": \"Design\", \"members\": [\"" + ann + "\"]}"), "members as strings");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:Group\"], \"displayName\": \"Design\", \"members\": \"" + ann + "\"}"), "members as a string");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:Group\"], \"displayName\": \"Design\", \"members\": [{\"display\": \"Ann\"}]}"), "a member without a value");
        t.Observe(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Design", "directory-design", "usr_ffffffffffffffffffffffffffffffff"), connection.Token), "a member this directory never provisioned");
        t.Observe(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Design", "directory-design", gone), connection.Token), "a member this directory deleted");
        t.Observe(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Design", "directory-engineering"), connection.Token), "an externalId another group holds");

        t.Observe(await directory.PatchAsync(group, Scim.Patch(("add", $"members[value eq \"{ann}\"]", Scim.Members(ann))), connection.Token), "adding through a filtered members path");
        t.Observe(await directory.PatchAsync(group, Scim.Patch(("remove", "displayName", null)), connection.Token), "removing displayName");
        t.Observe(await directory.PatchAsync(group, Scim.Patch(("replace", "description", JsonValue.Create("Builders"))), connection.Token), "an attribute groups do not have");
        t.Observe(await directory.PatchAsync(group, Scim.Patch(("replace", "id", JsonValue.Create("grp_ffffffffffffffffffffffffffffffff"))), connection.Token), "changing the group id");
        t.Observe(await directory.PatchAsync(group, Scim.Patch(("replace", "meta", new JsonObject { ["resourceType"] = "Group" })), connection.Token), "writing meta");
        t.Observe(await directory.PatchAsync(group, Scim.Patch(("add", "members", JsonValue.Create(ann))), connection.Token), "adding a member given as a string");

        t.Observe(await directory.GetAsync(missing, connection.Token), "reading an unknown group");
        t.Observe(await directory.PutAsync(missing, Scim.Group("Design"), connection.Token), "replacing an unknown group");
        t.Observe(await directory.PatchAsync(missing, Scim.Patch(("replace", "displayName", JsonValue.Create("Design"))), connection.Token), "patching an unknown group");
        t.Observe(await directory.DeleteAsync(missing, connection.Token), "deleting an unknown group");

        t.Observe(await directory.DeleteAsync(group, connection.Token), "delete Engineering");
        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-someone-else"), connection.Token),
            "the deleted displayName cannot be taken by a different externalId");
        t.Observe(await directory.GetAsync(group, connection.Token), "Engineering stays deleted");

        await t.ObserveAuditAsync("only the accepted writes are audited");
        await t.ApproveAsync();
    }

    /// <summary>Provisions a user at the organization's verified domain as a precondition and names its ID.</summary>
    internal static async Task<string> ProvisionAsync(Transcript t, HttpActor directory, ScenarioScimConnection connection, string domain, string name, string givenName, string familyName)
    {
        var email = t.Unique.Email(name, domain);
        var created = t.Discard(await directory.PostAsync(
            $"{Scim.Root}/Users",
            Scim.User(email, $"directory-{name}", givenName, familyName, email),
            connection.Token));
        EnterpriseSetup.EnsureSucceeded(created);
        var id = created.JsonString("id");
        t.Scrub(id, "usr", name);
        await t.SkipAuditAsync();
        return id;
    }
}
