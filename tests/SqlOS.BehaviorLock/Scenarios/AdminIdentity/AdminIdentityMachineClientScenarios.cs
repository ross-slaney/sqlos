using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Machine clients (client-credentials service accounts): creating them with FGA grants, testing
/// and rotating their secret, adding and removing grants, the emergency switches, revocation, and
/// the code-owned seed that only source control may change. The token endpoint's
/// <c>client_credentials</c> grant shows what each change does at runtime.
/// </summary>
[TestClass]
public sealed class AdminIdentityMachineClientScenarios
{
    private const string Audience = BehaviorLockConstants.ResourceApiAudience;

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/machine-clients")]
    [Covers("GET /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/validate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/rotate")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /__probe/fga/check")]
    public async Task Operator_creates_tests_and_rotates_a_machine_client()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var probe = t.NewClient("probe");
        t.Discard(await probe.PostJsonAsync("/__probe/fga/workspaces", new { name = "Alpha", id = "alpha" }));
        var organization = await t.Setup.CreateOrganizationAsync("acme");
        var machines = AdminIdentity.Api + "/machine-clients";

        var created = t.Observe(
            await t.Operator.PostJsonAsync(machines, new
            {
                clientId = "  reporting-worker  ",
                displayName = "Reporting Worker",
                description = "  Nightly reports  ",
                audience = Audience,
                scopes = new[] { BehaviorLockAuthorization.ReadPermission, BehaviorLockAuthorization.ReadPermission, " " },
                organizationId = organization.Id,
                grants = new[] { new { resourceId = "workspace::alpha", roleId = BehaviorLockAuthorization.ReaderRole, description = "Read alpha" } }
            }),
            "create a machine client with a grant; the secret is shown once");
        var secret = created.JsonString("clientSecret");
        await t.ObserveAuditAsync("creation is audited");
        t.Observe(await t.Operator.GetAsync(machines), "machine clients by client ID, the code-owned seed included");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/reporting-worker/validate", new { clientSecret = secret, resource = Audience, scopes = new[] { BehaviorLockAuthorization.ReadPermission } }),
            "the secret, audience, and scope test as ready");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/reporting-worker/validate", new { clientSecret = secret + "x", resource = Audience, scopes = Array.Empty<string>() }),
            "a wrong secret tests as invalid");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/reporting-worker/validate", new { clientSecret = secret, resource = "https://other.example.test", scopes = Array.Empty<string>() }),
            "so does another audience");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/reporting-worker/validate", new { clientSecret = secret, resource = Audience, scopes = new[] { BehaviorLockAuthorization.WritePermission } }),
            "and a scope the client was not given");
        await t.ObserveAuditAsync("each test is audited without the secret");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", ClientCredentials(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("reporting-worker", secret))),
            "the client-credentials grant issues a token for the audience");
        var subjectId = t.Discard(await t.Operator.GetAsync("/sqlos/admin/fga/api/service-accounts"))
            .Json!["data"]!.AsArray().First(item => item!["clientId"]!.GetValue<string>() == "reporting-worker")!["subjectId"]!.GetValue<string>();
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId, permissionKey = BehaviorLockAuthorization.ReadPermission, resourceId = "workspace::alpha" }),
            "the grant lets the machine client's subject read alpha");

        var rotated = t.Observe(await t.Operator.PostJsonAsync(machines + "/reporting-worker/rotate", new { }), "rotate the secret");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", ClientCredentials(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("reporting-worker", secret))),
            "the old secret is refused");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", ClientCredentials(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("reporting-worker", rotated.JsonString("clientSecret")))),
            "the new secret is accepted");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/clients/reporting-worker/credentials"), "rotation revoked the old credential and issued a new one");
        await t.ObserveAuditAsync("token issuance and rotation");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/grants")]
    [Covers("DELETE /sqlos/admin/auth/api/machine-clients/{clientId}/grants/{grantId}")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/rotate")]
    [Covers("GET /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}/grants")]
    public async Task Operator_changes_grants_and_switches_a_machine_client_off()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var probe = t.NewClient("probe");
        t.Discard(await probe.PostJsonAsync("/__probe/fga/workspaces", new { name = "Alpha", id = "alpha" }));
        t.Discard(await probe.PostJsonAsync("/__probe/fga/workspaces", new { name = "Beta", id = "beta" }));
        var machines = AdminIdentity.Api + "/machine-clients";
        var created = await t.Setup.OperatorPostAsync(machines, new
        {
            clientId = "sync-worker",
            displayName = "Sync Worker",
            audience = Audience,
            scopes = new[] { BehaviorLockAuthorization.ReadPermission },
            grants = Array.Empty<object>()
        });
        var secret = created.JsonString("clientSecret");
        var subjectId = t.Discard(await t.Operator.GetAsync("/sqlos/admin/fga/api/service-accounts"))
            .Json!["data"]!.AsArray().First(item => item!["clientId"]!.GetValue<string>() == "sync-worker")!["subjectId"]!.GetValue<string>();
        Task<HttpExchange> Token() => t.Api.PostFormAsync("/sqlos/auth/token", ClientCredentials(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("sync-worker", secret)));

        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/sync-worker/grants", new { resourceId = "workspace::beta", roleId = BehaviorLockAuthorization.AdminRole, description = "Administer beta" }),
            "grant the admin role on beta");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/sync-worker/grants", new { resourceId = "workspace::beta", roleId = BehaviorLockAuthorization.AdminRole, description = "again" }),
            "the same grant again is a silent no-op");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/sync-worker/grants", new { resourceId = "workspace::missing", roleId = BehaviorLockAuthorization.AdminRole }),
            "a grant on a resource that does not exist is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(machines + "/sync-worker/grants", new { resourceId = "workspace::beta", roleId = "workspace_owner" }),
            "a role that does not exist is refused");
        var grants = t.Observe(await t.Operator.GetAsync($"/sqlos/admin/fga/api/subjects/{subjectId}/grants"), "the machine client's subject has one grant");
        var grantId = grants.JsonString("data.0.id");
        t.Observe(await t.Operator.DeleteAsync($"{machines}/sync-worker/grants/{grantId}"), "remove it");
        t.Observe(await t.Operator.DeleteAsync($"{machines}/sync-worker/grants/{grantId}"), "removing it again is refused");
        await t.ObserveAuditAsync("grant changes");

        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/emergency-enable", new { }), "emergency enable of an active client is a no-op");
        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/emergency-disable", new { }), "emergency disable");
        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/emergency-disable", new { }), "a second emergency disable changes nothing");
        t.Observe(await Token(), "the disabled client gets no token");
        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/emergency-enable", new { }), "emergency enable");
        t.Observe(await Token(), "tokens are issued again");
        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/revoke", new { }), "revoke the machine client");
        t.Observe(await Token(), "the revoked client gets no token");
        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/emergency-enable", new { }), "emergency enable cannot restore a revoked client");
        t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/emergency-disable", new { }), "emergency disable leaves the revocation in place");
        var rotated = t.Observe(await t.Operator.PostJsonAsync(machines + "/sync-worker/rotate", new { }), "a revoked client can still be rotated");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", ClientCredentials(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("sync-worker", rotated.JsonString("clientSecret")))),
            "but its new secret gets no token either");
        t.Observe(await t.Operator.GetAsync(machines), "the revoked client in the list");
        await t.ObserveAuditAsync("switches and revocation");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/rotate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/validate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/grants")]
    [Covers("DELETE /sqlos/admin/auth/api/machine-clients/{clientId}/grants/{grantId}")]
    public async Task Machine_client_requests_are_validated()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp, options => options.AnswerUnhandledExceptionsAsServerErrors = true);
        var machines = AdminIdentity.Api + "/machine-clients";
        object Create(string clientId, string? displayName = "Worker", string? audience = Audience, string[]? scopes = null, string? organizationId = null, object[]? grants = null)
            => new { clientId, displayName, audience, scopes = scopes ?? [BehaviorLockAuthorization.ReadPermission], organizationId, grants = grants ?? [] };

        t.Observe(await t.Operator.PostJsonAsync(machines, Create("  ")), "a client ID is required");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create("worker", displayName: null)), "a display name is required");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create("worker", audience: " ")), "an audience is required");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create(new string('w', 201))), "the client ID is limited to two hundred characters");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create("worker", scopes: [" "])), "at least one scope is required");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create("worker", organizationId: "org_missing")), "an unknown organization is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(machines, Create("worker", grants: [new { resourceId = "workspace::missing", roleId = BehaviorLockAuthorization.ReaderRole }])),
            "a grant on a missing resource is refused");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create("atlas-portal")), "an existing OAuth client ID is refused");
        t.Observe(await t.Operator.PostJsonAsync(machines, Create("atlas-worker")), "and so is the seeded machine client's");
        t.Observe(
            await t.Operator.PostJsonAsync(machines, new { clientId = "worker", displayName = "Worker", audience = Audience, scopes = new[] { BehaviorLockAuthorization.ReadPermission } }),
            "a request without a grants array escapes as a 500");
        t.Observe(
            await t.Operator.PostJsonAsync(machines, new { clientId = "worker", displayName = "Worker", audience = Audience, grants = Array.Empty<object>() }),
            "and one without a scopes array too");
        foreach (var action in new[] { "rotate", "revoke", "emergency-disable", "emergency-enable" })
        {
            t.Observe(await t.Operator.PostJsonAsync($"{machines}/missing-machine/{action}", new { }), $"{action} on an unknown machine client is refused");
        }

        t.Observe(
            await t.Operator.PostJsonAsync($"{machines}/missing-machine/validate", new { clientSecret = "x", resource = Audience, scopes = Array.Empty<string>() }),
            "so is a test of its secret");
        t.Observe(
            await t.Operator.PostJsonAsync($"{machines}/missing-machine/grants", new { resourceId = "workspace::alpha", roleId = BehaviorLockAuthorization.ReaderRole }),
            "and a grant");
        t.Observe(await t.Operator.DeleteAsync($"{machines}/missing-machine/grants/grant_missing"), "and a grant removal");
        t.Observe(
            await t.Operator.PostJsonAsync($"{machines}/atlas-worker/validate", new { resource = Audience, scopes = Array.Empty<string>() }),
            "a test without a secret escapes as a 500");
        await t.ObserveAuditAsync("no audit event for refused requests");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/rotate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/grants")]
    [Covers("DELETE /sqlos/admin/auth/api/machine-clients/{clientId}/grants/{grantId}")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/validate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_code_owned_machine_client_accepts_only_tests_and_the_emergency_switches()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var worker = AdminIdentity.Api + "/machine-clients/atlas-worker";
        Task<HttpExchange> Token() => t.Api.PostFormAsync(
            "/sqlos/auth/token",
            ClientCredentials(),
            options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("atlas-worker", HostProfiles.MachineClientSecret)));

        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/machine-clients"), "the seeded machine client is code-owned");
        t.Observe(await t.Operator.PostJsonAsync(worker + "/rotate", new { }), "its secret cannot be rotated here");
        t.Observe(await t.Operator.PostJsonAsync(worker + "/revoke", new { }), "it cannot be revoked here");
        t.Observe(
            await t.Operator.PostJsonAsync(worker + "/grants", new { resourceId = "workspace::alpha", roleId = BehaviorLockAuthorization.ReaderRole }),
            "nor can grants be added");
        t.Observe(await t.Operator.DeleteAsync(worker + "/grants/grant_missing"), "or removed");
        t.Observe(
            await t.Operator.PostJsonAsync(worker + "/validate", new { clientSecret = HostProfiles.MachineClientSecret, resource = Audience, scopes = new[] { BehaviorLockAuthorization.ReadPermission } }),
            "its seeded secret can be tested");
        t.Observe(await Token(), "the seeded secret gets a token");
        t.Observe(await t.Operator.PostJsonAsync(worker + "/emergency-disable", new { }), "emergency disable applies to the seed");
        t.Observe(await Token(), "the disabled seed gets no token");
        t.Observe(await t.Operator.PostJsonAsync(worker + "/emergency-enable", new { }), "emergency enable restores it");
        t.Observe(await Token(), "tokens again");
        await t.ObserveAuditAsync("tests, token issuance, and the switches");
        await t.ApproveAsync();
    }

    private static IEnumerable<KeyValuePair<string, string>> ClientCredentials()
    {
        yield return new("grant_type", "client_credentials");
        yield return new("resource", Audience);
        yield return new("scope", BehaviorLockAuthorization.ReadPermission);
    }
}
