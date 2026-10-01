using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Client secrets for confidential OAuth clients: issuing them once, listing them without the
/// secret, revoking them, the limits, and code-owned secrets that only source control may change.
/// The token endpoint shows whether a secret authenticates: a bogus authorization code answers
/// <c>invalid_grant</c> to an authenticated client and <c>invalid_client</c> otherwise.
/// </summary>
[TestClass]
public sealed class AdminIdentityClientCredentialScenarios
{
    private const string WebRedirect = "https://web.example.test/signin";

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("DELETE /sqlos/admin/auth/api/clients/{clientId}/credentials/{credentialId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Operator_issues_and_revokes_secrets_for_a_confidential_client()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        await AdminIdentity.CreateClientAsync(t, new
        {
            clientId = "web",
            name = "Web",
            audience = "https://web.example.test/api",
            redirectUris = new[] { WebRedirect },
            clientType = "confidential"
        });
        var credentials = AdminIdentity.Api + "/clients/web/credentials";

        t.Observe(await t.Operator.GetAsync(credentials), "a new confidential client has no secrets");
        var primary = t.Observe(
            await t.Operator.PostJsonAsync(credentials, new { displayName = "  Primary  ", expiresAt = DateTime.UtcNow.AddDays(90) }),
            "issue a named, expiring secret; it is shown once");
        var secondary = t.Observe(await t.Operator.PostJsonAsync(credentials, new { }), "issue an unnamed secret that never expires");
        t.Observe(await t.Operator.GetAsync(credentials), "the list shows both, newest first, without secrets");
        await t.ObserveAuditAsync("issuing secrets is audited");

        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", BogusCode(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("web", primary.JsonString("clientSecret")))),
            "the primary secret authenticates the client");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", BogusCode(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("web", "not-the-secret"))),
            "a wrong secret does not");
        var primaryId = primary.JsonString("credential.id");
        t.Observe(await t.Operator.DeleteAsync($"{credentials}/{primaryId}"), "revoke the primary secret");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", BogusCode(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("web", primary.JsonString("clientSecret")))),
            "the revoked secret no longer authenticates");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", BogusCode(), options => options.Header("Authorization", "Basic " + AdminIdentity.Basic("web", secondary.JsonString("clientSecret")))),
            "the other secret still does");
        t.Observe(await t.Operator.DeleteAsync($"{credentials}/{primaryId}"), "revoking it again succeeds again");
        t.Observe(await t.Operator.DeleteAsync($"{credentials}/clcred_missing"), "an unknown secret is not found");
        t.Observe(await t.Operator.GetAsync(credentials), "the revoked secret keeps its row");
        await t.ObserveAuditAsync("each revocation is audited, the repeated one too");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("DELETE /sqlos/admin/auth/api/clients/{clientId}/credentials/{credentialId}")]
    public async Task Secrets_are_limited_to_five_active_ones_on_dashboard_owned_confidential_clients()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        await AdminIdentity.CreateClientAsync(t, new { clientId = "web", name = "Web", redirectUris = new[] { WebRedirect }, clientType = "confidential" });
        await AdminIdentity.CreateClientAsync(t, new { clientId = "spa", name = "SPA", redirectUris = new[] { "https://spa.example.test/callback" } });
        var credentials = AdminIdentity.Api + "/clients/web/credentials";
        await t.Setup.OperatorPostAsync(credentials, new { displayName = "expired", expiresAt = DateTime.UtcNow.AddMinutes(-5) });
        for (var issued = 1; issued <= 5; issued++)
        {
            await t.Setup.OperatorPostAsync(credentials, new { displayName = $"secret {issued}" });
        }

        t.Note("Setup issued one already-expired secret and five active ones.");
        t.Observe(await t.Operator.PostJsonAsync(credentials, new { displayName = "sixth" }), "a sixth active secret is refused; the expired one does not count");
        t.Observe(
            await t.Operator.PostJsonAsync(credentials, new { displayName = new string('n', 201) }),
            "an over-long display name is refused before the limit is checked");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/clients/spa/credentials", new { displayName = "public" }),
            "a public client cannot have secrets");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/clients/{BehaviorLockConstants.AppClientId}/credentials", new { }),
            "a code-owned client refuses first on ownership");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/clients/cli_missing/credentials"), "an unknown client's secrets are not found");
        t.Observe(await t.Operator.PostJsonAsync(AdminIdentity.Api + "/clients/cli_missing/credentials", new { }), "nor can one be issued");
        t.Observe(await t.Operator.DeleteAsync(AdminIdentity.Api + "/clients/cli_missing/credentials/clcred_missing"), "nor revoked");
        var page = t.Observe(await t.Operator.GetAsync(credentials + "?pageSize=4"), "four secrets to a page");
        t.Observe(
            await t.Operator.GetAsync(credentials + "?pageSize=4&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "the remaining two, the expired one last");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("DELETE /sqlos/admin/auth/api/clients/{clientId}/credentials/{credentialId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_code_owned_client_secret_changes_only_in_source_control()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var credentials = AdminIdentity.Api + "/clients/atlas-confidential/credentials";

        var listed = t.Observe(await t.Operator.GetAsync(credentials), "the seeded client's secret is code-owned");
        t.Observe(await t.Operator.PostJsonAsync(credentials, new { displayName = "extra" }), "the admin API cannot add one");
        t.Observe(
            await t.Operator.DeleteAsync($"{credentials}/{listed.JsonString("data.0.id")}"),
            "nor revoke the seeded one");
        t.Observe(
            await t.Api.PostFormAsync(
                "/sqlos/auth/token",
                [
                    new("grant_type", "authorization_code"),
                    new("code", "bogus-authorization-code"),
                    new("redirect_uri", "https://web.example.test/signin-oidc"),
                    new("client_id", "atlas-confidential"),
                    new("client_secret", HostProfiles.ConfidentialClientSecret)
                ]),
            "the seeded secret authenticates with client_secret_post");
        await t.ObserveAuditAsync("audit events");
        await t.ApproveAsync();
    }

    private static IEnumerable<KeyValuePair<string, string>> BogusCode()
    {
        yield return new("grant_type", "authorization_code");
        yield return new("code", "bogus-authorization-code");
        yield return new("redirect_uri", WebRedirect);
    }
}
