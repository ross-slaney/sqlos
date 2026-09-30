using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// The hosted identify step and password form beyond the pilot's happy path: the password-only
/// application's identify branch, standalone sign-in, the shared failure answer, account lockout,
/// the "Use password instead" loop, and a revoked issuer cookie meeting a fresh sign-in (#434).
/// </summary>
[TestClass]
public sealed class HostedPasswordSignInScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    public async Task In_a_password_only_application_identify_leads_to_the_password_form()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize();

        var authorize = t.Observe(await t.GetAsync(request.Url), "open the hosted sign-in page");
        var identified = t.Observe(
            await t.SubmitAsync(authorize.Form("/login/identify").With("email", alice.Email)),
            "identify: the password form is the only local factor");
        var signedIn = t.Observe(
            await t.SubmitAsync(identified.Form("/login/password").With("password", alice.Password)),
            "submit the password");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(signedIn.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("password sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task Password_sign_in_without_an_authorization_request_starts_an_issuer_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");

        var login = t.Observe(await t.GetAsync("/sqlos/auth/login"), "open the sign-in page directly: no request, no providers");
        var identified = t.Observe(
            await t.SubmitAsync(login.Form("/login/identify").With("email", alice.Email)),
            "identify without an authorization request");
        var signedIn = t.Observe(
            await t.SubmitAsync(identified.Form("/login/password").With("password", alice.Password)),
            "submit the password: signed in to SqlOS itself");
        t.Observe(await t.GetAsync(signedIn.Location!), "the signed-in status page");

        await t.ObserveAuditAsync("standalone password sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task A_standalone_sign_in_that_carries_a_device_code_returns_to_the_device_page()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");

        var login = t.Observe(await t.GetAsync("/sqlos/auth/login?user_code=WDJB-MJHT"), "the sign-in page opened for a device code");
        var identified = t.Observe(
            await t.SubmitAsync(login.Form("/login/identify").With("email", alice.Email)),
            "identify: the device code rides along");
        t.Observe(
            await t.SubmitAsync(identified.Form("/login/password").With("password", alice.Password)),
            "sign in: back to the device page for that code");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task A_third_party_client_shows_the_consent_page_after_the_password()
    {
        // A dynamically registered client is never first party, so the password form's consent
        // branch renders the consent interstitial instead of returning to the client.
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var alice = await t.Setup.CreateUserAsync("alice");
        const string redirectUri = "http://127.0.0.1/callback/taskrail";
        var registered = t.Discard(await t.NewClient("mcp-client").PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
        {
            ["client_name"] = "Taskrail Desktop",
            ["redirect_uris"] = new[] { redirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = "openid profile offline_access"
        }));
        HostedFlows.EnsureStatus(registered, 201);
        await t.SkipAuditAsync();
        var request = t.Urls.Authorize(registered.JsonString("client_id"), redirectUri, "openid profile offline_access");

        var authorize = t.Observe(await t.GetAsync(request.Url), "the registered client sends the user to sign in");
        var identified = t.Observe(
            await t.SubmitAsync(authorize.Form("/login/identify").With("email", alice.Email)),
            "identify");
        t.Observe(
            await t.SubmitAsync(identified.Form("/login/password").With("password", alice.Password)),
            "the password is right: the consent page, with the client's name and scopes");

        await t.ObserveAuditAsync("sign-in events so far");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task A_wrong_password_and_an_unknown_address_get_the_same_answer()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var form = begun.Page.Form("/login/password");

        t.Observe(
            await t.SubmitAsync(form.With("email", alice.Email).With("password", "Not-Alices-Password-1")),
            "a wrong password");
        t.Observe(
            await t.SubmitAsync(form.With("email", t.Unique.Email("nobody")).With("password", "Not-Alices-Password-1")),
            "an address with no account");

        await t.ObserveAuditAsync("the audit tells the two apart");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task Five_wrong_passwords_lock_the_account_and_the_right_password_is_then_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var form = begun.Page.Form("/login/password").With("email", alice.Email);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(form.With("password", $"Wrong-Password-{attempt}"))), 400);
        }

        await t.SkipAuditAsync();
        var seen = await HostedFlows.AuditEventIdsAsync(t);
        t.Observe(await t.SubmitAsync(form.With("password", "Wrong-Password-5")), "the fifth wrong password locks the account");
        t.Observe(await t.SubmitAsync(form.With("password", alice.Password)), "the right password is refused while locked");

        // One password.login.locked event per locked bucket, in database order; see the helper.
        await HostedFlows.ObserveAuditSortedAsync(t, seen, "the lock (email and user buckets) and the refused attempt");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("POST /sqlos/auth/login/identify")]
    public async Task Use_password_instead_returns_to_the_identify_form_which_prefers_email_codes()
    {
        // Every hosted page links "Use password instead" to GET /login, which renders the identify
        // form; identify then routes to the preferred local factor, which is the email code here.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t);

        var login = t.Observe(
            await t.GetAsync($"/sqlos/auth/login?request={begun.RequestId}&email={Uri.EscapeDataString(alice.Email)}"),
            "follow 'Use password instead'");
        t.Observe(await t.SubmitAsync(login.Form("/login/identify")), "continue: the email-code form again");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_revoked_issuer_cookie_does_not_block_a_fresh_password_sign_in()
    {
        // #434: a sqlos_auth_page cookie whose family logout-all revoked used to fail every later
        // credential sign-in with "Authentication session is no longer active." until it expired.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var deadCookie = t.Browser.Cookies!.GetCookies(new Uri(BehaviorLockConstants.PublicOrigin))["sqlos_auth_page"]!.Value;
        HostedFlows.EnsureStatus(t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/logout-all", new { refreshToken = session.RefreshToken })), 204);
        await t.SkipAuditAsync();
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });

        var authorize = t.Observe(
            await t.GetAsync(request.Url),
            "authorize with the revoked cookie: no session, the sign-in page, and the cookie is deleted");
        var signedIn = t.Observe(
            await t.SubmitAsync(
                authorize.Form("/login/password").With("email", alice.Email).With("password", alice.Password),
                options => options.Cookie($"sqlos_auth_page={deadCookie}")),
            "another tab still presents the dead cookie and signs in with the password: a new session family");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(signedIn.NextUrlParameter("code"))),
            "redeem the authorization code");
        t.Observe(
            await t.GetAsync(
                t.Urls.Authorize(extra: new Dictionary<string, string?> { ["prompt"] = "none" }).Url,
                options => options.WithoutCookies().Cookie($"sqlos_auth_page={deadCookie}")),
            "the revoked family is not revived: the dead cookie still cannot sign in silently");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }
}
