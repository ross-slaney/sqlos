using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// The authorization endpoint, <c>GET</c> and <c>POST /sqlos/auth/authorize</c>: request
/// validation (error page or error redirect), silent sign-in with an existing issuer session,
/// <c>prompt</c> and <c>max_age</c>, dead and revoked issuer cookies, and the headless redirect.
/// </summary>
[TestClass]
public sealed class AuthorizeScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task Requests_rejected_before_the_client_is_trusted_show_a_safe_message_on_the_sign_in_page()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("response_type", "token"))), "response_type=token (checked before the client)");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("client_id", null))), "no client_id");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none consent"))), "prompt=none combined with another value");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("max_age", "-1"))), "a negative max_age");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("code_challenge", null), ("code_challenge_method", null))), "no PKCE challenge");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("code_challenge_method", "plain"))), "the plain PKCE method");

        await t.ObserveAuditAsync("safe messages write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task Requests_rejected_before_the_client_is_trusted_show_an_opaque_message_and_audit_the_reason()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        await t.ObserveWithAuditAsync(await t.GetAsync(Modified(t.Urls.Authorize(), ("client_id", "unknown-client"))), "an unknown client");
        await t.ObserveWithAuditAsync(
            await t.GetAsync(Modified(t.Urls.Authorize(), ("redirect_uri", "https://attacker.example/callback"))),
            "a redirect_uri the client did not register: an error page, never a redirect");
        await t.ObserveWithAuditAsync(await t.GetAsync(Modified(t.Urls.Authorize(), ("code_challenge", "too-short"))), "an S256 challenge that is not 43 characters");
        var oversizedState = new string('s', 2049);
        t.Scrub(oversizedState, "state", "2049-characters");
        await t.ObserveWithAuditAsync(await t.GetAsync(Modified(t.Urls.Authorize(), ("state", oversizedState))), "a state longer than 2048 characters");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/authorize")]
    public async Task Requests_rejected_after_the_client_is_trusted_redirect_back_with_an_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("request", "eyJhbGciOiJub25lIn0.e30."))), "a request object (OIDC Core 6.1)");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("request", "a-request-object"), ("request_uri", "https://client.example.test/request.jwt"))), "request wins over request_uri");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("request_uri", "https://client.example.test/request.jwt"))), "a request_uri");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"))), "prompt=none without a session");

        var formPost = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["prompt"] = "none" });
        t.Observe(await t.PostFormAsync("/sqlos/auth/authorize", QueryOf(formPost.Url)), "the same request form-posted (OIDC Core 3.1.2.1)");

        await t.ObserveAuditAsync("error redirects write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/authorize")]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task A_form_posted_request_opens_the_sign_in_page_and_its_query_string_is_ignored()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });

        t.Observe(await t.PostFormAsync("/sqlos/auth/authorize", QueryOf(request.Url)), "the parameters as a form body");
        t.Observe(
            await t.PostFormAsync(request.Url, Array.Empty<KeyValuePair<string, string>>()),
            "the parameters only in the query string of a POST: every parameter reads as empty");
        t.Observe(
            await t.Browser.SendAsync(HttpMethod.Post, "/sqlos/auth/authorize", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
            "a JSON body is not a form: every parameter reads as empty");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("view", " SIGNUP "))), "view is trimmed and case-insensitive");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("view", "admin"))), "an unknown view opens the sign-in page");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task An_invitation_token_on_the_request_must_be_valid()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("invitationToken", "not-an-invitation"))), "an unknown invitationToken");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("token", "not-an-invitation"))), "any token parameter is read as an invitation token");

        await t.ObserveAuditAsync();
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_signed_in_browser_gets_a_code_silently_and_prompt_and_max_age_decide_when_it_must_sign_in_again()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);

        var silent = t.Urls.Authorize();
        var redirect = t.Observe(await t.GetAsync(silent.Url), "a new authorization request: the issuer session answers with a code");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", silent.TokenRequest(redirect.NextUrlParameter("code"))), "the code redeems");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"))), "prompt=none with a session");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("max_age", "3600"))), "max_age=3600: the session is young enough");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"), ("max_age", "0"))), "prompt=none with max_age=0: login_required, and the session is kept");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("max_age", "0"))), "max_age=0 without prompt=none: SqlOS signs the session out and shows the sign-in page");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"))), "the session is gone: login_required");

        await t.Setup.SignInWithPasswordAsync(alice);
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "login"))), "prompt=login signs out and asks for a fresh sign-in");
        await t.Setup.SignInWithPasswordAsync(alice);
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "select_account"))), "select_account is read as login");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task Prompt_login_signs_the_browser_out_before_the_request_is_validated()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);

        t.Note("A cross-site link to /authorize?prompt=login, with no client at all.");
        t.Observe(await t.GetAsync("/sqlos/auth/authorize?prompt=login"), "the request is invalid, but the issuer session is already revoked and its cookie deleted");
        t.Observe(await t.GetAsync(t.Urls.Authorize().Url), "the next valid request finds no session");

        await t.ObserveAuditAsync();
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_revoked_issuer_cookie_is_signed_out_and_a_fresh_sign_in_replaces_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);
        var issuerCookie = t.Browser.Cookies!.GetCookies(new Uri(BehaviorLockConstants.PublicOrigin))["sqlos_auth_page"]!.Value;
        t.Discard(await t.Api.PostJsonAsync("/__probe/auth/logout-all", new { userId = alice.Id }));
        await t.SkipAuditAsync();
        t.Note("Alice's sessions are revoked everywhere (SqlOSAuthService.LogoutAllAsync); her browser still holds the issuer cookie.");

        var replay = t.NewBrowser("replaying-browser");
        replay.SetCookie("sqlos_auth_page", issuerCookie);
        t.Observe(await replay.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"))), "prompt=none with the dead cookie: login_required, and the cookie is deleted");

        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.GetAsync(request.Url), "the dead cookie counts as signed out: the sign-in page, and the cookie is deleted");
        var login = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "a fresh password sign-in succeeds and sets a new issuer cookie");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(login.NextUrlParameter("code"))), "the code redeems");

        var stale = t.NewBrowser("stale-browser");
        stale.SetCookie("sqlos_auth_page", issuerCookie);
        t.Observe(await stale.GetAsync(t.Urls.Authorize().Url), "the revoked family stays revoked: its old cookie still signs no one in");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    public async Task After_an_operator_revokes_a_user_the_browser_session_still_mints_codes_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice);
        var pending = t.Urls.Authorize();
        var pendingCode = await CodeWithSessionAsync(t, pending);
        t.Note("Alice is signed in, and a code issued to her browser has not been redeemed yet.");

        var preview = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { userId = alice.Id, reason = "incident" }),
            "the operator previews revoking Alice's sessions");
        t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation", new
            {
                userId = alice.Id,
                reason = "incident",
                confirm = true,
                operationId = preview.JsonString("operationId"),
                expectedMatchedSessions = preview.Json!["matchedSessions"]!.GetValue<int>()
            }),
            "and revokes them");
        await t.ObserveAuditAsync("revocation events");

        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", pending.TokenRequest(pendingCode)), "known defect #427: the code issued before the revocation still redeems");
        var request = t.Urls.Authorize();
        var redirect = t.Observe(await t.GetAsync(request.Url), "known defect #427: the issuer session survives and silently mints a new code");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(redirect.NextUrlParameter("code"))), "which redeems into a new session");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "Alice has live sessions again");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task A_request_without_redirect_uri_is_accepted_and_every_redirect_it_makes_is_relative()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("redirect_uri", null), ("prompt", "none"))), "prompt=none without a session and without redirect_uri");
        await t.Setup.SignInWithPasswordAsync(alice);
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("redirect_uri", null))), "a signed-in browser: the code goes to a relative Location on SqlOS itself");

        await t.ObserveAuditAsync();
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task A_user_in_two_organizations_must_choose_one_and_prompt_none_cannot_ask()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice, "admin");
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Discard(await t.GetAsync(request.Url));
        var chooser = t.Discard(await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        t.Discard(await t.SubmitAsync(chooser.Form("/login/select-organization").With("organizationId", acme.Id)));
        await t.SkipAuditAsync();
        t.Note("Alice signed in and chose Acme; her issuer session carries no organization.");

        t.ObserveOrganizationChooser(await t.GetAsync(t.Urls.Authorize().Url), "a new request asks her to choose again");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"))), "prompt=none cannot ask: interaction_required");

        await t.ObserveAuditAsync();
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    public async Task A_headless_application_receives_every_interactive_request_at_its_own_ui()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);

        t.Observe(await t.GetAsync(t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "signup", ["login_hint"] = "someone@example.test" }).Url), "a valid request redirects to the app's UI with the request ID and view");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("client_id", "unknown-client"))), "an invalid request redirects to the UI with an error");
        t.Observe(await t.GetAsync(Modified(t.Urls.Authorize(), ("prompt", "none"))), "prompt=none still redirects to the client");

        await t.ObserveAuditAsync();
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_access_restricted_application_refuses_unassigned_users_even_for_prompt_none()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice, AtlasClients.PortalPasswordRequest(t));
        t.Note("atlas-ops-console admits only assigned users (AccessMode selected_users_groups_roles); Alice is signed in and unassigned.");

        await t.ObserveWithAuditAsync(
            await t.GetAsync(t.Urls.Authorize(AtlasClients.OpsConsole, AtlasClients.OpsConsoleRedirectUri).Url),
            "the request is refused on SqlOS's own error page");
        await t.ObserveWithAuditAsync(
            await t.GetAsync(Modified(t.Urls.Authorize(AtlasClients.OpsConsole, AtlasClients.OpsConsoleRedirectUri), ("prompt", "none"))),
            "prompt=none gets the same error page, not a redirect");

        await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/applications/{AtlasClients.OpsConsole}/assignments", new { principalType = "user", principalId = alice.Id, reason = "on call" });
        t.Note("The operator assigns Alice to the ops console.");
        var request = t.Urls.Authorize(AtlasClients.OpsConsole, AtlasClients.OpsConsoleRedirectUri);
        var redirect = t.Observe(await t.GetAsync(request.Url), "the same session now gets a code");
        t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(redirect.NextUrlParameter("code"))), "which redeems");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_disabled_client_is_refused_at_the_authorization_and_token_endpoints()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice, AtlasClients.PortalPasswordRequest(t));
        await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/clients/{AtlasClients.Portal}/emergency-disable", new { });
        t.Note("Alice holds the portal's tokens and a browser session; the operator then emergency-disables the portal (a code-owned client cannot be disabled any other way).");

        await t.ObserveWithAuditAsync(await t.GetAsync(t.Urls.Authorize(AtlasClients.Portal, AtlasClients.PortalRedirectUri).Url), "a new authorization request for the disabled client");
        await t.ObserveWithAuditAsync(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(AtlasClients.Portal, session.RefreshToken)),
            "its refresh token");
        t.ObserveResource(
            await t.Api.GetAsync("/resource-api/me", options => options.Bearer(session.AccessToken)),
            "the separate resource API still accepts its unexpired access token");

        await t.ApproveAsync();
    }
}
