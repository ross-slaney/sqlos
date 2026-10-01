using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// The operator's user administration: creating users, listing and paging them, the per-user
/// detail, membership, session, application, and consent views, and the operator-triggered
/// password reset email.
/// </summary>
[TestClass]
public sealed class AdminIdentityUserScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/organizations")]
    public async Task Operator_lists_searches_and_pages_users_with_bound_cursors()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        await t.Setup.CreateUserAsync("carol");
        await t.Setup.CreateUserAsync("alice");
        await t.Setup.CreateUserAsync("bob");
        var users = AdminIdentity.Api + "/users";

        t.Observe(await t.Operator.GetAsync(users), "users are listed by display name, ten to a page by default");
        t.Observe(await t.Operator.GetAsync(users + "?search=ob"), "search matches the display name or the default email");
        var first = t.Observe(await t.Operator.GetAsync(users + "?pageSize=2"), "a two-user page returns a next cursor");
        var cursor = Uri.EscapeDataString(first.JsonString("nextCursor"));
        t.Observe(await t.Operator.GetAsync(users + "?pageSize=2&cursor=" + cursor), "the cursor continues after the last row");
        t.Observe(await t.Operator.GetAsync(users + "?pageSize=2&page=1"), "page=1 is accepted as the first window");
        t.Observe(await t.Operator.GetAsync(users + "?page=2"), "deeper offset pages are rejected");
        t.Observe(await t.Operator.GetAsync(users + "?search=ob&cursor=" + cursor), "a cursor is bound to the filters it was issued with");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/organizations?cursor=" + cursor), "and to the list that issued it");
        var unsupported = Base64Url("{\"v\":2,\"s\":\"auth.users\",\"f\":\"\",\"k\":[\"Alice\"]}");
        t.Scrub(unsupported, "crafted-cursor");
        t.Observe(await t.Operator.GetAsync(users + "?cursor=" + unsupported), "a cursor of an unknown version is not supported");
        t.Observe(await t.Operator.GetAsync(users + "?cursor=not-a-cursor"), "an undecodable cursor is invalid");
        var oversized = new string('A', 4097);
        t.Scrub(oversized, "oversized-cursor");
        t.Observe(await t.Operator.GetAsync(users + "?cursor=" + oversized), "a cursor longer than the encoded limit is invalid");
        t.Observe(await t.Operator.GetAsync(users + "?pageSize=0"), "a page size below one is raised to one");
        t.Observe(await t.Operator.GetAsync(users + "?pageSize=500"), "a page size above one hundred is capped at one hundred");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task An_operator_created_user_signs_in_only_with_the_password_the_operator_set()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var withPassword = t.Unique.Email("pat");
        var withoutPassword = t.Unique.Email("nopass");
        var blankPassword = t.Unique.Email("blank");

        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/users", new { displayName = "Pat", email = withPassword, password = t.Unique.Password("pat") }),
            "create a user with a password");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/users", new { displayName = "No Password", email = withoutPassword }),
            "create a user without one");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/users", new { displayName = "Blank Password", email = blankPassword, password = "   " }),
            "a blank password is treated as no password");
        await t.ObserveAuditAsync("user creation writes no audit event (see the #415 known-defect scenario)");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = withPassword, password = t.Unique.Password("pat"), clientId = BehaviorLockConstants.AppClientId }),
            "the password the operator set signs the user in");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = withoutPassword, password = t.Unique.Password("pat"), clientId = BehaviorLockConstants.AppClientId }),
            "a user created without a password cannot sign in with one (the public login lets the failure escape as a 500)");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = blankPassword, password = "   ", clientId = BehaviorLockConstants.AppClientId }),
            "nor can the blank-password user with the blank password");
        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users")]
    public async Task A_taken_or_invalid_email_fails_user_creation_with_a_server_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var users = AdminIdentity.Api + "/users";
        var dana = t.Unique.Email("dana");

        t.Observe(await t.Operator.PostJsonAsync(users, new { displayName = "Dana", email = dana }), "create a user");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Dana Again", email = dana }),
            "the same email again escapes as an unhandled InvalidOperationException ('already exists'): 500");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Dana Shouting", email = "  " + dana.ToUpperInvariant() + "  " }),
            "the same mailbox in upper case with surrounding spaces is taken too");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "No At", email = "not-an-email" }),
            "an address without @ is rejected as invalid, also as an unhandled 500");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Long S", email = "\u017Fam@example.test" }),
            "an ASCII look-alike character (long s) is rejected as invalid");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Missing Email" }),
            "a missing email is invalid");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { email = t.Unique.Email("nameless") }),
            "a missing display name reaches the database");
        t.Observe(await t.Operator.GetAsync(users), "what was created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users")]
    public async Task A_lookalike_domain_is_a_different_mailbox_while_its_idna_form_is_the_same_one()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var users = AdminIdentity.Api + "/users";

        t.Note("The 7.2.1 fix for #422 keys accounts by a canonical email (NFC, IDNA ASCII domain, ASCII-only case folding) and confirms matches ordinally, so a look-alike never selects another person's account.");
        t.Observe(await t.Operator.PostJsonAsync(users, new { displayName = "Bob", email = "bob@business.example.test" }), "create bob at business.example.test");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Look Alike", email = "bob@busine\u00DF.example.test" }),
            "busine\u00DF is another domain, so this is a new user, not a duplicate");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Look Alike Ascii", email = "bob@XN--BUSINE-GTA.example.test" }),
            "the IDNA ASCII form of busine\u00DF, in any letter case, is the look-alike user's mailbox, so it is taken (500)");
        t.Observe(
            await t.Operator.PostJsonAsync(users, new { displayName = "Bob Shouting", email = "BOB@BUSINESS.EXAMPLE.TEST" }),
            "and the upper-case ASCII address is bob's (500)");
        t.Observe(await t.Operator.GetAsync(users + "?search=bob"), "exactly two users, each with the address as written");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/applications")]
    public async Task User_detail_memberships_sessions_and_applications_follow_the_user()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice, "admin");
        await AdminIdentity.SignInAsync(t, alice);
        await AdminIdentity.SignInAsync(t, alice, browser: t.NewBrowser("second-device"));
        var user = $"{AdminIdentity.Api}/users/{alice.Id}";

        t.Observe(await t.Operator.GetAsync(user), "the user with active membership and session counts");
        t.Observe(await t.Operator.GetAsync(user + "/memberships"), "the user's memberships");
        var sessions = t.Observe(await t.Operator.GetAsync(user + "/sessions?pageSize=1"), "the newest session first, one to a page");
        t.Observe(
            await t.Operator.GetAsync(user + "/sessions?pageSize=1&cursor=" + Uri.EscapeDataString(sessions.JsonString("nextCursor"))),
            "the older session on the next page");
        t.Observe(await t.Operator.GetAsync(user + "/applications"), "applications the user can reach in each organization, and recent sessions");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{bob.Id}/applications"), "a user without memberships is evaluated without an organization");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/users/usr_missing/memberships"), "an unknown user has no memberships");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/users/usr_missing/sessions"), "and no sessions");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/users/usr_missing/applications"), "but the applications view reports it as not found");
        t.Observe(
            await t.Operator.GetAsync(AdminIdentity.Api + "/users/usr_missing"),
            "and the user detail lets 'User not found.' escape as a 500");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users/{userId}/password-reset-email")]
    [Covers("POST /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Operator_sends_a_password_reset_email_to_the_stored_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var nopass = (await t.Setup.OperatorPostAsync(AdminIdentity.Api + "/users", new { displayName = "Nopass", email = t.Unique.Email("nopass") })).JsonString("id");
        string Reset(string userId) => $"{AdminIdentity.Api}/users/{userId}/password-reset-email";

        t.Observe(await t.Operator.PostJsonAsync(Reset(alice.Id), new { }), "send the reset email with the hosted reset page link");
        var token = ResetToken(t.LatestEmailTo(alice.Email).TextBody);
        t.Observe(
            await t.Operator.PostJsonAsync(Reset(bob.Id), new { resetUrlTemplate = "https://app.example.test/reset?token={token}" }),
            "a trusted template places the token where {token} is");
        t.Observe(
            await t.Operator.PostJsonAsync(Reset(bob.Id), new { resetUrlTemplate = "https://app.example.test/account/reset?source=admin" }),
            "a template without {token} gets the token appended as a query parameter");
        t.Observe(
            await t.Operator.PostJsonAsync(Reset(bob.Id), new { resetUrlTemplate = "http://localhost:5173/reset/{token}" }),
            "plain HTTP is allowed only for a loopback host");
        t.Observe(
            await t.Operator.PostJsonAsync(Reset(bob.Id), new { resetUrlTemplate = "http://app.example.test/reset?token={token}" }),
            "plain HTTP to another host is rejected and the send is audited as failed");
        t.Observe(
            await t.Operator.PostJsonAsync(Reset(bob.Id), new { resetUrlTemplate = "https://{token}.example.test/reset" }),
            "the token may not appear in the URL authority");
        t.Observe(
            await t.Operator.PostJsonAsync(Reset(bob.Id), new { resetUrlTemplate = "https://user:pass@app.example.test/reset?token={token}" }),
            "nor may the URL carry user information");
        t.Observe(await t.Operator.PostJsonAsync(Reset(nopass), new { }), "a user without a password cannot be sent a reset");
        t.Observe(await t.Operator.PostJsonAsync(Reset("usr_missing"), new { }), "an unknown user is reported as not found");
        await t.ObserveAuditAsync("reset deliveries and failures");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token, newPassword = t.Unique.Password("alice-new") }),
            "the emailed token resets alice's password");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = t.Unique.Password("alice-new"), clientId = BehaviorLockConstants.AppClientId }),
            "and the new password signs alice in");
        await t.ObserveAuditAsync("reset completion and sign-in");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/grants")]
    [Covers("POST /sqlos/admin/auth/api/users/{userId}/grants/{grantId}/revoke")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Operator_revokes_a_user_consent_grant_and_the_client_must_ask_again()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var aliceBrowser = t.NewBrowser("alice-browser");
        var aliceSession = await AdminIdentity.SignInWithConsentAsync(t, alice, AdminIdentity.PartnerClientId, AdminIdentity.PartnerRedirectUri, aliceBrowser);
        await AdminIdentity.SignInWithConsentAsync(t, bob, AdminIdentity.PartnerClientId, AdminIdentity.PartnerRedirectUri);
        var grants = $"{AdminIdentity.Api}/users/{alice.Id}/grants";

        var listed = t.Observe(await t.Operator.GetAsync(grants), "alice's remembered consent for the partner client");
        var grantId = listed.JsonString("data.0.id");
        var bobGrantId = t.Discard(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{bob.Id}/grants")).JsonString("data.0.id");
        t.Observe(
            await t.Operator.PostJsonAsync($"{grants}/{bobGrantId}/revoke", new { }),
            "bob's grant cannot be revoked through alice");
        t.Observe(await t.Operator.PostJsonAsync($"{grants}/{grantId}/revoke", new { }), "revoke alice's grant");
        t.Observe(await t.Operator.PostJsonAsync($"{grants}/{grantId}/revoke", new { }), "revoking it again is refused");
        t.Observe(await t.Operator.PostJsonAsync($"{grants}/grant_missing/revoke", new { }), "an unknown grant is not found");
        t.Observe(await t.Operator.GetAsync(grants), "alice has no active grants left");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{bob.Id}/grants"), "bob's grant is untouched");
        await t.ObserveAuditAsync("the revocation is audited once");

        var silent = t.Urls.Authorize(AdminIdentity.PartnerClientId, AdminIdentity.PartnerRedirectUri, extra: new Dictionary<string, string?> { ["prompt"] = "none" });
        t.Observe(
            await aliceBrowser.GetAsync(silent.Url),
            "a silent authorization from alice's browser now needs consent again");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(aliceSession.RefreshToken, AdminIdentity.PartnerClientId)),
            "the refresh token issued under the revoked grant still refreshes");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task Admin_reads_return_database_timestamps_without_a_utc_marker_CurrentBehavior_KnownDefect_325()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);

        t.Note("Known defect #325, recorded as it behaves today: create responses serialize the in-memory UTC value with Z, while every value read back from the database has no zone ({datetime:unspecified}).");
        var user = t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/users", new { displayName = "Alice", email = t.Unique.Email("alice") }),
            "the create response carries Z");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{user.JsonString("id")}"), "the same user read back has no zone");
        t.Observe(await t.Operator.GetAsync(AdminIdentity.Api + "/users"), "nor does the list");
        var organization = t.Observe(await t.Operator.PostJsonAsync(AdminIdentity.Api + "/organizations", new { name = "Acme" }), "an organization's create response carries Z");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/{organization.JsonString("id")}"), "read back, it has no zone");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/organizations/{organization.JsonString("id")}/memberships", new { userId = user.JsonString("id"), role = "member" }),
            "a membership's create response carries Z");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{user.JsonString("id")}/memberships"), "read back, it has no zone");
        await t.ApproveAsync();
    }

    private static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ResetToken(string? text)
    {
        var match = Regex.Match(text ?? string.Empty, @"[?&]token=(?<token>[A-Za-z0-9_\-%]+)");
        return match.Success
            ? WebUtility.UrlDecode(match.Groups["token"].Value)
            : throw new InvalidOperationException($"No reset token in the email: {text}");
    }
}
