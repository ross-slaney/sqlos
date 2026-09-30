using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Scim;

[TestClass]
public sealed class ScimUserScenarios
{
    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("GET /scim/v2/Users/{id}")]
    public async Task A_directory_provisions_a_user_at_a_verified_domain()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.Setup.VerifyDomainAsync(acme, "acme");
        var directory = await t.Setup.CreateScimConnectionAsync(acme);
        var scim = t.NewClient("scim");
        var email = t.Unique.Email("judy", domain);
        var user = $$"""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
              "externalId": "directory-judy",
              "userName": "{{email}}",
              "name": { "givenName": "Judy", "familyName": "Hopps" },
              "emails": [{ "value": "{{email}}", "primary": true, "type": "work" }],
              "active": true
            }
            """;

        t.Observe(
            await scim.SendAsync(HttpMethod.Post, "/scim/v2/Users", ScimJson(user)),
            "without the connection's bearer token the request is refused");
        var created = t.Observe(
            await scim.SendAsync(HttpMethod.Post, "/scim/v2/Users", ScimJson(user), options => options.Bearer(directory.Token)),
            "create the user");
        t.Observe(
            await scim.GetAsync($"/scim/v2/Users/{created.JsonString("id")}", options => options.Bearer(directory.Token)),
            "read the provisioned user");

        await t.ObserveAuditAsync("provisioning events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Known defect (7.2.1): the documented default SCIM Base URL sits under the dashboard prefix,
    /// and the dashboard middleware answers every unlisted path under it, so a directory client
    /// that is not a dashboard operator never reaches SCIM (404 here, 401 in Password mode).
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/scim/v2/Users")]
    public async Task A_directory_client_cannot_reach_scim_under_the_dashboard_prefix()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var directory = await t.Setup.CreateScimConnectionAsync(acme);
        var scim = t.NewClient("scim");

        t.Observe(
            await scim.SendAsync(
                HttpMethod.Post,
                "/sqlos/scim/v2/Users",
                ScimJson("""{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"blocked@example.test","active":true}"""),
                options => options.Bearer(directory.Token)),
            "a valid directory token still gets the dashboard's 404");

        await t.ApproveAsync();
    }

    private static StringContent ScimJson(string json)
        => new(json, Encoding.UTF8, "application/scim+json");
}
