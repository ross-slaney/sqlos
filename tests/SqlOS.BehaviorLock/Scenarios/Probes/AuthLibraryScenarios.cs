using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Probes;

/// <summary>
/// The <c>SqlOSAuthService</c> members a host calls in-process, through the <c>/__probe/auth</c>
/// routes: direct password sign-in, token validation, refresh, logout, email-code start, device
/// authorization start, invitations, MFA status, and the enabled OIDC providers.
/// </summary>
[TestClass]
public sealed class AuthLibraryScenarios
{
    private const string AppClient = BehaviorLockConstants.AppClientId;
    private const string ApiAudience = BehaviorLockConstants.ApiAudience;

    [Scenario]
    [Covers("POST /__probe/auth/password-login")]
    [Covers("POST /__probe/auth/validate")]
    [Covers("POST /__probe/auth/refresh")]
    [Covers("POST /__probe/auth/logout")]
    public async Task Library_sign_in_issues_tokens_that_validate_refresh_and_log_out()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var probe = t.NewClient("probe");

        var login = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient }),
            "LoginWithPasswordAsync signs a user in for a first-party client");
        var access = login.JsonString("tokens.accessToken");
        var refresh = login.JsonString("tokens.refreshToken");
        t.Observe(await Validate(probe, access, ApiAudience), "ValidateAccessTokenAsync accepts it for the application API");
        t.Observe(await Validate(probe, access, "https://sqlos.example.test/other"), "but not for another audience");
        t.Observe(await Validate(probe, access, " "), "the expected audience is required");
        t.Observe(await Validate(probe, "not-a-jwt", ApiAudience), "a malformed token is not valid");
        t.Observe(
            await Validate(probe, ProbeCalls.WithForgedClaim(access, "sub", bob.Id), ApiAudience),
            "a token edited to name another user fails its signature");

        var rotated = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = refresh }),
            "RefreshAsync rotates the refresh token");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = refresh }),
            "the used token inside the grace window returns the same rotation");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = rotated.JsonString("refreshToken"), clientId = "another-client" }),
            "a refresh for another client is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = "not-a-refresh-token" }),
            "an unknown refresh token is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/logout", new { refreshToken = rotated.JsonString("refreshToken") }),
            "LogoutAsync by refresh token ends the session");
        t.Observe(await Validate(probe, rotated.JsonString("accessToken"), ApiAudience), "its access token stops validating");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = rotated.JsonString("refreshToken") }),
            "and its refresh token is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/logout", new { refreshToken = "not-a-refresh-token" }),
            "logging out an unknown token is a silent no-op");

        await t.ObserveAuditAsync("sign-in and logout events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// A client-credentials token has no session: ValidateAccessTokenAsync accepts it through the
    /// machine client's service account instead.
    /// </summary>
    [Scenario]
    [Covers("POST /__probe/auth/validate")]
    public async Task Service_tokens_validate_through_their_service_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"atlas-worker:{HostProfiles.MachineClientSecret}"));
        var issued = t.Discard(await t.Api.PostFormAsync(
            "/sqlos/auth/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["resource"] = BehaviorLockConstants.ResourceApiAudience,
                ["scope"] = BehaviorLockAuthorization.ReadPermission
            },
            options => options.Header("Authorization", $"Basic {credentials}")));
        if (issued.StatusCode != 200)
        {
            throw new InvalidOperationException($"The client-credentials setup failed: {issued.Describe()} {issued.ResponseBody}");
        }

        t.Note("The atlas-worker machine client obtained a client-credentials token for the resource API (not recorded).");
        var probe = t.NewClient("probe");
        var token = issued.JsonString("access_token");
        t.Observe(await Validate(probe, token, BehaviorLockConstants.ResourceApiAudience), "the service token validates for its audience, with no session or user");
        t.Observe(await Validate(probe, token, BehaviorLockConstants.BillingAudience), "and not for another audience");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/auth/password-login")]
    public async Task Library_sign_in_refuses_bad_input_and_locks_the_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password }),
            "a client is required");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = "no-such-client" }),
            "an unknown client is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = t.Unique.Email("nobody"), password = alice.Password, clientId = AppClient }),
            "an unknown email gets the generic failure");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient, organizationId = acme.Id }),
            "the right password for an organization the user does not belong to");
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            t.Observe(
                await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = "Wrong-Password-1!", clientId = AppClient }),
                $"wrong password, attempt {attempt}");
        }

        await t.ObserveAuditAsync("the failures so far");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = "Wrong-Password-1!", clientId = AppClient }),
            "the fifth wrong password locks the account");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient }),
            "while locked the right password gets the same generic failure");
        t.Note(
            "The lockout's audit events are not locked here: SqlOS lists the locked scopes, and writes one password.login.locked " +
            "event per locked bucket, in the order of the buckets' random IDs (an EF Include with no ORDER BY), so their order differs between runs.");

        await t.ApproveAsync();
    }

    /// <summary>
    /// Attack test from #419 (fixed in 7.2.1): the direct sign-in APIs return tokens to the caller
    /// with no consent screen, so a client that is not first-party is refused before any
    /// credential is checked or code is sent, and the attempt is audited.
    /// </summary>
    [Scenario]
    [Covers("POST /__probe/auth/password-login")]
    [Covers("POST /__probe/auth/email-otp")]
    public async Task Third_party_clients_cannot_sign_users_in_directly()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/clients", new
        {
            clientId = "partner-app",
            name = "Partner App",
            audience = ApiAudience,
            redirectUris = new[] { "https://partner.example.test/callback" },
            allowedScopes = new[] { "openid", "profile", "email" },
            isFirstParty = false
        });
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = "partner-app" }),
            "password sign-in for a third-party client is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = "Wrong-Password-1!", clientId = "partner-app" }),
            "before the password is checked: a wrong one gets the same refusal");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email, clientId = "partner-app" }),
            "an email code for a third-party client is refused and no email is sent");

        await t.ObserveAuditAsync("each refusal is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/auth/password-login")]
    [Covers("GET /__probe/admin/users/{userId}/organizations")]
    [Covers("GET /__probe/admin/users/{userId}/memberships/{organizationId}")]
    [Covers("POST /__probe/auth/mfa-status")]
    public async Task A_member_of_two_organizations_chooses_one()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var beta = await t.Setup.CreateOrganizationAsync("beta");
        var gamma = await t.Setup.CreateOrganizationAsync("gamma");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(beta, alice, "admin");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient }),
            "without an organization the caller must choose one");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient, organizationId = beta.Id }),
            "naming a membership issues tokens for it");
        t.Observe(await probe.GetAsync($"/__probe/admin/users/{alice.Id}/organizations"), "GetUserOrganizationsAsync lists both (the probe orders them by name)");
        t.Observe(await probe.GetAsync($"/__probe/admin/users/{alice.Id}/memberships/{acme.Id}"), "UserHasMembershipAsync for a membership");
        t.Observe(await probe.GetAsync($"/__probe/admin/users/{alice.Id}/memberships/{gamma.Id}"), "and for an organization she does not belong to");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/mfa-status", new { userId = alice.Id }),
            "GetMfaStatusAsync under the default policy");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/mfa-status", new { userId = alice.Id, organizationId = beta.Id }),
            "and for one of her organizations");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Known defect #427 (7.2.1): revoking an organization's sessions matches only the organization
    /// a session started in. A session that started in Acme and refreshed into Beta keeps
    /// Beta tokens that validate after the operator revokes Beta.
    /// </summary>
    [Scenario]
    [Covers("POST /__probe/auth/password-login")]
    [Covers("POST /__probe/auth/refresh")]
    [Covers("POST /__probe/auth/validate")]
    public async Task Revoking_an_organization_misses_sessions_switched_into_it_CurrentBehavior_KnownDefect_427()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var beta = await t.Setup.CreateOrganizationAsync("beta");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(beta, alice);
        var probe = t.NewClient("probe");

        var login = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient, organizationId = acme.Id }),
            "sign in to Acme");
        var switched = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = login.JsonString("tokens.refreshToken"), organizationId = beta.Id }),
            "refresh into Beta");
        var preview = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { organizationId = beta.Id }),
            "the operator previews revoking Beta's sessions: none match");
        t.Observe(
            await t.Operator.PostJsonAsync(
                "/sqlos/admin/auth/api/sessions/revocation",
                new { organizationId = beta.Id, confirm = true, expectedMatchedSessions = preview.Json!["matchedSessions"]!.GetValue<int>() }),
            "executing it finds nothing to revoke");
        t.Observe(await Validate(probe, switched.JsonString("accessToken"), ApiAudience), "the Beta access token still validates");
        var stillBeta = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = switched.JsonString("refreshToken"), organizationId = beta.Id }),
            "and the session keeps refreshing into Beta");
        var acmePreview = t.Observe(
            await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { organizationId = acme.Id }),
            "the session is only found under Acme, where it started");
        t.Observe(
            await t.Operator.PostJsonAsync(
                "/sqlos/admin/auth/api/sessions/revocation",
                new { organizationId = acme.Id, confirm = true, expectedMatchedSessions = acmePreview.Json!["matchedSessions"]!.GetValue<int>() }),
            "revoking Acme's sessions");
        t.Observe(await Validate(probe, stillBeta.JsonString("accessToken"), ApiAudience), "ends the Beta tokens too");

        await t.ObserveAuditAsync("sign-in, refresh, and revocation events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/auth/password-login")]
    [Covers("POST /__probe/auth/logout-all")]
    [Covers("POST /__probe/auth/logout")]
    [Covers("POST /__probe/auth/validate")]
    public async Task Logout_all_ends_every_session_of_the_user()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var probe = t.NewClient("probe");

        var first = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient }),
            "a first session");
        var second = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = alice.Email, password = alice.Password, clientId = AppClient }),
            "a second session");
        var bobs = t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = bob.Email, password = bob.Password, clientId = AppClient }),
            "another user's session");
        t.Observe(await probe.PostJsonAsync("/__probe/auth/logout-all", new { userId = alice.Id }), "LogoutAllAsync for the user");
        t.Observe(await Validate(probe, first.JsonString("tokens.accessToken"), ApiAudience), "the first session's token stops validating");
        t.Observe(await Validate(probe, second.JsonString("tokens.accessToken"), ApiAudience), "so does the second's");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/refresh", new { refreshToken = second.JsonString("tokens.refreshToken") }),
            "their refresh tokens are refused");
        t.Observe(await Validate(probe, bobs.JsonString("tokens.accessToken"), ApiAudience), "the other user's session is untouched");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/logout", new { sessionId = bobs.JsonString("tokens.sessionId") }),
            "LogoutAsync by session ID");
        t.Observe(await Validate(probe, bobs.JsonString("tokens.accessToken"), ApiAudience), "ends that session");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/logout-all", new { userId = "usr_00000000000000000000000000000000" }),
            "LogoutAllAsync for a user that does not exist still succeeds");

        await t.ObserveAuditAsync("logout events, including one for the user that does not exist");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Email-code start: a code for an existing account goes only to the address stored on it
    /// (#422, fixed in 7.2.1), an unknown address gets the same answer and no email, and the
    /// resend cooldown and the per-address hourly limit apply one request at a time. (#424, the
    /// same limits under concurrent requests, is a race this suite cannot replay deterministically.)
    /// </summary>
    [Scenario]
    [Covers("POST /__probe/auth/email-otp")]
    public async Task Email_code_start_sends_to_the_stored_address_and_enforces_its_limits()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email, clientId = AppClient }),
            "RequestEmailOtpAsync emails a code to the account");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email, clientId = AppClient }),
            "asking again inside the cooldown is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email.ToUpperInvariant(), clientId = AppClient, organizationId = "context-b" }),
            "a differently cased address gets the code at the stored address");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = t.Unique.Email("nobody"), clientId = AppClient }),
            "an unknown address gets the same answer and no email");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = "not-an-email", clientId = AppClient }),
            "an invalid address is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = " ", clientId = AppClient }),
            "an address is required");
        for (var context = 3; context <= 5; context++)
        {
            t.Discard(await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email, clientId = AppClient, organizationId = $"context-{context}" }));
        }

        t.Note("Three more codes for the account in other contexts (not recorded): five in the last hour.");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email, clientId = AppClient, organizationId = "context-6" }),
            "the sixth code within the hour is refused");

        await t.ObserveAuditAsync("challenge and rate-limit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/auth/email-otp")]
    [Covers("GET /__probe/auth/providers")]
    public async Task Email_codes_and_oidc_providers_follow_the_configured_credentials()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/email-otp", new { email = alice.Email, clientId = AppClient }),
            "with only passwords enabled, email sign-in is unavailable");
        t.Observe(await probe.GetAsync("/__probe/auth/providers"), "and no OIDC provider is enabled");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /__probe/auth/providers")]
    public async Task The_enabled_oidc_providers_are_listed_by_display_name()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.NewClient("probe").GetAsync("/__probe/auth/providers"), "ListEnabledProvidersAsync");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/auth/device-authorization")]
    public async Task Device_authorization_starts_for_a_device_client_and_binds_its_resource()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/device-authorization", new { clientId = "atlas-cli", scope = "openid profile offline_access" }),
            "StartDeviceAuthorizationAsync issues device and user codes");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/device-authorization", new { clientId = "atlas-cli", scope = "openid", resource = BehaviorLockConstants.ResourceApiAudience }),
            "the client's own audience is accepted as the resource");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/device-authorization", new { clientId = "atlas-cli", scope = "openid", resource = BehaviorLockConstants.BillingAudience }),
            "any other resource is refused as invalid_target");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/device-authorization", new { clientId = "atlas-portal", scope = "openid" }),
            "a client without the device grant is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/device-authorization", new { clientId = "no-such-client", scope = "openid" }),
            "an unknown client is refused");

        await t.ObserveAuditAsync("device authorization events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/auth/invitations")]
    public async Task Invitations_email_an_acceptance_link_and_supersede_older_ones()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var probe = t.NewClient("probe");
        var bob = t.Unique.Email("bob");

        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = acme.Id, email = bob, role = "member" }),
            "CreateEmailInvitationAsync emails an acceptance link");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = acme.Id, email = bob, role = "admin", sendEmail = false }),
            "inviting the same address again supersedes the first invitation; without an email the link is only returned");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = acme.Id, email = bob, role = "member", clientId = AppClient, redirectUri = BehaviorLockConstants.AppRedirectUri }),
            "an invitation can carry the client to continue to");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = acme.Id, email = bob, role = "member", clientId = AppClient, redirectUri = "https://evil.example/callback" }),
            "but only with one of the client's redirect URIs");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = acme.Id, email = bob, role = "member", expiresAt = "2000-01-01T00:00:00Z" }),
            "an expiry in the past is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = acme.Id, email = "not-an-email", role = "member" }),
            "an invalid address is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/invitations", new { organizationId = "org_00000000000000000000000000000000", email = bob, role = "member" }),
            "an unknown organization is refused");

        await t.ObserveAuditAsync("invitation events");
        await t.ApproveAsync();
    }

    private static Task<HttpExchange> Validate(HttpActor probe, string token, string audience)
        => probe.PostJsonAsync("/__probe/auth/validate", new { token, audience });
}
