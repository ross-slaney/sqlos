using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Known defect #447 (7.2.1): SCIM at its documented default base path, <c>/sqlos/scim/v2</c>, sits
/// under the dashboard prefix. The dashboard middleware answers every path under <c>/sqlos</c> that
/// is not on its pass-through list before routing, so a directory that is not also a dashboard
/// operator never reaches a SCIM endpoint. The fix passes the SCIM base path through; these
/// approvals then change with a ledger entry.
/// </summary>
[TestClass]
public sealed class ScimDashboardPrefixScenarios
{
    private const string UnknownUserId = "directory-user-id";
    private const string UnknownGroupId = "directory-group-id";

    [Scenario]
    [Covers("GET /sqlos/scim/v2/ServiceProviderConfig")]
    [Covers("GET /sqlos/scim/v2/ResourceTypes")]
    [Covers("GET /sqlos/scim/v2/ResourceTypes/{id}")]
    [Covers("GET /sqlos/scim/v2/Schemas")]
    [Covers("GET /sqlos/scim/v2/Schemas/{id}")]
    [Covers("GET /sqlos/scim/v2/Users")]
    [Covers("POST /sqlos/scim/v2/Users")]
    [Covers("GET /sqlos/scim/v2/Users/{id}")]
    [Covers("PUT /sqlos/scim/v2/Users/{id}")]
    [Covers("PATCH /sqlos/scim/v2/Users/{id}")]
    [Covers("DELETE /sqlos/scim/v2/Users/{id}")]
    [Covers("GET /sqlos/scim/v2/Groups")]
    [Covers("POST /sqlos/scim/v2/Groups")]
    [Covers("GET /sqlos/scim/v2/Groups/{id}")]
    [Covers("PUT /sqlos/scim/v2/Groups/{id}")]
    [Covers("PATCH /sqlos/scim/v2/Groups/{id}")]
    [Covers("DELETE /sqlos/scim/v2/Groups/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    public async Task Every_scim_route_at_the_default_base_path_answers_the_dashboard_404_CurrentBehavior_KnownDefect_447()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var email = t.Unique.Email("judy", domain);
        var user = Scim.User(email, "directory-judy", "Judy", "Hopps", email);
        var group = Scim.Group("Engineering", "directory-engineering");
        var rename = Scim.Patch(("replace", "displayName", JsonValue.Create("Judy H.")));
        t.Note("Every request carries the connection's valid bearer token; none of them reaches SCIM.");

        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/ServiceProviderConfig", connection.Token), "service provider configuration");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/ResourceTypes", connection.Token), "resource types");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/ResourceTypes/User", connection.Token), "the User resource type");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/Schemas", connection.Token), "schemas");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/Schemas/{Scim.UserSchema}", connection.Token), "the core User schema");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/Users", connection.Token), "list users");
        t.Observe(await directory.PostAsync($"{Scim.DefaultRoot}/Users", user, connection.Token), "create a user");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/Users/{UnknownUserId}", connection.Token), "read a user");
        t.Observe(await directory.PutAsync($"{Scim.DefaultRoot}/Users/{UnknownUserId}", user, connection.Token), "replace a user");
        t.Observe(await directory.PatchAsync($"{Scim.DefaultRoot}/Users/{UnknownUserId}", rename, connection.Token), "patch a user");
        t.Observe(await directory.DeleteAsync($"{Scim.DefaultRoot}/Users/{UnknownUserId}", connection.Token), "delete a user");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/Groups", connection.Token), "list groups");
        t.Observe(await directory.PostAsync($"{Scim.DefaultRoot}/Groups", group, connection.Token), "create a group");
        t.Observe(await directory.GetAsync($"{Scim.DefaultRoot}/Groups/{UnknownGroupId}", connection.Token), "read a group");
        t.Observe(await directory.PutAsync($"{Scim.DefaultRoot}/Groups/{UnknownGroupId}", group, connection.Token), "replace a group");
        t.Observe(
            await directory.PatchAsync($"{Scim.DefaultRoot}/Groups/{UnknownGroupId}", Scim.Patch(("replace", "displayName", JsonValue.Create("Platform"))), connection.Token),
            "patch a group");
        t.Observe(await directory.DeleteAsync($"{Scim.DefaultRoot}/Groups/{UnknownGroupId}", connection.Token), "delete a group");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/sync-events", "dashboard: no directory traffic was recorded");
        await t.ObserveAuditAsync("nothing reached SCIM, so nothing was audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/scim/v2/ServiceProviderConfig")]
    [Covers("POST /sqlos/scim/v2/Users")]
    [Covers("GET /sqlos/scim/v2/Users/{id}")]
    public async Task Only_a_caller_that_is_also_a_dashboard_operator_reaches_scim_at_the_default_base_path_CurrentBehavior_KnownDefect_447()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var email = t.Unique.Email("judy", domain);
        t.Note("The operator actor carries the dashboard's AuthorizationCallback header; a directory never would.");

        t.Observe(
            await t.Operator.GetAsync($"{Scim.DefaultRoot}/ServiceProviderConfig"),
            "with the operator header but no SCIM token, the request reaches SCIM, which demands its own token");
        var created = t.Observe(
            await t.Operator.SendAsync(
                HttpMethod.Post,
                $"{Scim.DefaultRoot}/Users",
                Scim.Json(Scim.User(email, "directory-judy", "Judy", "Hopps", email)),
                options => options.Bearer(connection.Token)),
            "with both the operator header and the SCIM token, provisioning works");
        t.Observe(
            await t.Operator.GetAsync($"{Scim.DefaultRoot}/Users/{created.JsonString("id")}", options => options.Bearer(connection.Token)),
            "the provisioned user reads back under the default base path");
        t.Observe(
            await t.NewClient("directory").GetAsync($"{Scim.DefaultRoot}/Users/{created.JsonString("id")}", connection.Token),
            "the same read from the directory alone gets the dashboard 404");

        await t.ObserveAuditAsync("provisioning events");
        await t.ApproveAsync();
    }
}
