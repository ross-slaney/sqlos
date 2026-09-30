using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// The device authorization grant approved or denied in the app's headless UI: both the
/// request-bound path (the verification link) and the user-code path.
/// </summary>
[TestClass]
public sealed class HeadlessDeviceScenarios
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    [Scenario]
    [Covers("POST /sqlos/auth/device_authorization")]
    [Covers("GET /sqlos/auth/device")]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("GET /sqlos/auth/device/approve")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_cli_code_opened_from_the_verification_link_is_approved_after_sign_in()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var cli = t.NewClient("cli");

        var started = t.Observe(
            await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
            {
                ["client_id"] = CliClientId,
                ["scope"] = "openid profile offline_access"
            }),
            "the CLI starts a device authorization");
        var verification = t.Observe(
            await t.GetAsync(new Uri(started.JsonString("verification_uri_complete")).PathAndQuery),
            "the user opens the verification link: SqlOS binds a device request and sends the browser to the app's UI");
        var requestId = verification.NextUrlParameter("request");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/resolve", new { requestId }),
            "the UI loads the device request");
        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "sign in: device requests skip the consent step and redirect to device approval");
        t.Observe(
            await t.GetAsync(login.JsonString("redirectUrl")),
            "following the redirect returns the browser to the UI's approval view");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { requestId }),
            "approve the device");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI polls and receives tokens");

        await t.ObserveAuditAsync("device approval events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_signed_in_user_approves_a_cli_by_typing_its_user_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        await SignInAsync(t, alice);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));
        var userCode = started.JsonString("user_code");

        var typed = userCode.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);
        t.Scrub(typed, "user-code", "as-typed");

        t.Observe(
            await t.PostJsonAsync($"{Api}/device/resolve", new { userCode }),
            "the UI resolves the typed user code");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode = typed }),
            "approve with the code typed in lower case with a space: the issuer session cookie is the approver");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI polls and receives tokens");

        await t.ObserveAuditAsync("device approval events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/headless/device/deny")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_signed_in_user_denies_a_cli_and_the_cli_is_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        await SignInAsync(t, alice);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));
        var userCode = started.JsonString("user_code");

        t.Observe(
            await t.PostJsonAsync($"{Api}/device/deny", new { userCode }),
            "deny the device");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode }),
            "a denied device request cannot be approved afterwards");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/resolve", new { userCode }),
            "resolving the denied code shows its status");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI polls and is told access was denied");

        await t.ObserveAuditAsync("device denial events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/headless/device/deny")]
    public async Task Device_decisions_need_a_known_user_code_and_approval_needs_a_signed_in_browser()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        await SignInAsync(t, alice);
        var userCode = await StartDeviceAsync(t);
        var anonymous = t.NewBrowser("anonymous");
        var interactiveRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: anonymous);

        t.Observe(
            await anonymous.PostJsonAsync($"{Api}/device/approve", new { userCode }),
            "approve from a browser that is not signed in");
        t.Observe(
            await anonymous.PostJsonAsync($"{Api}/device/resolve", new { userCode = "ZZZZ-ZZZZ" }),
            "resolve a user code that was never issued");
        t.Observe(
            await anonymous.PostJsonAsync($"{Api}/device/resolve", new { }),
            "resolve with no user code and no request");
        t.Observe(
            await anonymous.PostJsonAsync($"{Api}/device/resolve", new { requestId = interactiveRequestId }),
            "resolve an interactive request that is not a device request");
        t.Observe(
            await anonymous.PostJsonAsync($"{Api}/device/deny", new { userCode = "ZZZZ-ZZZZ" }),
            "deny a user code that was never issued");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { }),
            "a signed-in browser, but no user code and no request");

        await t.ObserveAuditAsync("device refusal events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Device_approval_refuses_an_interactive_request_and_cannot_rebind_its_organization()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreatePartnerClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var victim = await t.Setup.CreateOrganizationAsync("victim");
        await SignInAsync(t, alice);
        var request = t.Urls.Authorize(PartnerClientId, PartnerRedirectUri);
        var toConsent = t.Observe(
            await t.GetAsync(request.Url),
            "the signed-in browser authorizes a third-party client: sent to the UI's consent view");
        var requestId = toConsent.NextUrlParameter("request");

        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { requestId, organizationId = victim.Id }),
            "post that interactive request to device approval, naming another organization (#418): refused");
        var approved = t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId, consentToken = toConsent.NextUrlParameter("consentToken") }),
            "the request is unchanged: consent completes it normally");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(approved))),
            "the tokens carry no organization: the refused approval bound nothing");

        await t.ObserveAuditAsync("refused device approval events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/headless/organization/select")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_member_of_two_organizations_approves_a_device_into_the_one_they_pick()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var frank = await t.Setup.CreateUserAsync("frank");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var initech = await t.Setup.CreateOrganizationAsync("initech");
        await t.Setup.AddMembershipAsync(acme, frank);
        await t.Setup.AddMembershipAsync(globex, frank);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));
        var userCode = started.JsonString("user_code");
        await SignInFrankAsync(t, frank, acme);

        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode, organizationId = initech.Id }),
            "approve into an organization frank does not belong to (#418): refused");
        var approve = t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode }),
            "approve without naming one: the UI must ask which organization");
        var selected = t.Observe(
            await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken = approve.JsonString("viewModel.pendingToken"), organizationId = globex.Id }),
            "pick globex: device requests continue to the approval step");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { requestId = QueryRequest(selected.JsonString("redirectUrl")) }),
            "approve the bound request");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI's tokens carry the picked organization");

        await t.ObserveAuditAsync("multi-organization device approval events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/reset")]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    public async Task A_revoked_issuer_cookie_cannot_approve_a_device_but_a_fresh_sign_in_replaces_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        await SignInAsync(t, alice);
        var userCode = await StartDeviceAsync(t);
        var newPassword = t.Unique.Password("alice-new");
        var mailbox = t.NewBrowser("mailbox");
        t.Discard(await mailbox.PostJsonAsync($"{Api}/password/forgot", new { email = alice.Email }));
        t.Observe(
            await mailbox.PostJsonAsync($"{Api}/password/reset", new { token = EmailLinkToken(t, alice.Email), newPassword }),
            "alice resets her password from her mailbox: every issuer session family is revoked");

        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode }),
            "the browser still holds the revoked issuer cookie: it cannot approve the device");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = newPassword }),
            "a fresh password sign-in with the dead cookie present succeeds and replaces the cookie (#434)");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode }),
            "the new cookie approves the device");

        await t.ObserveAuditAsync("reset, refusal, and re-sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/headless/invitations/resolve")]
    public async Task Device_and_invitation_views_report_stored_times_without_a_utc_marker_CurrentBehavior_KnownDefect_325()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitationToken = await HeadlessInvitationScenarios.InviteAsync(t, acme, t.Unique.Email("erin"));
        var userCode = await StartDeviceAsync(t);

        t.Note("Known defect #325: times read back from the database serialize without Z ({datetime:unspecified}), so browsers read UTC as local time.");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/resolve", new { userCode }),
            "the device view's deviceAuthorization.expiresAt");
        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/resolve", new { invitationToken }),
            "the invite view's invitation.createdAt and expiresAt");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/deny")]
    [Covers("POST /sqlos/auth/headless/device/resolve")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Anyone_holding_the_verification_link_can_deny_the_device_without_signing_in()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));
        var verification = t.Discard(await t.GetAsync(new Uri(started.JsonString("verification_uri_complete")).PathAndQuery));
        var requestId = verification.NextUrlParameter("request");

        t.Observe(
            await t.PostJsonAsync($"{Api}/device/deny", new { requestId }),
            "deny the device request from a browser that never signed in");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/resolve", new { requestId }),
            "the denied request is closed");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI polls and is told access was denied");

        await t.ObserveAuditAsync("anonymous denial events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/device/approve")]
    [Covers("POST /sqlos/auth/headless/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_session_that_predates_an_mfa_policy_must_enroll_before_it_approves_a_device()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreateCliClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        await SignInAsync(t, alice);
        await RequireTotpForAllUsersAsync(t);
        var cli = t.NewClient("cli");
        var started = t.Discard(await cli.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));

        var approve = t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { userCode = started.JsonString("user_code") }),
            "approve with a password-only session after the operator required TOTP: the enrollment view instead");
        var step = await Host.Support.Totp.StableCurrentStepAsync();
        var enrolled = t.Observe(
            await t.PostJsonAsync($"{Api}/mfa/totp/enroll/verify", new
            {
                requestId = approve.JsonString("viewModel.requestId"),
                mfaToken = approve.JsonString("viewModel.mfaToken"),
                enrollmentToken = approve.JsonString("viewModel.totpEnrollment.enrollmentToken"),
                code = Host.Support.Totp.Code(approve.JsonString("viewModel.totpEnrollment.secret"), step)
            }),
            "confirm the authenticator: a redirect back to device approval");
        t.Observe(
            await t.PostJsonAsync($"{Api}/device/approve", new { requestId = QueryRequest(enrolled.JsonString("redirectUrl")) }),
            "approve the device with the stepped-up session");
        t.Observe(
            await cli.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["device_code"] = started.JsonString("device_code"),
                ["client_id"] = CliClientId
            }),
            "the CLI receives tokens");

        await t.ObserveAuditAsync("step-up device approval events");
        await t.ApproveAsync();
    }

    /// <summary>Starts a device authorization for the CLI (unrecorded) and returns its user code.</summary>
    internal static async Task<string> StartDeviceAsync(Transcript t)
    {
        var started = t.Discard(await t.NewClient("cli").PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
        {
            ["client_id"] = CliClientId,
            ["scope"] = "openid profile offline_access"
        }));
        return started.JsonString("user_code");
    }

    private static async Task SignInFrankAsync(Transcript t, ScenarioUser frank, ScenarioOrganization organization)
    {
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var login = t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = frank.Email, password = frank.Password }));
        // Picking an organization answers 400 after signing the browser in (see HeadlessOrganizationScenarios);
        // the issuer session cookie is what this precondition needs.
        t.Discard(await t.PostJsonAsync($"{Api}/organization/select", new { pendingToken = login.JsonString("viewModel.pendingToken"), organizationId = organization.Id }));
        await t.SkipAuditAsync();
    }

    private static string QueryRequest(string relativeUrl)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(new Uri("https://sqlos.example.test"), relativeUrl).Query)["request"].ToString();
}
