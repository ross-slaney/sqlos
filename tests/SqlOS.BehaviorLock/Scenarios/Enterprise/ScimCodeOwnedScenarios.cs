using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Database;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// A SCIM connection declared in code (<c>AuthServer.SeedScimConnection</c>) is reconciled at
/// startup and owned by source control: the admin API shows it and can disable or re-enable it in
/// an emergency, but refuses to rotate its token or edit it or its mappings.
/// </summary>
[TestClass]
public sealed class ScimCodeOwnedScenarios
{
    private const string Token = "scim_code-owned-directory-token-0123456789";

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/scim-connections")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/token/rotate")]
    [Covers("PUT /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/enable")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    [Covers("PUT /sqlos/admin/auth/api/scim-mappings/{mappingId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-mappings/{mappingId}/disable")]
    [Covers("POST /scim/v2/Users")]
    [Covers("POST /scim/v2/Groups")]
    public async Task A_code_owned_connection_can_be_disabled_but_not_edited_through_the_admin_api()
    {
        var database = await BehaviorLockDatabase.CreateDatabaseAsync("enterprise-seeded");
        // Both hosts must read the signing key the first one created, as instances of one deployment do.
        var keys = Directory.CreateTempSubdirectory("behavior-lock-enterprise-keys-");
        try
        {
            // The seed resolves its organization by slug at startup, so a first host on the same
            // database creates the organization, its verified domain, and its FGA resources.
            string organizationId, slug, domain;
            await using (var first = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath, options =>
            {
                options.ExistingDatabase = database;
                options.DataProtectionKeysDirectory = keys.FullName;
            }))
            {
                var created = await first.Setup.CreateOrganizationAsync("acme");
                organizationId = created.Id;
                slug = created.Slug;
                domain = await first.VerifyDomainAsync(created, "acme");
                await first.CreateWorkspaceResourceAsync("workspace::acme", "Acme");
                await first.CreateWorkspaceResourceAsync("workspace::acme-projects", "Acme projects", "workspace::acme");
            }

            await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath, options =>
            {
                options.ExistingDatabase = database;
                options.DataProtectionKeysDirectory = keys.FullName;
                options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedScimConnection("acme-directory", seed =>
                {
                    seed.OrganizationSlug = slug;
                    seed.DisplayName = "Acme directory (code)";
                    seed.Token = Token;
                    seed.GrantBoundaryResourceId = "workspace::acme";
                    seed.MapGroup("Engineering", mapping =>
                    {
                        mapping.RoleKey = BehaviorLockAuthorization.ReaderRole;
                        mapping.ResourceId = "workspace::acme-projects";
                    });
                });
            });
            t.Scrub(slug, "slug", "acme");
            t.Scrub(domain, "domain", "acme");
            t.ScrubScimToken(Token);
            t.Note("A first host created Acme, verified its domain, and created its FGA resources; this host declares Acme's SCIM connection in code.");
            var connections = $"/sqlos/admin/auth/api/organizations/{organizationId}/scim-connections";

            var list = t.Observe(await t.Operator.GetAsync(connections), "the code-owned connection, reconciled at startup");
            var connectionId = list.JsonString("data.0.id");
            var connection = $"/sqlos/admin/auth/api/scim-connections/{connectionId}";
            t.Observe(await t.Operator.GetAsync(connection), "its detail: owned by code");
            t.Observe(await t.Operator.PostJsonAsync($"{connection}/token/rotate", new { }), "rotating its token is refused");
            t.Observe(await t.Operator.PutJsonAsync(connection, new { displayName = "Renamed", enabled = true }), "editing it is refused");
            var mappings = t.Observe(await t.Operator.GetAsync($"{connection}/mappings"), "its code-owned mapping");
            var mappingId = mappings.JsonString("data.0.id");
            t.Observe(
                await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{mappingId}", new
                {
                    matchType = "display_name",
                    groupDisplayName = "Engineering",
                    roleKey = BehaviorLockAuthorization.AdminRole,
                    resourceId = "workspace::acme-projects",
                    enabled = true
                }),
                "editing the mapping is refused");
            t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{mappingId}/disable", new { }), "so is disabling the mapping");

            var directory = t.NewClient("directory");
            var ann = t.Unique.Email("ann", domain);
            var annId = t.Observe(
                await directory.PostAsync($"{Scim.Root}/Users", Scim.User(ann, "directory-ann", "Ann", "Archer", ann), Token),
                "the directory authenticates with the token from configuration").JsonString("id");
            t.Scrub(annId, "usr", "ann");
            t.Observe(
                await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-engineering", annId), Token),
                "the code-owned mapping grants on the pushed group");
            t.Observe(await t.Operator.PostJsonAsync($"{connection}/disable", new { }), "the emergency disable is allowed and revokes the grant");
            t.Observe(await directory.GetAsync($"{Scim.Root}/ServiceProviderConfig", Token), "the disabled connection refuses its token");
            t.Observe(await t.Operator.PostJsonAsync($"{connection}/enable", new { }), "and it can be re-enabled");

            await t.ObserveAuditAsync("administration and provisioning events");
            await t.ApproveAsync();
        }
        finally
        {
            await BehaviorLockDatabase.DropDatabaseAsync(database);
            keys.Delete(recursive: true);
        }
    }
}
