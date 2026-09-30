using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

/// <summary>
/// FGA user groups as the FGA dashboard and the library checks see them. SqlOS creates groups
/// from SCIM (there is no dashboard or probe route that creates one), so the directory is the
/// arrangement here.
/// </summary>
[TestClass]
public sealed class FgaGroupScenarios
{
    private const string Api = "/sqlos/admin/fga/api";

    /// <summary>
    /// Known defect #448 (7.2.1): SCIM gives a provisioned user a second FGA subject with a
    /// generated ID (its <c>externalRef</c> is the SqlOS user ID) and puts that subject in the SCIM
    /// group. A grant to the group reaches the generated subject, but a check or list filter by the
    /// SqlOS user ID, as the FGA guides write it, finds no subject at all.
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/user-groups")]
    [Covers("GET /sqlos/admin/fga/api/subjects")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}")]
    [Covers("POST /sqlos/admin/fga/api/grants")]
    [Covers("POST /sqlos/admin/fga/api/trace")]
    [Covers("POST /__probe/fga/check")]
    [Covers("POST /__probe/fga/filter")]
    public async Task Scim_group_grants_reach_only_the_scim_subject_CurrentBehavior_KnownDefect_448()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        var directory = await t.Setup.CreateScimConnectionAsync(acme);
        var scim = t.NewClient("scim");
        var email = t.Unique.Email("judy", domain);
        var user = await ProvisionAsync(t, scim, directory, "/scim/v2/Users", $$"""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
              "externalId": "directory-judy",
              "userName": "{{email}}",
              "name": { "givenName": "Judy", "familyName": "Hopps" },
              "emails": [{ "value": "{{email}}", "primary": true, "type": "work" }],
              "active": true
            }
            """);
        var userId = user.JsonString("id");
        await ProvisionAsync(t, scim, directory, "/scim/v2/Groups", $$"""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"],
              "externalId": "directory-support",
              "displayName": "Support",
              "members": [{ "value": "{{userId}}" }]
            }
            """);
        await t.SkipAuditAsync();
        var fga = new FgaFixture(t);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var op = t.Operator;
        var probe = t.NewClient("probe");
        var read = BehaviorLockAuthorization.ReadPermission;
        t.Note("Setup: a SCIM directory provisioned Judy and a Support group with Judy as its member (not recorded).");

        var groups = t.Observe(await op.GetAsync($"{Api}/user-groups"), "the SCIM group is an FGA user group with one member");
        var groupSubjectId = groups.JsonString("data.0.subjectId");
        var subjects = t.Observe(
            await op.GetAsync($"{Api}/subjects?type=user"),
            "SCIM created a user subject of its own, with the SqlOS user ID as its external reference");
        var scimSubjectId = subjects.JsonString("data.0.id");
        t.Observe(await op.GetAsync($"{Api}/subjects/{groupSubjectId}"), "the group subject lists its member");
        t.Observe(await op.GetAsync($"{Api}/subjects/{scimSubjectId}"), "the member subject lists its group");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = groupSubjectId, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = alpha }),
            "grant the group read on Alpha");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = scimSubjectId, permissionKey = read, resourceId = alpha }),
            "the SCIM subject reads Alpha through the group");
        t.Observe(
            await op.PostJsonAsync($"{Api}/trace", new { subjectId = scimSubjectId, resourceId = alpha, permissionKey = read }),
            "the trace names the group that granted it");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = userId, permissionKey = read, resourceId = alpha }),
            "a check by the SqlOS user ID finds no subject");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/filter", new { subjectId = userId, permissionKey = read }),
            "so the list filter by user ID is empty");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/filter", new { subjectId = scimSubjectId, permissionKey = read }),
            "while the SCIM subject's list includes Alpha");

        await t.ObserveAuditAsync("the dashboard grant is not audited");
        await t.ApproveAsync();
    }

    private static async Task<HttpExchange> ProvisionAsync(Transcript t, HttpActor scim, ScenarioScimConnection directory, string path, string json)
    {
        var exchange = t.Discard(await scim.SendAsync(
            HttpMethod.Post,
            path,
            new StringContent(json, Encoding.UTF8, "application/scim+json"),
            options => options.Bearer(directory.Token)));
        if (exchange.StatusCode != 201)
        {
            throw new InvalidOperationException($"SCIM setup failed: {exchange.Describe()} {exchange.ResponseBody}");
        }

        return exchange;
    }
}
