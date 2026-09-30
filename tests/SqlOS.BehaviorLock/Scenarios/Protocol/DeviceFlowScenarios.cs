using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// The OAuth 2.0 device authorization grant (RFC 8628): a CLI starts a device authorization and
/// polls the token endpoint while the user enters the code in the hosted device pages and
/// approves or denies it.
/// </summary>
[TestClass]
public sealed class DeviceFlowScenarios
{
    private const string CliScope = "openid profile email offline_access";

    [Scenario]
    [Covers("POST /sqlos/auth/device_authorization")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/auth/device")]
    [Covers("POST /sqlos/auth/device/verify")]
    [Covers("GET /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/device/approve")]
    public async Task A_cli_receives_tokens_after_a_signed_in_user_approves_its_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice, AtlasClients.PortalPasswordRequest(t));
        var cli = t.NewClient("cli");

        var started = await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))),
            "the CLI starts a device authorization");
        var deviceCode = started.JsonString("device_code");
        var userCode = started.JsonString("user_code");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)), "the first poll: authorization_pending");
        await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)),
            "polling again at once: slow_down, and the interval grows by five seconds");

        var entry = t.ObservePage(await t.GetAsync("/sqlos/auth/device"), "Alice opens the verification page");
        var typed = userCode.ToLowerInvariant().Replace('-', ' ');
        t.Scrub(typed, "user-code", "as-typed");
        var verified = t.Observe(
            await t.SubmitAsync(entry.Form("/device/verify").With("userCode", typed)),
            "she types the code in lower case with a space; SqlOS redirects without checking it");
        var resolved = t.ObservePage(await t.GetAsync(verified.Location!), "the code resolves for her signed-in session: SqlOS continues to the approval page");
        var approval = t.ObservePage(await t.GetAsync(resolved.NextUrl!), "the approval page names the CLI, the code, the scopes, and the expiry");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(approval.Form("/device/approve")), "she approves");

        var tokens = await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)),
            "the next poll returns tokens (approved polls are never slowed down)");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)), "the device code cannot be redeemed twice");
        t.ObserveTokens(
            await cli.PostFormAsync("/sqlos/auth/token", Refresh(AtlasClients.Cli, tokens.JsonString("refresh_token"))),
            "the CLI rotates its refresh token");
        t.ObservePage(await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(userCode)}"), "the consumed code is no longer accepted on the device page");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "Alice's sessions");
        await t.ObserveAuditAsync("remaining events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/device")]
    [Covers("GET /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_user_who_is_not_signed_in_signs_in_from_the_device_page_and_approves()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))));
        await t.SkipAuditAsync();
        var deviceCode = started.JsonString("device_code");
        var userCode = started.JsonString("user_code");

        var login = t.ObservePage(
            await t.GetAsync(new Uri(started.JsonString("verification_uri_complete")).PathAndQuery),
            "the complete verification URI asks Alice to sign in to approve CLI access");
        var password = t.ObservePage(
            await t.SubmitAsync(login.Form("/login/identify").With("email", alice.Email)),
            "she enters her email");
        var signedIn = t.ObservePage(
            await t.SubmitAsync(password.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "she enters her password");
        var approval = t.ObservePage(await t.GetAsync(signedIn.NextUrl!), "SqlOS continues to the approval page");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(approval.Form("/device/approve")), "she approves");
        await t.ObserveWithAuditAsync(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)), "the CLI's poll returns tokens");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device/deny")]
    [Covers("GET /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_denied_device_code_tells_the_cli_access_denied_on_every_poll()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice, AtlasClients.PortalPasswordRequest(t));
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))));
        await t.SkipAuditAsync();
        var deviceCode = started.JsonString("device_code");
        var userCode = started.JsonString("user_code");

        var resolved = t.Discard(await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(userCode)}"));
        var approval = t.ObservePage(await t.GetAsync(resolved.NextUrl!), "the approval page");
        await t.ObserveWithAuditAsync(
            await t.SubmitAsync(approval.Form("/device/deny")),
            "Alice denies; the page says access was denied above a card that says it was approved");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)), "the CLI is told access_denied");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)), "and again on the next poll, without slow_down");
        await t.ObserveUnhandledAsync(
            async () => await t.SubmitAsync(approval.Form("/device/deny")),
            "denying the same request again");
        await t.ObserveUnhandledAsync(
            async () => await t.GetAsync(resolved.NextUrl!),
            "reopening the approval page of the cancelled request");
        t.ObservePage(await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(userCode)}"), "the denied code is no longer pending");

        await t.ObserveAuditAsync("remaining events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device_authorization")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Device_authorization_start_admits_only_device_clients_and_their_own_audience()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var cli = t.NewClient("cli");

        await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Portal), ("scope", CliScope))),
            "a browser client is not allowed to use device authorization");
        await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope), ("resource", "https://sqlos.example.test/api"))),
            "a resource other than the client's audience is invalid_target");
        var scoped = await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", "openid admin"), ("resource", "https://api.example.test"))),
            "the client's own audience is accepted, and an unknown scope is dropped without an error");
        var deviceCode = scoped.JsonString("device_code");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode)), "polling without the resource the device authorization named");
        t.ObserveTokens(
            await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode).Append(new("resource", "https://api.example.test"))),
            "polling with it: still pending");

        await t.ObserveUnhandledAsync(
            async () => await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("scope", CliScope))),
            "no client_id");
        await t.ObserveUnhandledAsync(
            async () => await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", "atlas-unknown"), ("scope", CliScope))),
            "an unknown client");
        await t.ObserveUnhandledAsync(
            async () => await cli.PostJsonAsync("/sqlos/auth/device_authorization", new { client_id = AtlasClients.Cli }),
            "a JSON body instead of a form");

        await t.ObserveAuditAsync("remaining events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device_authorization")]
    public async Task Device_authorization_starts_are_limited_per_address_and_per_client()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var cli = t.NewClient("cli");

        for (var start = 0; start < 60; start++)
        {
            var admitted = t.Discard(await cli.PostFormAsync(
                "/sqlos/auth/device_authorization",
                Form(("client_id", AtlasClients.Cli), ("scope", CliScope)),
                options => options.FromAddress("198.51.100.7")));
            Assert.AreEqual(200, admitted.StatusCode, admitted.Describe());
        }

        t.Note("198.51.100.7 has started 60 device authorizations for the CLI in the last hour.");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope)), options => options.FromAddress("198.51.100.7")),
            "the 61st start from the same address is slow_down");

        for (var start = 0; start < 60; start++)
        {
            var admitted = t.Discard(await cli.PostFormAsync(
                "/sqlos/auth/device_authorization",
                Form(("client_id", AtlasClients.Cli), ("scope", CliScope)),
                options => options.FromAddress("198.51.100.8")));
            Assert.AreEqual(200, admitted.StatusCode, admitted.Describe());
        }

        t.Note("198.51.100.8 has started 60 more, so the CLI client has 120 starts in the last hour.");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope)), options => options.FromAddress("198.51.100.9")),
            "a start from a fresh address is refused by the per-client limit");

        await t.SkipAuditAsync();
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task Device_polling_rejects_missing_unknown_and_foreign_codes()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))));
        await t.SkipAuditAsync();
        var deviceCode = started.JsonString("device_code");

        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", Form(("grant_type", DeviceCodeGrant), ("client_id", AtlasClients.Cli))), "no device_code");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, "not-a-device-code")), "an unknown device code");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Portal, deviceCode)), "a client that is not allowed to use device authorization");
        await t.ObserveWithAuditAsync(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll("atlas-unknown", deviceCode)), "an unknown client gets the opaque grant error");
        t.ObserveTokens(
            await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, deviceCode).Append(new("resource", "https://api.example.test"))),
            "a resource the device authorization did not name cannot be introduced while polling");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device_authorization")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_expired_device_code_is_expired_token_for_the_cli_and_the_device_page()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.MultiApp,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.DeviceAuthorization.Lifetime = TimeSpan.FromSeconds(2));
        var cli = t.NewClient("cli");

        var started = await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))),
            "a device authorization with a two-second lifetime (DeviceAuthorization.Lifetime)");
        await Task.Delay(TimeSpan.FromSeconds(3));
        t.Note("Three seconds later the device code has expired.");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, started.JsonString("device_code"))), "the CLI's poll is expired_token");
        t.ObservePage(
            await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(started.JsonString("user_code"))}"),
            "the device page no longer accepts the code");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device_authorization")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/auth/device")]
    public async Task With_the_device_flow_disabled_every_device_route_refuses()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.MultiApp,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.DeviceAuthorization.Enabled = false);
        var cli = t.NewClient("cli");

        t.Observe(await cli.GetAsync("/sqlos/auth/.well-known/oauth-authorization-server"), "the metadata a CLI reads first");
        t.Observe(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))), "starting a device authorization");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, "any-device-code")), "polling");
        t.ObservePage(await t.GetAsync("/sqlos/auth/device"), "the code entry page still renders");
        await t.ObserveWithAuditAsync(await t.GetAsync("/sqlos/auth/device?user_code=ABCD-EFGH"), "entering a code");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device/approve")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_device_approval_cannot_rebind_an_ordinary_authorization_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var mallory = await t.Setup.CreateUserAsync("mallory");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var victimOrganization = await t.Setup.CreateOrganizationAsync("victim");
        await t.Setup.SignInWithPasswordAsync(mallory, AtlasClients.PortalPasswordRequest(t));
        await t.Setup.AddMembershipAsync(acme, mallory);
        t.Note("Issue #418: Mallory, a member of Acme only, has an ordinary authorization request open for the partner (a third-party client) and tries to bind it to the victim organization through the device approval form.");

        var request = t.Urls.Authorize(AtlasClients.Partner, AtlasClients.PartnerRedirectUri);
        var consent = t.Observe(await t.GetAsync(request.Url), "the partner's request stops at consent");
        var consentForm = consent.Form("/consent/approve");
        var forged = new HtmlForm("/sqlos/auth/device/approve",
        [
            new("__RequestVerificationToken", consentForm["__RequestVerificationToken"]),
            new("requestId", consentForm["requestId"]),
            new("userCode", ""),
            new("organizationId", victimOrganization.Id)
        ]);
        await t.ObserveWithAuditAsync(await t.SubmitAsync(forged), "the device approval form refuses a request that is not a device request, and changes nothing");
        await t.ObserveUnhandledAsync(
            async () => await t.SubmitAsync(new HtmlForm("/sqlos/auth/device/deny", forged.Fields.Where(field => field.Key != "organizationId"))),
            "the device deny form with the same request");
        await t.ObserveUnhandledAsync(
            async () => await t.GetAsync($"/sqlos/auth/device/approve?request={Uri.EscapeDataString(consentForm["requestId"])}"),
            "the device approval page for the same request");

        var approved = await t.ObserveWithAuditAsync(await t.SubmitAsync(consentForm), "the untouched request still completes normally");
        t.ObserveTokens(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))),
            "its token names Acme, never the victim organization");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_user_in_two_organizations_picks_the_one_the_cli_acts_in()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice, AtlasClients.PortalPasswordRequest(t));
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var initech = await t.Setup.CreateOrganizationAsync("initech");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice, "admin");
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))));
        await t.SkipAuditAsync();
        var userCode = started.JsonString("user_code");
        t.Note("Alice is signed in and now belongs to Acme and Globex.");

        var chooser = await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(userCode)}");
        var chooserForm = chooser.Form("/login/select-organization");
        t.ObserveOrganizationChooser(chooser, "the device page asks which organization the CLI should act in");
        HtmlForm Approve(string organizationId) => new("/sqlos/auth/device/approve",
        [
            new("__RequestVerificationToken", chooserForm["__RequestVerificationToken"]),
            new("requestId", chooserForm["requestId"]),
            new("userCode", userCode),
            new("organizationId", organizationId)
        ]);
        t.ObserveOrganizationChooser(await t.SubmitAsync(Approve(initech.Id)), "approving for an organization she does not belong to: the approval page again, with an error");
        await t.ObserveAuditAsync();
        await t.ObserveWithAuditAsync(await t.SubmitAsync(Approve(globex.Id)), "approving for Globex");
        await t.ObserveWithAuditAsync(
            await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, started.JsonString("device_code"))),
            "the CLI's tokens act in Globex");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device/verify")]
    [Covers("POST /sqlos/auth/device/approve")]
    [Covers("GET /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/device/deny")]
    [Covers("POST /sqlos/auth/token")]
    public async Task The_device_forms_need_an_antiforgery_token_and_approval_needs_a_session_but_denial_does_not()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))));
        await t.SkipAuditAsync();
        var userCode = started.JsonString("user_code");
        var stranger = t.NewBrowser("stranger");
        var entry = t.Discard(await stranger.GetAsync("/sqlos/auth/device"));
        var token = entry.Form("/device/verify")["__RequestVerificationToken"];

        t.Observe(await stranger.SubmitAsync(entry.Form("/device/verify").With("userCode", userCode).Without("__RequestVerificationToken")), "the code entry form without its antiforgery token");
        t.Observe(await stranger.SubmitAsync(entry.Form("/device/verify").With("userCode", "not a code")), "the code entry form redirects whatever was typed");
        t.Observe(
            await stranger.SubmitAsync(new HtmlForm("/sqlos/auth/device/approve", [new("__RequestVerificationToken", token), new("userCode", userCode)])),
            "approving without a session goes back to the device page");
        var login = t.Discard(await stranger.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(userCode)}"));
        var requestId = login.Form("/login/identify")["requestId"];
        t.Observe(await stranger.GetAsync($"/sqlos/auth/device/approve?request={Uri.EscapeDataString(requestId)}"), "the approval page without a session goes back to the device page");
        await t.ObserveUnhandledAsync(async () => await stranger.GetAsync("/sqlos/auth/device/approve?request=req_00000000000000000000000000000000"), "the approval page for an unknown request");
        await t.ObserveUnhandledAsync(async () => await stranger.GetAsync("/sqlos/auth/device/approve"), "the approval page without a request");
        await t.ObserveWithAuditAsync(
            await stranger.SubmitAsync(new HtmlForm("/sqlos/auth/device/deny", [new("__RequestVerificationToken", token), new("userCode", userCode)])),
            "anyone who holds the code can deny it without signing in");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, started.JsonString("device_code"))), "the CLI is told access_denied");
        await t.ObserveUnhandledAsync(
            async () => await stranger.SubmitAsync(new HtmlForm("/sqlos/auth/device/deny", [new("__RequestVerificationToken", token), new("userCode", "ZZZZ-ZZZZ")])),
            "denying an unknown code");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Replaying_the_approval_form_after_the_cli_redeemed_the_code_fails_unhandled()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.SignInWithPasswordAsync(alice, AtlasClients.PortalPasswordRequest(t));
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", AtlasClients.Cli), ("scope", CliScope))));
        var resolved = t.Discard(await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(started.JsonString("user_code"))}"));
        var approval = t.Discard(await t.GetAsync(resolved.NextUrl!));
        var approveForm = approval.Form("/device/approve");
        await t.SkipAuditAsync();

        await t.ObserveWithAuditAsync(await t.SubmitAsync(approveForm), "Alice approves");
        await t.ObserveWithAuditAsync(await t.SubmitAsync(approveForm), "approving again before the CLI polls: the request is no longer active");
        t.ObserveTokens(await cli.PostFormAsync("/sqlos/auth/token", DevicePoll(AtlasClients.Cli, started.JsonString("device_code"))), "the CLI redeems the code");
        await t.ObserveUnhandledAsync(async () => await t.SubmitAsync(approveForm), "approving again after the code was redeemed");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/device")]
    [Covers("GET /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/device_authorization")]
    public async Task A_headless_application_receives_device_verification_at_its_own_ui()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/clients", new
        {
            clientId = "behavior-lock-cli",
            name = "Behavior Lock CLI",
            audience = BehaviorLockConstants.ApiAudience,
            redirectUris = Array.Empty<string>(),
            allowedScopes = new[] { "openid", "profile", "offline_access" },
            isFirstParty = true,
            allowDeviceAuthorization = true,
            clientType = "public_cli"
        });
        var cli = t.NewClient("cli");
        t.Note("The operator added a CLI client with device authorization (single-application mode takes no seeded clients).");

        t.Observe(await t.GetAsync("/sqlos/auth/device"), "the device page redirects to the app's UI");
        var started = t.Observe(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", Form(("client_id", "behavior-lock-cli"), ("scope", "openid profile offline_access"))),
            "the CLI starts a device authorization; the verification URI is still SqlOS's");
        var redirected = t.Observe(
            await t.GetAsync($"/sqlos/auth/device?user_code={Uri.EscapeDataString(started.JsonString("user_code"))}"),
            "a valid code redirects to the app's UI with the device request");
        t.Observe(await t.GetAsync("/sqlos/auth/device?user_code=ZZZZ-ZZZZ"), "an unknown code renders SqlOS's own hosted error page instead");
        t.Observe(
            await t.GetAsync($"/sqlos/auth/device/approve?request={Uri.EscapeDataString(redirected.NextUrlParameter("request"))}"),
            "the approval page redirects to the app's UI sign-in");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }
}
