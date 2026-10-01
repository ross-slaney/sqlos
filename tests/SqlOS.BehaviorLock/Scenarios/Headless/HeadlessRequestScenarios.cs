using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// Loading an authorization request into the app's UI (<c>GET /headless/requests/{id}</c>): the
/// hand-off parameters the <c>@sqlos/headless</c> package forwards, view normalization for every
/// view the package's contract names, unknown and finished requests, and consent-token reloads.
/// </summary>
[TestClass]
public sealed partial class HeadlessRequestScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    public async Task Loading_a_request_echoes_the_handoff_parameters_and_normalizes_every_contract_view()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var email = t.Unique.Email("paula");

        t.Observe(
            await t.GetAsync(
                $"{Api}/requests/{requestId}?view=%20SIGNUP%20&error=Try%20again&email={Uri.EscapeDataString(email)}" +
                "&displayName=Paula&pendingToken=pending-from-the-url"),
            "reload with the parameters the package forwards: the view is trimmed and lower-cased, the rest echoed");
        t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}?view=not-a-view"),
            "an unknown view name falls back to login");

        // Every view the package's contract (packages/headless/src/contract.ts HEADLESS_VIEWS) names
        // must round-trip through the server's normalization; the table locks the mapping compactly.
        foreach (var view in ContractViews())
        {
            var loaded = t.Discard(await t.GetAsync($"{Api}/requests/{requestId}?view={Uri.EscapeDataString(view)}"));
            t.Note($"view={view} -> {loaded.StatusCode} view={loaded.Json?["view"]?.GetValue<string>() ?? "(none)"}");
        }

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/identify")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    public async Task Unknown_and_finished_requests_are_rejected_as_json_errors()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.GetAsync($"{Api}/requests/req_00000000000000000000000000000000"),
            "an authorization request that never existed");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }));
        t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}"),
            "a request that already issued its code");
        t.Observe(
            await t.PostJsonAsync($"{Api}/identify", new { requestId, email = alice.Email }),
            "identify on the finished request");
        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "a second password sign-in on the finished request");

        await t.ObserveAuditAsync("rejection events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    public async Task After_a_password_sign_in_reaches_consent_a_reload_has_no_consent_token_to_recover()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreatePartnerClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(PartnerClientId, PartnerRedirectUri));
        var login = t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }));

        t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}?view=consent"),
            "the UI lost its consent token and reloads: consent comes before the issuer session, so nothing can be re-minted");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId, consentToken = login.JsonString("viewModel.consentToken") }),
            "the token from the sign-in response still approves");

        await t.ObserveAuditAsync("consent events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    public async Task A_signed_in_browser_reloading_consent_gets_a_fresh_token_and_other_browsers_do_not()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreatePartnerClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var bobsBrowser = t.NewBrowser("bobs-browser");
        await SignInAsync(t, alice);
        await SignInAsync(t, bob, bobsBrowser);

        var toConsent = t.Observe(
            await t.GetAsync(t.Urls.Authorize(PartnerClientId, PartnerRedirectUri).Url),
            "alice's signed-in browser authorizes the partner: the UI's consent view, with a consent token in the URL");
        var requestId = toConsent.NextUrlParameter("request");
        var reloaded = t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}?view=consent"),
            "alice's browser reloads the consent view: a fresh consent token from her issuer session");
        t.Observe(
            await bobsBrowser.GetAsync($"{Api}/requests/{requestId}?view=consent"),
            "bob's signed-in browser loads alice's request: no consent token for another user");
        t.Observe(
            await t.NewBrowser("anonymous").GetAsync($"{Api}/requests/{requestId}?view=consent"),
            "a browser without an issuer session: no consent token");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId, consentToken = reloaded.JsonString("consentToken") }),
            "approve with the re-minted token: a redirect to the partner with a code");

        await t.ObserveAuditAsync("consent reload events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/headless/requests/{requestId}")]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_request_that_omits_openid_warns_the_ui_and_issues_no_id_token()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize(scope: "profile email offline_access");
        var requestId = await OpenAuthorizeAsync(t, request);

        t.Observe(
            await t.GetAsync($"{Api}/requests/{requestId}"),
            "the UI loads a request whose scope omits openid: omittedOpenId and a warning in info");
        t.Observe(
            await t.PostJsonAsync($"{Api}/email-otp/start", new { requestId, email = alice.Email }),
            "a view with its own info message carries the warning too");
        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "sign in with the password: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))),
            "the token response has no ID token");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }

    /// <summary>The view names in <c>packages/headless/src/contract.ts</c> (<c>HEADLESS_VIEWS</c>), in order.</summary>
    private static IReadOnlyList<string> ContractViews()
    {
        var contract = File.ReadAllText(RepositoryPaths.Combine("packages", "headless", "src", "contract.ts"));
        var block = ViewsBlock().Match(contract);
        if (!block.Success)
        {
            throw new InvalidOperationException("packages/headless/src/contract.ts no longer exports HEADLESS_VIEWS as a literal array.");
        }

        return Quoted().Matches(block.Groups["body"].Value).Select(match => match.Groups[1].Value).ToList();
    }

    [GeneratedRegex(@"export const HEADLESS_VIEWS = \[(?<body>[\s\S]*?)\] as const")]
    private static partial Regex ViewsBlock();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quoted();
}
