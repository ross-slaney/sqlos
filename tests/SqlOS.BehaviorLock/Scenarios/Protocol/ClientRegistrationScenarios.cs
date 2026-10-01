using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// Clients SqlOS has never been told about: dynamic client registration (RFC 7591) and client ID
/// metadata documents. Both are third-party clients, so both sign users in through consent.
/// </summary>
[TestClass]
public sealed class ClientRegistrationScenarios
{
    private const string DesktopRedirectUri = "http://127.0.0.1/callback/taskrail";

    [Scenario]
    [Covers("POST /sqlos/auth/register")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_dynamically_registered_client_signs_a_user_in_through_consent()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var alice = await t.Setup.CreateUserAsync("alice");
        var desktop = t.NewClient("desktop-app");

        var registered = await t.ObserveWithAuditAsync(
            await desktop.PostJsonAsync("/sqlos/auth/register", PublicRegistration("Taskrail Desktop", DesktopRedirectUri, "openid profile offline_access")),
            "the desktop app registers itself");
        var clientId = registered.JsonString("client_id");
        var request = t.Urls.Authorize(clientId, DesktopRedirectUri, "openid profile offline_access", new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.GetAsync(request.Url), "its authorization request opens the password page");
        var consent = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "a registered client is third-party: Alice sees the consent page");
        var approved = await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/approve")), "she approves");
        var tokens = t.ObserveTokens(await desktop.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))), "the app redeems the code");
        t.ObserveTokens(await desktop.PostFormAsync("/sqlos/auth/token", Refresh(clientId, tokens.JsonString("refresh_token"))), "and rotates its refresh token");

        await t.ObserveStateAsync("/sqlos/admin/auth/api/clients?search=Taskrail", "the operator's client list shows the registered client");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/register")]
    public async Task Registration_refuses_client_metadata_it_cannot_support()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var app = t.NewClient("registering-app");
        Dictionary<string, object> Metadata(params (string Name, object Value)[] changes)
        {
            var metadata = PublicRegistration("Taskrail", "https://taskrail.example.test/callback");
            foreach (var (name, value) in changes)
            {
                metadata[name] = value;
            }

            return metadata;
        }

        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("client_secret", "chosen-by-the-client"))), "a client-chosen secret");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("redirect_uris", Array.Empty<string>()))), "no redirect URIs");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("redirect_uris", new[] { "callback" }))), "a relative redirect URI");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("redirect_uris", new[] { "http://taskrail.example.test/callback" }))), "plain HTTP off loopback");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("redirect_uris", new[] { "com.example.taskrail:/callback" }))), "a private-use URI scheme");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("grant_types", new[] { "refresh_token" }))), "grant_types without authorization_code");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("grant_types", new[] { "authorization_code", "client_credentials" }))), "the client_credentials grant");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("response_types", new[] { "token" }))), "the implicit response type");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("token_endpoint_auth_method", "private_key_jwt"))), "private_key_jwt client authentication");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("client_uri", "not a uri"))), "a client_uri that is not absolute");
        await t.ObserveWithAuditAsync(
            await app.PostJsonAsync("/sqlos/auth/register", Metadata(("scope", string.Join(' ', Enumerable.Range(1, 33).Select(index => $"scope{index}"))))),
            "more than 32 scope values");
        await t.ObserveWithAuditAsync(await app.PostJsonAsync("/sqlos/auth/register", Metadata(("scope", "openid \"quoted\""))), "a scope value with a quote");
        t.Observe(
            await app.SendAsync(HttpMethod.Post, "/sqlos/auth/register", new StringContent("{\"client_name\":", Encoding.UTF8, "application/json")),
            "malformed JSON is refused by the framework");
        t.Observe(
            await app.SendAsync(HttpMethod.Post, "/sqlos/auth/register", new StringContent("client_name=Taskrail", Encoding.UTF8, "application/x-www-form-urlencoded")),
            "a form body is not JSON");
        await t.ObserveWithAuditAsync(
            await app.PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
            {
                ["redirect_uris"] = new[] { "https://taskrail.example.test/callback" },
                ["client_id"] = "chosen-client-id",
                ["tos_uri"] = "https://taskrail.example.test/terms"
            }),
            "minimal metadata: defaults fill in, and a client-chosen client_id is ignored");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/register")]
    public async Task Registration_is_rate_limited_per_address_before_the_metadata_is_read()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var app = t.NewClient("registering-app");

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var refused = t.Discard(await app.PostJsonAsync("/sqlos/auth/register", new { client_name = "Burst" }, options => options.FromAddress("198.51.100.20")));
            Assert.AreEqual(400, refused.StatusCode, refused.Describe());
        }

        await t.SkipAuditAsync();
        t.Note("198.51.100.20 has made 25 registration requests, all invalid (no redirect URIs).");
        await t.ObserveWithAuditAsync(
            await app.PostJsonAsync("/sqlos/auth/register", PublicRegistration("Valid", "https://valid.example.test/callback"), options => options.FromAddress("198.51.100.20")),
            "the 26th request from that address is refused before its metadata is read");
        await t.ObserveWithAuditAsync(
            await app.PostJsonAsync("/sqlos/auth/register", PublicRegistration("Valid", "https://valid.example.test/callback"), options => options.FromAddress("198.51.100.21")),
            "another address still registers");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /mcp")]
    public async Task A_client_metadata_document_client_connects_to_the_mcp_resource_through_consent()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var clientId = CimdClientId("inspector");
        var redirectUri = CimdRedirectUri("inspector");
        t.Setup.PublishClientMetadata(clientId, ClientMetadataDocument(clientId, "MCP Inspector", redirectUri));
        var inspector = t.NewClient("mcp-client");

        var request = t.Urls.Authorize(clientId, redirectUri, extra: new Dictionary<string, string?> { ["view"] = "password", ["resource"] = BehaviorLockConstants.McpAudience });
        var page = t.Observe(await t.GetAsync(request.Url), "SqlOS fetches the client's metadata document and opens the password page");
        var consent = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "a metadata-document client is always third-party: the consent page");
        var approved = await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/approve")), "Alice approves");
        var tokens = t.ObserveTokens(
            await inspector.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"), BehaviorLockConstants.McpAudience)),
            "the client redeems the code for the MCP resource");
        t.Observe(
            await inspector.PostJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, options => options.Bearer(tokens.JsonString("access_token"))),
            "and calls the MCP endpoint");

        await t.ObserveStateAsync("/sqlos/admin/auth/api/clients", "the operator's client list includes the metadata-document client");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task A_client_metadata_document_must_exist_match_its_url_and_list_the_redirect()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var redirectUri = CimdRedirectUri("tool");

        var missing = CimdClientId("missing");
        await t.ObserveWithAuditAsync(await t.GetAsync(t.Urls.Authorize(missing, redirectUri).Url), "no document at the client_id URL");

        var mismatched = CimdClientId("mismatched");
        t.Setup.PublishClientMetadata(mismatched, ClientMetadataDocument(CimdClientId("someone-else"), "Mismatched", redirectUri));
        await t.ObserveWithAuditAsync(await t.GetAsync(t.Urls.Authorize(mismatched, redirectUri).Url), "a document that declares another client_id");

        var otherRedirect = CimdClientId("other-redirect");
        t.Setup.PublishClientMetadata(otherRedirect, ClientMetadataDocument(otherRedirect, "Other Redirect", CimdRedirectUri("elsewhere")));
        await t.ObserveWithAuditAsync(await t.GetAsync(t.Urls.Authorize(otherRedirect, redirectUri).Url), "a redirect_uri the document does not list");

        var confidential = CimdClientId("confidential");
        t.Setup.PublishClientMetadata(confidential, ClientMetadataDocument(confidential, "Confidential", redirectUri, tokenEndpointAuthMethod: "client_secret_basic"));
        await t.ObserveWithAuditAsync(await t.GetAsync(t.Urls.Authorize(confidential, redirectUri).Url), "a document that asks for a client secret");

        await t.ObserveWithAuditAsync(
            await t.GetAsync(t.Urls.Authorize("https://localhost/client.json", "https://localhost/callback").Url),
            "a metadata document on localhost is never fetched");
        await t.ObserveWithAuditAsync(
            await t.GetAsync(t.Urls.Authorize("http://client.example.test/plain.json", redirectUri).Url),
            "an http client_id is not a metadata document: it is an unknown client");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task A_host_that_trusts_named_metadata_hosts_refuses_every_other_host()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);

        await t.ObserveWithAuditAsync(
            await t.GetAsync(t.Urls.Authorize("https://tools.example.org/client.json", "https://tools.example.org/callback").Url),
            "Cimd.TrustedHosts lists only client.example.test");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /api/me")]
    public async Task A_metadata_document_client_can_mint_a_token_for_the_first_party_api_CurrentBehavior_KnownDefect_429()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var clientId = CimdClientId("rogue-mcp-client");
        var redirectUri = CimdRedirectUri("rogue-mcp-client");
        t.Setup.PublishClientMetadata(clientId, ClientMetadataDocument(clientId, "Handy MCP Tool", redirectUri));
        var rogue = t.NewClient("rogue-mcp-client");
        t.Note("The client should only ever receive tokens for the MCP resource. It asks for the first-party API instead.");

        var request = t.Urls.Authorize(clientId, redirectUri, extra: new Dictionary<string, string?> { ["view"] = "password", ["resource"] = BehaviorLockConstants.ApiAudience });
        var page = t.Observe(await t.GetAsync(request.Url), "known defect #429: /authorize accepts resource={origin}/api from a third-party client");
        var consent = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "the consent page names the client and scopes, not the resource");
        var approved = await t.ObserveWithAuditAsync(await t.SubmitAsync(consent.Form("/consent/approve")), "Alice approves");
        var tokens = t.ObserveTokens(
            await rogue.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"), BehaviorLockConstants.ApiAudience)),
            "known defect #429: the access token's audience is the first-party API");
        t.Observe(await rogue.GetAsync("/api/me", options => options.Bearer(tokens.JsonString("access_token"))), "known defect #429: the first-party API accepts it");

        var silent = t.Urls.Authorize(clientId, redirectUri, extra: new Dictionary<string, string?> { ["prompt"] = "none", ["resource"] = "https://attacker.example/any-audience" });
        var redirect = t.Observe(await t.GetAsync(silent.Url), "known defect #429: the remembered grant ignores the resource, so prompt=none issues a code for any audience");
        t.ObserveTokens(
            await rogue.PostFormAsync("/sqlos/auth/token", silent.TokenRequest(redirect.NextUrlParameter("code"), "https://attacker.example/any-audience")),
            "and the token carries it");

        await t.ApproveAsync();
    }
}
