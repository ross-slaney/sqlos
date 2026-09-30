using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Dcr;

[TestClass]
public sealed class DynamicClientRegistrationScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/oauth-authorization-server")]
    [Covers("POST /sqlos/auth/register")]
    public async Task A_public_client_registers_and_a_confidential_one_is_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var client = t.NewClient("mcp-client");

        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server"), "discover the registration endpoint");
        t.Observe(
            await client.PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
            {
                ["client_name"] = "Taskrail Desktop",
                ["redirect_uris"] = new[] { "http://127.0.0.1/callback/taskrail" },
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none",
                ["scope"] = "openid profile offline_access"
            }),
            "register a public PKCE client");
        t.Observe(
            await client.PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
            {
                ["client_name"] = "Taskrail Server",
                ["redirect_uris"] = new[] { "https://taskrail.example.test/callback" },
                ["grant_types"] = new[] { "authorization_code" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "client_secret_basic"
            }),
            "a confidential client is refused while PublicClientsOnly is on");

        await t.ObserveAuditAsync("registration events");
        await t.ApproveAsync();
    }
}
