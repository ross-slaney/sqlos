using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The settings admin API: security lifetimes, global MFA, organization MFA policy, and AuthPage
/// and auth-email branding, including the ownership rules between code seeds and the dashboard.
/// Security, global MFA, and organization MFA validation failures escape their endpoints (a
/// production server answers 500); AuthPage and email branding failures are handled as 400.
/// Single-application and application-description hosts seed AuthPage and email branding as
/// code-owned, so no profile reaches the dashboard's AuthPage takeover path; the explicit-wiring
/// profile leaves email branding system-owned, which the dashboard claims on its first edit.
/// </summary>
[TestClass]
public sealed class SettingsAdminScenarios
{
    private const string SecurityRoute = "/sqlos/admin/auth/api/settings/security";
    private const string MfaRoute = "/sqlos/admin/auth/api/settings/mfa";
    private const string AuthPageRoute = "/sqlos/admin/auth/api/settings/auth-page";
    private const string EmailRoute = "/sqlos/admin/auth/api/settings/email";
    private const string MfaStatusProbe = "/__probe/auth/mfa-status";

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/security")]
    [Covers("PUT /sqlos/admin/auth/api/settings/security")]
    [Covers("GET /sqlos/admin/auth/api/signing-keys")]
    public async Task Operator_updates_session_and_signing_key_lifetimes()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);

        t.Observe(
            await t.Operator.GetAsync(SecurityRoute, options => options.WithoutCredentials()),
            "without operator credentials the security settings are not found");
        t.Observe(await t.Operator.GetAsync(SecurityRoute), "the default lifetimes");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, new
            {
                refreshTokenLifetimeMinutes = 20160,
                sessionIdleTimeoutMinutes = 1440,
                sessionAbsoluteLifetimeMinutes = 20160,
                signingKeyRotationIntervalDays = 30,
                signingKeyGraceWindowDays = 5,
                signingKeyRetiredCleanupDays = 5,
                refreshTokenGraceWindowSeconds = 0
            }),
            "shorten every lifetime and turn the refresh-token grace window off with 0");
        t.Observe(await t.Operator.GetAsync(SecurityRoute), "the stored lifetimes");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/auth/api/signing-keys"), "the signing-key view reports the new rotation schedule");

        await t.ObserveAuditAsync("security setting changes write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/api/settings/security")]
    public async Task Invalid_security_settings_escape_as_server_errors()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);

        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, Security(refreshTokenLifetimeMinutes: 0)),
            "a non-positive minute value");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, Security(signingKeyRotationIntervalDays: -1)),
            "a non-positive day value");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, Security(signingKeyRotationIntervalDays: 7, signingKeyGraceWindowDays: 7)),
            "a grace window as long as the rotation interval");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, Security(signingKeyGraceWindowDays: 7, signingKeyRetiredCleanupDays: 6)),
            "retired-key cleanup before the grace window ends");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, Security(refreshTokenGraceWindowSeconds: -1)),
            "a negative refresh-token grace window");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, Security(refreshTokenGraceWindowSeconds: 601)),
            "a refresh-token grace window longer than the access-token lifetime");
        t.Observe(
            await t.Operator.PutJsonAsync(SecurityRoute, new { }),
            "an empty body binds every value to zero");
        await t.ObserveStateAsync(SecurityRoute, "the settings are unchanged");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/mfa")]
    [Covers("PUT /sqlos/admin/auth/api/settings/mfa")]
    public async Task Operator_requires_mfa_for_everyone_and_the_sign_in_policy_follows()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var probe = t.NewClient("probe");

        t.Observe(
            await t.Operator.GetAsync(MfaRoute),
            "by default MFA is available but not required, system-owned, and reported as not editable although the first dashboard edit claims it");
        t.Observe(await probe.PostJsonAsync(MfaStatusProbe, new { userId = alice.Id }), "Alice needs no second factor");
        t.Observe(
            await t.Operator.PutJsonAsync(MfaRoute, new
            {
                enabled = true,
                totpEnabled = true,
                userSelfEnrollmentEnabled = true,
                recoveryCodesEnabled = true,
                requireForAllUsers = true,
                requireForOwnersAndAdmins = true,
                requiredRoles = new[] { " Owner ", "admin", "OWNER", " " },
                availableFactors = new[] { "TOTP", "sms", "recovery_code" }
            }),
            "require MFA for everyone: the dashboard takes ownership, roles are normalized, unknown factors are dropped");
        t.Observe(await probe.PostJsonAsync(MfaStatusProbe, new { userId = alice.Id }), "Alice must now enroll a second factor");
        t.Observe(
            await t.Operator.PutJsonAsync(MfaRoute, new
            {
                enabled = true,
                totpEnabled = true,
                userSelfEnrollmentEnabled = false,
                recoveryCodesEnabled = false,
                requireForAllUsers = false,
                requireForOwnersAndAdmins = false,
                requiredRoles = Array.Empty<string>(),
                availableFactors = new[] { "sms" }
            }),
            "no supported factor falls back to TOTP and no roles fall back to owner and admin");
        t.Observe(await t.Operator.GetAsync(MfaRoute), "the stored MFA settings");

        await t.ObserveAuditAsync("MFA setting changes write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/mfa")]
    [Covers("PUT /sqlos/admin/auth/api/settings/mfa")]
    public async Task Code_seeded_mfa_settings_accept_only_the_emergency_enabled_switch()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback, options =>
            options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedMfaPolicy(mfa =>
            {
                mfa.Enabled = true;
                mfa.TotpEnabled = true;
                mfa.RequireForOwnersAndAdmins = true;
            }));

        var seeded = t.Observe(await t.Operator.GetAsync(MfaRoute), "the MFA seed owns the settings");
        t.Observe(
            await t.Operator.PutJsonAsync(MfaRoute, Mfa(seeded, enabled: false)),
            "switching MFA off changes only the enabled flag, which a code-owned policy allows");
        t.Observe(
            await t.Operator.PutJsonAsync(MfaRoute, Mfa(seeded, enabled: false, requireForAllUsers: true)),
            "any other change to the code-owned policy escapes as a server error that names its owner");
        t.Observe(await t.Operator.GetAsync(MfaRoute), "MFA stays switched off and otherwise as seeded");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/mfa-policy")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}/mfa-policy")]
    public async Task Operator_sets_an_organization_mfa_policy()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.Setup.AddMembershipAsync(acme, alice, "member");
        var probe = t.NewClient("probe");
        var route = $"/sqlos/admin/auth/api/organizations/{acme.Id}/mfa-policy";

        t.Observe(await t.Operator.GetAsync(route), "without a policy the organization inherits the global MFA settings");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Slug}/mfa-policy"),
            "the policy can be read by organization slug");
        t.Observe(await probe.PostJsonAsync(MfaStatusProbe, new { userId = alice.Id, organizationId = acme.Id }), "Alice needs no second factor in Acme");
        t.Observe(
            await t.Operator.PutJsonAsync(route, new
            {
                isEnabled = true,
                requireMfaForAllUsers = true,
                requireMfaForOwnersAndAdmins = true,
                userSelfEnrollmentEnabled = true,
                recoveryCodesEnabled = false,
                requiredRoles = new[] { "Billing", "billing" },
                availableFactors = new[] { "totp" }
            }),
            "require MFA for every Acme member");
        t.Observe(await probe.PostJsonAsync(MfaStatusProbe, new { userId = alice.Id, organizationId = acme.Id }), "Alice must enroll a second factor in Acme");
        t.Observe(await probe.PostJsonAsync(MfaStatusProbe, new { userId = alice.Id }), "outside Acme the global settings still apply");
        t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/auth/api/organizations/org_00000000000000000000000000000000/mfa-policy"),
            "reading the policy of an unknown organization escapes as a server error");
        t.Observe(
            await t.Operator.PutJsonAsync("/sqlos/admin/auth/api/organizations/org_00000000000000000000000000000000/mfa-policy", new
            {
                isEnabled = true,
                requireMfaForAllUsers = true,
                requireMfaForOwnersAndAdmins = true,
                userSelfEnrollmentEnabled = true,
                recoveryCodesEnabled = true
            }),
            "writing the policy of an unknown organization escapes as a server error");

        await t.ObserveAuditAsync("organization MFA policy changes write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/auth-page")]
    [Covers("PUT /sqlos/admin/auth/api/settings/auth-page")]
    [Covers("GET /sqlos/admin/auth/api/settings/email")]
    [Covers("PUT /sqlos/admin/auth/api/settings/email")]
    public async Task Code_owned_branding_is_read_only_in_the_dashboard()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.Operator.GetAsync(AuthPageRoute), "the application description and app.Brand own the AuthPage branding");
        t.Observe(
            await t.Operator.PutJsonAsync(AuthPageRoute, AuthPage(layout: "grid")),
            "an unknown layout is rejected before ownership is checked");
        t.Observe(
            await t.Operator.PutJsonAsync(AuthPageRoute, AuthPage()),
            "a valid edit is rejected because code owns the branding");
        t.Observe(await t.Operator.GetAsync(EmailRoute), "the application description owns the auth email branding");
        t.Observe(
            await t.Operator.PutJsonAsync(EmailRoute, Email()),
            "an edit is rejected because code owns the email branding");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/settings/email")]
    [Covers("PUT /sqlos/admin/auth/api/settings/email")]
    [Covers("GET /sqlos/admin/auth/api/settings/auth-page")]
    [Covers("PUT /sqlos/admin/auth/api/settings/auth-page")]
    public async Task Operator_takes_over_email_branding_that_code_does_not_own()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.LegacyHost);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(await t.Operator.GetAsync(EmailRoute), "without an email seed the branding is system-owned and editable");
        t.Observe(
            await t.Operator.PutJsonAsync(EmailRoute, Email(primaryColor: "red;}body{background:url(https://evil.example.test)}")),
            "a value that is not a CSS color is rejected");
        t.Observe(
            await t.Operator.PutJsonAsync(EmailRoute, Email(applicationName: " ")),
            "a blank application name is rejected");
        t.Observe(
            await t.Operator.PutJsonAsync(EmailRoute, Email()),
            "the first dashboard edit claims the email branding");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = "legacy-web" }),
            "a password-reset email carries the new branding");
        t.Observe(await t.Operator.GetAsync(AuthPageRoute), "SeedAuthPage owns the AuthPage branding in this host");
        t.Observe(
            await t.Operator.PutJsonAsync(AuthPageRoute, AuthPage()),
            "so the dashboard cannot edit it");

        t.Note("This host maps the auth server itself, which withdraws the admin audit API, so no audit section is recorded (see LegacyWiringScenarios).");
        await t.ApproveAsync();
    }

    private static object Security(
        int refreshTokenLifetimeMinutes = 43200,
        int sessionIdleTimeoutMinutes = 1440,
        int sessionAbsoluteLifetimeMinutes = 20160,
        int signingKeyRotationIntervalDays = 30,
        int signingKeyGraceWindowDays = 5,
        int signingKeyRetiredCleanupDays = 10,
        int refreshTokenGraceWindowSeconds = 30)
        => new
        {
            refreshTokenLifetimeMinutes,
            sessionIdleTimeoutMinutes,
            sessionAbsoluteLifetimeMinutes,
            signingKeyRotationIntervalDays,
            signingKeyGraceWindowDays,
            signingKeyRetiredCleanupDays,
            refreshTokenGraceWindowSeconds
        };

    private static object Mfa(Infrastructure.Transcripts.HttpExchange current, bool enabled, bool? requireForAllUsers = null)
    {
        var json = current.Json!;
        return new
        {
            enabled,
            totpEnabled = json["totpEnabled"]!.GetValue<bool>(),
            userSelfEnrollmentEnabled = json["userSelfEnrollmentEnabled"]!.GetValue<bool>(),
            recoveryCodesEnabled = json["recoveryCodesEnabled"]!.GetValue<bool>(),
            requireForAllUsers = requireForAllUsers ?? json["requireForAllUsers"]!.GetValue<bool>(),
            requireForOwnersAndAdmins = json["requireForOwnersAndAdmins"]!.GetValue<bool>(),
            requiredRoles = json["requiredRoles"]!.AsArray().Select(role => role!.GetValue<string>()).ToArray(),
            availableFactors = json["availableFactors"]!.AsArray().Select(factor => factor!.GetValue<string>()).ToArray()
        };
    }

    private static object AuthPage(string layout = "stacked")
        => new
        {
            logoBase64 = "data:image/png;base64,iVBORw0KGgo=",
            primaryColor = "#0EA5E9",
            accentColor = "rgb(15, 23, 42)",
            backgroundColor = "transparent",
            layout,
            pageTitle = " Welcome back ",
            pageSubtitle = "Sign in to continue",
            enablePasswordSignup = false,
            enabledCredentialTypes = new[] { "password", "Password", " " }
        };

    private static object Email(string applicationName = " Behavior Lock Mail ", string primaryColor = "#16A34A")
        => new
        {
            applicationName,
            logoBase64 = " data:image/png;base64,iVBORw0KGgo= ",
            primaryColor,
            accentColor = "hsl(222, 47%, 11%)",
            backgroundColor = "#F8FAFC"
        };
}
