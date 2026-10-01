using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// Native headless sign-in: a first-party public client opted in with
/// <c>AllowNativeHeadlessAuth</c> creates the authorization request itself with
/// <c>POST /headless/start</c> instead of redirecting a browser to <c>/authorize</c>.
/// </summary>
[TestClass]
public sealed class HeadlessNativeStartScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task The_app_starts_a_request_itself_and_redeems_the_code_after_password_sign_in()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize();

        var started = t.Observe(
            await t.PostJsonAsync($"{Api}/start", NativeStart(request, view: "password", loginHint: alice.Email, uiContext: new { screen = "onboarding" })),
            "start natively: the response is the first view, with the login hint and UI context");
        var requestId = started.JsonString("viewModel.requestId");
        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "sign in with the password: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))),
            "redeem the authorization code with the PKCE verifier");

        await t.ObserveAuditAsync("native sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    public async Task Native_start_is_limited_to_opted_in_first_party_public_clients()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateClientAsync(t, "partner-native", "Partner Native", ["https://partner.example.test/native"], isFirstParty: false, allowNativeHeadlessAuth: true);
        await CreateClientAsync(t, "ops-console", "Ops Console", ["https://ops.example.test/callback"], isFirstParty: true);
        await CreateClientAsync(t, "server-web", "Server Web", ["https://web.example.test/callback"], isFirstParty: true, allowNativeHeadlessAuth: true, clientType: "confidential");

        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize("partner-native", "https://partner.example.test/native"))),
            "a third-party client opted in to native headless auth is still refused: it would collect credentials without consent (#419)");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize("ops-console", "https://ops.example.test/callback"))),
            "a first-party client that did not opt in");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize("server-web", "https://web.example.test/callback"))),
            "a first-party confidential client");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize("unknown-app", "https://unknown.example.test/callback"))),
            "an unknown client");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(AppClientId, "https://attacker.example.test/callback"))),
            "the opted-in app with a redirect URI it never registered");

        await t.ObserveAuditAsync("refused native starts");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    public async Task Native_start_refuses_malformed_authorization_parameters()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);

        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(), responseType: "token")),
            "response type token");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(), omitCodeChallenge: true)),
            "no PKCE code challenge");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(), codeChallengeMethod: "plain")),
            "the plain PKCE method");
        var shortChallenge = NativeStart(t.Urls.Authorize());
        shortChallenge["codeChallenge"] = "too-short-to-be-s256";
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", shortChallenge),
            "a code challenge that is not a 43-character S256 value");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(), prompt: "none login")),
            "prompt none combined with login");
        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(), maxAge: "soon")),
            "a max_age that is not a number");

        await t.ObserveAuditAsync("refused native starts");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    public async Task Native_start_with_prompt_none_returns_login_required_to_the_app()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var request = t.Urls.Authorize();

        t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(request, prompt: "none")),
            "prompt none without a session: a redirect action carrying login_required and the state");

        await t.ObserveAuditAsync("prompt none events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    [Covers("POST /sqlos/auth/headless/invitations/signup")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Native_start_with_an_invitation_opens_the_invite_view_for_the_invited_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("erin");
        var invitationToken = await HeadlessInvitationScenarios.InviteAsync(t, acme, email);
        var request = t.Urls.Authorize();

        var started = t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(request, invitationToken: invitationToken)),
            "start natively with the invitation: the invite view, prefilled with the invited address");
        var signup = t.Observe(
            await t.Api.PostJsonAsync($"{Api}/invitations/signup", new
            {
                requestId = started.JsonString("viewModel.requestId"),
                displayName = "Erin",
                email,
                invitationToken
            }),
            "accept by signing up: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(signup))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("native invitation events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Native_start_ignores_a_requested_resource_when_the_host_declares_no_mcp_surface()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        const string ForeignResource = "https://evil.example.test/api";
        var request = t.Urls.Authorize();

        var started = t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(request, resource: ForeignResource)),
            "start natively naming a foreign resource: resource indicators are off without app.Mcp, so it is not stored");
        var login = t.Observe(
            await t.Api.PostJsonAsync($"{Api}/password/login", new { requestId = started.JsonString("viewModel.requestId"), email = alice.Email, password = alice.Password }),
            "sign in: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login), ForeignResource)),
            "redeem the code naming the foreign resource: the access token keeps the client's own audience");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Native_start_accepts_a_resource_the_client_does_not_own_CurrentBehavior_KnownDefect_429()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Mcp);
        await CreateClientAsync(
            t,
            "mcp-desktop",
            "MCP Desktop",
            ["http://127.0.0.1/callback/mcp-desktop"],
            isFirstParty: true,
            allowNativeHeadlessAuth: true,
            audience: Host.BehaviorLockConstants.McpAudience);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize("mcp-desktop", "http://127.0.0.1/callback/mcp-desktop");

        t.Note("Known defect #429: the MCP client's audience is the MCP surface, but a requested resource is stored without checking it, so the token opens the first-party API.");
        var started = t.Observe(
            await t.Api.PostJsonAsync($"{Api}/start", NativeStart(request, resource: Host.BehaviorLockConstants.ApiAudience)),
            "an MCP-audience native client starts natively for the app's first-party API resource: accepted");
        var login = t.Observe(
            await t.Api.PostJsonAsync($"{Api}/password/login", new { requestId = started.JsonString("viewModel.requestId"), email = alice.Email, password = alice.Password }),
            "sign in: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login), Host.BehaviorLockConstants.ApiAudience)),
            "redeem the code for that resource: the access token's aud is the first-party API, which the SqlOS scheme on /api accepts");

        await t.ObserveAuditAsync("foreign resource events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/start")]
    public async Task A_dynamically_registered_client_cannot_start_native_headless_auth()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var attacker = t.NewClient("registered-client");
        var registered = t.Discard(await attacker.PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
        {
            ["client_name"] = "Collector",
            ["redirect_uris"] = new[] { "http://127.0.0.1/callback/collector" },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = "openid profile offline_access"
        }));
        var clientId = registered.JsonString("client_id");
        t.Scrub(clientId, "client-id", "registered");
        await t.SkipAuditAsync();

        t.Observe(
            await attacker.PostJsonAsync($"{Api}/start", NativeStart(t.Urls.Authorize(clientId, "http://127.0.0.1/callback/collector", "openid profile offline_access"))),
            "a client that registered itself (DCR, never first party) tries to collect credentials in its own UI (#419): refused");

        await t.ObserveAuditAsync("refused native start events");
        await t.ApproveAsync();
    }
}
