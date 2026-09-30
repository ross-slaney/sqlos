using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Host.Support;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using SqlOS.BehaviorLock.Infrastructure.Upgrade;

namespace SqlOS.BehaviorLock.Scenarios.Upgrade;

/// <summary>
/// The upgrade gate. The released package (tests/SqlOS.BehaviorLock.UpgradeSeed) seeds a database
/// with every persisted feature in use; then the build under test starts on that database, so
/// SqlOS bootstrap upgrades the schema and reconciles code-owned configuration, and the data the
/// old version wrote must keep working: sessions, refresh tokens, TOTP enrollments, directory
/// tokens and group mappings, FGA grants, pending device codes, and everything the dashboard reads.
/// </summary>
[TestClass]
public sealed class UpgradeScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/mfa/verify")]
    [Covers("GET /scim/v2/Users")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    [Covers("GET /sqlos/admin/calendar/api/connections")]
    public async Task Data_seeded_by_the_released_package_keeps_working_after_the_upgrade()
    {
        await using var seeded = await SeededDatabase.CreateAsync();
        var seed = seeded.Manifest;
        await using var t = await Transcript.StartAsync(HostProfiles.Upgrade, options =>
        {
            options.ExistingDatabase = seeded.ConnectionString;
            options.DataProtectionKeysDirectory = seeded.DataProtectionKeysDirectory;
        });
        seeded.RegisterWith(t);
        t.Note($"The database and data-protection keys were written by the upgrade seed with SqlOS {seeded.SeededVersion}; this host is the build under test.");

        // Sessions and refresh tokens issued before the upgrade.
        var returning = t.NewBrowser("alice-returning");
        returning.SetCookie("sqlos_auth_page", seed.SessionCookie);
        var resume = t.Urls.Authorize(UpgradeData.PortalClientId, UpgradeData.PortalRedirectUri);
        var resumed = t.Observe(await returning.GetAsync(resume.Url), "Alice's hosted session from before the upgrade continues without a sign-in");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", resume.TokenRequest(resumed.NextUrlParameter("code"))), "redeem the code");
        foreach (var token in seed.RefreshTokens)
        {
            t.Observe(
                await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken,
                    ["client_id"] = token.ClientId
                }),
                $"rotate the {token.Label} client's refresh token");
        }

        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = seed.PendingDevice.DeviceCode,
                ["client_id"] = seed.PendingDevice.ClientId
            }),
            "the CLI's device authorization is still pending");

        // The directory keeps working with the token issued before the upgrade.
        var directory = t.NewClient("directory");
        t.Observe(
            await directory.GetAsync(
                $"{UpgradeData.ScimBasePath}/Users?filter={Uri.EscapeDataString($"userName eq \"{UpgradeData.BobEmail}\"")}",
                options => options.Bearer(seed.ScimToken)),
            "find Bob with the existing SCIM token");
        var daveEmail = $"dave@{UpgradeData.Domain}";
        t.Scrub(daveEmail, "email", "dave");
        t.Scrub(daveEmail.ToUpperInvariant(), "email", "DAVE");
        var dave = t.Observe(
            await directory.SendAsync(
                HttpMethod.Post,
                $"{UpgradeData.ScimBasePath}/Users",
                ScimJson($$"""
                    {
                      "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
                      "externalId": "directory-dave",
                      "userName": "{{daveEmail}}",
                      "name": { "givenName": "Dave", "familyName": "Lister" },
                      "emails": [{ "value": "{{daveEmail}}", "primary": true, "type": "work" }],
                      "active": true
                    }
                    """),
                options => options.Bearer(seed.ScimToken)),
            "provision Dave at the domain verified before the upgrade");
        var daveId = dave.JsonString("id");
        t.Scrub(daveId, "usr", "dave");
        t.Observe(
            await directory.SendAsync(
                new HttpMethod("PATCH"),
                $"{UpgradeData.ScimBasePath}/Groups/{seed.ScimGroupId}",
                ScimJson($$"""
                    {
                      "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
                      "Operations": [{ "op": "add", "path": "members", "value": [{ "value": "{{daveId}}" }] }]
                    }
                    """),
                options => options.Bearer(seed.ScimToken)),
            "add Dave to the group the existing mapping targets");

        var daveSubject = t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/fga/api/users?search=Dave"),
            "dashboard: SCIM mirrored Dave into FGA");
        var daveSubjectId = daveSubject.JsonString("data.0.subjectId");
        t.Scrub(daveSubjectId, "subj", "dave");

        // Authorization decisions over grants written before the upgrade.
        var probe = t.NewClient("probe");
        async Task CheckAsync(string subjectId, string permission, string resourceId, string caption)
            => t.Observe(
                await probe.PostJsonAsync("/__probe/fga/check", new { subjectId, permissionKey = permission, resourceId }),
                caption);
        await CheckAsync(seed.Alice.FgaSubjectId, BehaviorLockAuthorization.WritePermission, UpgradeData.ChildWorkspace, "Alice's admin grant on the root workspace reaches the child");
        await CheckAsync(seed.Carol.FgaSubjectId, BehaviorLockAuthorization.ReadPermission, UpgradeData.ChildWorkspace, "Carol's direct reader grant");
        await CheckAsync(seed.Carol.FgaSubjectId, BehaviorLockAuthorization.WritePermission, UpgradeData.ChildWorkspace, "a reader cannot write");
        await CheckAsync(seed.Bob.FgaSubjectId, BehaviorLockAuthorization.ReadPermission, UpgradeData.ChildWorkspace, "Bob reads the child through the SCIM group mapping");
        await CheckAsync(seed.Bob.FgaSubjectId, BehaviorLockAuthorization.ReadPermission, UpgradeData.RootWorkspace, "the mapping grants the child only");
        await CheckAsync(seed.Bob.Id, BehaviorLockAuthorization.ReadPermission, UpgradeData.ChildWorkspace, "a check by Bob's user ID finds no subject: SCIM keys the subjects it creates by their own ID");
        await CheckAsync(daveSubjectId, BehaviorLockAuthorization.ReadPermission, UpgradeData.ChildWorkspace, "the existing mapping grants Dave after the upgrade");

        // A fresh sign-in with the password and the authenticator enrolled before the upgrade.
        var signIn = t.Urls.Authorize(UpgradeData.PortalClientId, UpgradeData.PortalRedirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.Browser.GetAsync(signIn.Url), "open the portal's password sign-in");
        var challenge = t.Observe(
            await t.Browser.SubmitAsync(page.Form("/login/password")
                .With("email", seed.Alice.Email)
                .With("password", seed.Alice.Password!)),
            "the password step asks for the authenticator Alice enrolled before the upgrade");
        var step = await Totp.NextUnusedStepAsync(seed.Alice.LastTotpStep);
        var totp = Totp.Code(seed.Alice.TotpSecret!, step);
        t.Scrub(totp, "totp");
        var verified = t.Observe(
            await t.Browser.SubmitAsync(challenge.Form("/mfa/verify").With("code", totp)),
            "the authenticator code verifies");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", signIn.TokenRequest(verified.NextUrlParameter("code"))), "redeem the code");

        // What the dashboard shows for the data the old version wrote.
        await t.ObserveStateAsync("/sqlos/admin/auth/api/users", "dashboard: users");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{seed.Alice.Id}/sessions", "dashboard: Alice's sessions");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{seed.OrganizationId}", "dashboard: the organization");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{seed.OrganizationId}/memberships", "dashboard: memberships");
        await t.ObserveStateAsync("/sqlos/admin/auth/api/clients", "dashboard: clients");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{seed.OrganizationId}/sso-connections", "dashboard: SAML connections");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{seed.ScimConnectionId}", "dashboard: the SCIM connection");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{seed.ScimConnectionId}/mappings", "dashboard: SCIM group mappings");
        await t.ObserveStateAsync("/sqlos/admin/calendar/api/connections", "dashboard: calendar connections");
        await t.ObserveStateAsync($"/sqlos/admin/fga/api/subjects/{seed.Alice.Id}/grants", "dashboard: Alice's grants");

        await t.ObserveAuditAsync("events written after the upgrade");
        await t.ApproveAsync();
    }

    private static StringContent ScimJson(string json)
        => new(json, Encoding.UTF8, "application/scim+json");
}
