using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Who a directory may create, link, and change (#420, #423): email writes stay inside the
/// organization's verified domains, an unverified account is never linked, an existing verified
/// account is linked without handing its lifecycle to the directory, and ownership ends when the
/// person gains an anchor outside the directory.
/// </summary>
[TestClass]
public sealed class ScimOwnershipScenarios
{
    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("PATCH /scim/v2/Users/{id}")]
    [Covers("PUT /scim/v2/Users/{id}")]
    [Covers("GET /scim/v2/Users/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    public async Task A_directory_cannot_write_an_email_outside_the_organizations_verified_domains()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var victim = t.Unique.Email("victim");
        var judy = t.Unique.Email("judy", domain);

        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(victim, "directory-victim", "Vic", "Tim", victim), connection.Token),
            "provisioning an address at a domain Acme has not verified is rejected");
        var id = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), connection.Token),
            "provisioning at Acme's verified domain works").JsonString("id");
        t.Scrub(id, "usr", "judy");
        var user = $"{Scim.Root}/Users/{id}";
        t.Observe(
            await directory.PatchAsync(user, Scim.Patch(("replace", "emails[type eq \"work\"].value", JsonValue.Create(victim))), connection.Token),
            "moving Judy's work email outside the domain is rejected");
        t.Observe(
            await directory.PutAsync(user, Scim.User(judy, "directory-judy", "Judy", "Hopps", victim), connection.Token),
            "so is a replace that carries the outside address");
        t.Observe(await directory.GetAsync(user, connection.Token), "Judy keeps her verified address");
        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(victim, "directory-no-email", "No", "Email"), connection.Token),
            "a user with an email-shaped userName but no emails is created without any address");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}", "dashboard: Judy's global account");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/sync-events?pageSize=50", "dashboard: rejected writes are recorded as failed sync events");
        await t.ObserveAuditAsync("only the accepted writes are audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task A_directory_never_links_an_account_whose_email_is_unverified()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var ceo = t.Unique.Email("ceo", domain);
        var squatter = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new
        {
            displayName = "Squatter",
            email = ceo,
            password = t.Unique.Password("squatter")
        });
        var squatterId = squatter.JsonString("id");
        t.Scrub(squatterId, "usr", "squatter");
        t.Note("Before the directory syncs, someone registered an account for the CEO's address and never verified it.");

        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(ceo, "directory-ceo", "Cara", "Ceo", ceo), connection.Token),
            "the directory's user matches the unverified account by email: refused");
        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(ceo, "directory-ceo"), connection.Token),
            "matching through userName alone is refused the same way");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{squatterId}/memberships", "dashboard: the unverified account gained no membership");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/sync-events", "dashboard: both refusals recorded");
        await t.ObserveAuditAsync("refusals write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("PATCH /scim/v2/Users/{id}")]
    [Covers("GET /scim/v2/Users/{id}")]
    [Covers("DELETE /scim/v2/Users/{id}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task A_second_directory_links_an_existing_verified_account_without_owning_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var globexDomain = await t.VerifyDomainAsync(globex, "globex");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var acmeDomain = await t.VerifyDomainAsync(acme, "acme");
        var globexDirectory = await t.CreateDirectoryAsync(globex, "Globex directory");
        var acmeDirectory = await t.CreateDirectoryAsync(acme, "Acme directory");
        var globexClient = t.NewClient("globex-directory");
        var acmeClient = t.NewClient("acme-directory");
        var bob = t.Unique.Email("bob", globexDomain);
        var bobAtAcme = t.Unique.Email("bob.contractor", acmeDomain);

        var id = t.Observe(
            await globexClient.PostAsync($"{Scim.Root}/Users", Scim.User(bob, "globex-bob", "Bob", "Builder", bob), globexDirectory.Token),
            "Globex's directory creates Bob and owns his account").JsonString("id");
        t.Scrub(id, "usr", "bob");
        var user = $"{Scim.Root}/Users/{id}";

        t.Observe(
            await acmeClient.PostAsync($"{Scim.Root}/Users", Scim.User(bob, "acme-contractor-bob", "Robert", "Builder", bob), acmeDirectory.Token),
            "Acme's directory provisions the same address: it links the existing account");
        t.Observe(
            await acmeClient.PatchAsync(user, Scim.Patch(
                ("replace", "displayName", JsonValue.Create("Robert (contractor)")),
                ("replace", "emails[type eq \"work\"].value", JsonValue.Create(bobAtAcme))), acmeDirectory.Token),
            "Acme renames Bob and moves his work email: only Acme's view changes");
        t.Observe(
            await acmeClient.PatchAsync(user, Scim.Patch(("replace", "active", JsonValue.Create(false))), acmeDirectory.Token),
            "Acme deactivates Bob");
        t.Observe(await acmeClient.GetAsync(user, acmeDirectory.Token), "Acme's view of Bob");
        t.Observe(await globexClient.GetAsync(user, globexDirectory.Token), "Globex's view of Bob is untouched");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}", "dashboard: Bob's global account keeps Globex's name, email, and active state");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}/memberships", "dashboard: Bob's memberships");

        t.Observe(await globexClient.DeleteAsync(user, globexDirectory.Token), "Globex, the owning directory, deletes Bob");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}", "dashboard: with no other active membership, Bob is deactivated globally");
        t.Observe(
            await acmeClient.PatchAsync(user, Scim.Patch(("replace", "active", JsonValue.Create(true))), acmeDirectory.Token),
            "Acme reactivates Bob: its own membership returns");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}", "dashboard: Acme's link does not own Bob, so it cannot reactivate his account");

        await t.ObserveAuditAsync("provisioning events from both directories");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("PATCH /scim/v2/Users/{id}")]
    [Covers("DELETE /scim/v2/Users/{id}")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task Directory_ownership_ends_when_the_person_joins_another_organization()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var judy = t.Unique.Email("judy", domain);

        var created = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), connection.Token),
            "Acme's directory creates Judy and owns her account");
        var id = created.JsonString("id");
        t.Scrub(id, "usr", "judy");
        var user = $"{Scim.Root}/Users/{id}";
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}/memberships", new { userId = id, role = "member" }),
            "an operator adds Judy to Globex: an anchor outside Acme's directory");

        t.Observe(
            await directory.PatchAsync(user, Scim.Patch(("replace", "displayName", JsonValue.Create("Judy (Acme)"))), connection.Token),
            "Acme's next write releases its ownership and changes only Acme's view");
        t.Observe(
            await directory.PatchAsync(user, Scim.Patch(("replace", "active", JsonValue.Create(false))), connection.Token),
            "Acme deactivates Judy: only the Acme membership ends");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}", "dashboard: Judy stays active under her original name");
        t.Observe(await directory.DeleteAsync(user, connection.Token), "Acme deletes Judy");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{id}/memberships", "dashboard: the Globex membership survives");

        await t.ObserveAuditAsync("the lifecycle release is audited");
        await t.ApproveAsync();
    }
}
