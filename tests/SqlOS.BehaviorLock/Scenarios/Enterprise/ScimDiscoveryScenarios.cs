using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// SCIM service discovery (RFC 7644 section 4) and the bearer-token gate every SCIM route shares,
/// served at <c>/scim/v2</c> outside the dashboard prefix.
/// </summary>
[TestClass]
public sealed class ScimDiscoveryScenarios
{
    [Scenario]
    [Covers("GET /scim/v2/ServiceProviderConfig")]
    [Covers("GET /scim/v2/ResourceTypes")]
    [Covers("GET /scim/v2/ResourceTypes/{id}")]
    [Covers("GET /scim/v2/Schemas")]
    [Covers("GET /scim/v2/Schemas/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    public async Task A_directory_reads_the_service_provider_configuration_resource_types_and_schemas()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");

        t.Observe(await directory.GetAsync($"{Scim.Root}/ServiceProviderConfig", connection.Token), "the service provider configuration");
        t.Observe(await directory.GetAsync($"{Scim.Root}/ResourceTypes", connection.Token), "the resource types");
        t.Observe(await directory.GetAsync($"{Scim.Root}/ResourceTypes/User", connection.Token), "the User resource type");
        t.Observe(await directory.GetAsync($"{Scim.Root}/ResourceTypes/group", connection.Token), "resource type IDs match case-insensitively");
        t.Observe(await directory.GetAsync($"{Scim.Root}/ResourceTypes/Device", connection.Token), "an unknown resource type");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Schemas", connection.Token), "the schemas");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Schemas/{Scim.UserSchema}", connection.Token), "the core User schema");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Schemas/{Scim.GroupSchema}", connection.Token), "the core Group schema");
        t.Observe(
            await directory.GetAsync($"{Scim.Root}/Schemas/urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", connection.Token),
            "the enterprise user extension is not offered");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}", "dashboard: the connection records when its token was last used");
        await t.ObserveAuditAsync("discovery writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /scim/v2/ServiceProviderConfig")]
    [Covers("GET /scim/v2/Users")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/token/rotate")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/disable")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    public async Task Requests_without_a_live_connection_bearer_token_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var acmeDirectory = await t.CreateDirectoryAsync(acme);
        var globexDirectory = await t.CreateDirectoryAsync(globex);
        var directory = t.NewClient("directory");
        var config = $"{Scim.Root}/ServiceProviderConfig";

        t.Observe(await directory.GetAsync(config), "no Authorization header");
        t.Observe(
            await directory.GetAsync(config, options => options.Header("Authorization", "Basic ZGlyZWN0b3J5OnNlY3JldA==")),
            "a Basic credential instead of a bearer token");
        t.Observe(await directory.GetAsync(config, "scim_not-a-token-this-host-issued"), "a token this host never issued");

        var rotated = t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-connections/{acmeDirectory.Id}/token/rotate", new { }),
            "the operator rotates Acme's token");
        var newToken = rotated.JsonString("token");
        t.ScrubScimToken(newToken);
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users", acmeDirectory.Token), "the token issued before the rotation");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users", newToken), "the rotated token");

        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-connections/{acmeDirectory.Id}/disable", new { }),
            "the operator disables Acme's connection");
        t.Observe(await directory.GetAsync(config, newToken), "a disabled connection's token");

        t.Observe(await directory.GetAsync(config, globexDirectory.Token), "Globex's token while Globex is active");
        t.Observe(
            await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}", new { name = globex.Name, slug = globex.Slug, isActive = false }),
            "the operator deactivates Globex");
        t.Observe(await directory.GetAsync(config, globexDirectory.Token), "a deactivated organization's token");

        await t.ObserveAuditAsync("token rotation, disablement, and deactivation events");
        await t.ApproveAsync();
    }
}
