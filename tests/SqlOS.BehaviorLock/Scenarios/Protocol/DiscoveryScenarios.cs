using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// What SqlOS publishes for clients and resource servers to discover it: OpenID Provider and
/// RFC 8414 metadata, the JWKS, and RFC 9728 protected-resource metadata. Each deployment model
/// publishes a different document, so each profile that changes the document has a scenario.
/// </summary>
[TestClass]
public sealed class DiscoveryScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/openid-configuration")]
    [Covers("GET /sqlos/auth/.well-known/oauth-authorization-server")]
    [Covers("GET /sqlos/auth/.well-known/jwks.json")]
    public async Task A_single_application_publishes_provider_metadata_and_its_signing_key()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var client = t.NewClient("relying-party");

        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/openid-configuration"), "OpenID Provider metadata");
        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server"), "OAuth 2.0 authorization server metadata (RFC 8414)");
        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/jwks.json"), "the JWKS holds the one active signing key");
        t.Observe(
            await client.GetAsync("/sqlos/auth/.well-known/openid-configuration", options => options.Header("Host", "attacker.example")),
            "a request for another Host still advertises the configured issuer and endpoints");

        await t.ObserveAuditAsync("discovery writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/jwks.json")]
    [Covers("GET /api/me")]
    public async Task After_a_key_rotation_the_jwks_publishes_the_new_key_and_the_retiring_one()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var before = await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("resource-server");

        var original = t.Observe(await client.GetAsync("/sqlos/auth/.well-known/jwks.json"), "one key before the rotation");
        t.Observe(await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/signing-keys/rotate", new { }), "the operator rotates the signing key");
        t.ObserveJwksInStableOrder(
            await client.GetAsync("/sqlos/auth/.well-known/jwks.json"),
            [original.JsonString("keys.0.kid")],
            "the new key and the retiring key are both published");

        var after = await t.Setup.SignInWithPasswordAsync(alice, browser: t.NewBrowser("second-device"));
        t.Note("A token signed before the rotation and one signed after it: the kid in each header names the key that signed it.");
        t.ObserveResource(await client.GetAsync("/api/me", options => options.Bearer(before.AccessToken)), "a token signed with the retiring key still validates");
        t.ObserveResource(await client.GetAsync("/api/me", options => options.Bearer(after.AccessToken)), "a token signed with the new key validates");

        await t.ObserveAuditAsync("key rotation events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/openid-configuration")]
    [Covers("GET /sqlos/auth/.well-known/oauth-authorization-server")]
    public async Task A_standalone_identity_server_publishes_its_configured_issuer_and_every_grant()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var client = t.NewClient("relying-party");

        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/openid-configuration"), "OpenID Provider metadata");
        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server"), "OAuth 2.0 authorization server metadata");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/openid-configuration")]
    public async Task A_compatibility_authorization_server_advertises_registration_and_client_metadata_documents()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var client = t.NewClient("mcp-client");

        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/openid-configuration"), "OpenID Provider metadata with DCR and CIMD");
        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource"), "no protected-resource metadata: this host declares no API or MCP surface");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/.well-known/oauth-authorization-server")]
    [Covers("GET /sqlos/auth/.well-known/jwks.json")]
    public async Task An_oauth_only_server_publishes_no_openid_metadata_and_no_userinfo()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.OAuthOnly);
        var client = t.NewClient("client");

        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server"), "OAuth 2.0 metadata without OpenID fields");
        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/jwks.json"), "the JWKS is still published for access tokens");
        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/openid-configuration"), "OpenID discovery is not mapped");
        t.Observe(await client.GetAsync("/sqlos/auth/userinfo"), "UserInfo is not mapped");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /.well-known/oauth-protected-resource")]
    [Covers("GET /.well-known/oauth-protected-resource/api")]
    public async Task A_single_application_describes_its_api_as_a_protected_resource()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var client = t.NewClient("resource-client");

        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource"), "the well-known root describes the API");
        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource/api"), "the path-suffixed document for /api (RFC 9728 §3)");
        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource/mcp"), "no MCP surface is declared, so there is no /mcp document");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /.well-known/oauth-protected-resource")]
    [Covers("GET /.well-known/oauth-protected-resource/api")]
    [Covers("GET /.well-known/oauth-protected-resource/mcp")]
    [Covers("GET /sqlos/auth/.well-known/openid-configuration")]
    public async Task An_mcp_host_describes_its_api_and_mcp_resources_and_advertises_resource_indicators()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var client = t.NewClient("mcp-client");

        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource"), "the well-known root describes the API");
        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource/api"), "the API document");
        t.Observe(await client.GetAsync("/.well-known/oauth-protected-resource/mcp"), "the MCP document names the MCP audience");
        t.Observe(await client.GetAsync("/sqlos/auth/.well-known/openid-configuration"), "the provider metadata an MCP client reads next");

        await t.ApproveAsync();
    }
}
