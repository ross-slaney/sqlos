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
        await CheckAsync(seed.Bob.FgaSubjectId, BehaviorLockAuthorization.ReadPermission, UpgradeData.ChildWorkspace, "the upgrade merged the subject SCIM created for Bob into his user ID's, so a check by the old subject ID finds no subject");
        await CheckAsync(seed.Bob.FgaSubjectId, BehaviorLockAuthorization.ReadPermission, UpgradeData.RootWorkspace, "on the root either");
        await CheckAsync(seed.Bob.Id, BehaviorLockAuthorization.ReadPermission, UpgradeData.ChildWorkspace, "a check by Bob's user ID reads the child through the SCIM group mapping");
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

    /// <summary>
    /// #448: the released package gave each SCIM-provisioned user an FGA subject of SCIM's own
    /// (<c>subj_…</c>). The upgrade merges it into the subject keyed by the user ID, creating that
    /// subject when the host never provisioned one: group memberships and grants move, a grant the
    /// user's subject already holds is dropped, and the directory then manages the merged subject.
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/users")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}/grants")]
    [Covers("POST /__probe/fga/check")]
    [Covers("POST /__probe/fga/filter")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    public async Task Directory_users_fga_subjects_merge_into_their_user_id_subject_at_the_upgrade()
    {
        await using var seeded = await SeededDatabase.CreateAsync(UpgradeData.DirectorySubjectsDataset);
        var seed = seeded.ManifestAs<DirectorySubjectsManifest>();
        await using var t = await Transcript.StartAsync(HostProfiles.Upgrade, options =>
        {
            options.ExistingDatabase = seeded.ConnectionString;
            options.DataProtectionKeysDirectory = seeded.DataProtectionKeysDirectory;
            options.ObserveStartupAudit = true;
        });
        seeded.RegisterWith(t, seed);
        t.Note($"SqlOS {seeded.SeededVersion} seeded the database: its SCIM directory provisioned Bob and Ann into the mapped Engineering group, the host provisioned Ann's subject by her user ID and made it admin on the child workspace, and an operator made Ann's SCIM subject admin on the child and reader on the root.");
        await t.ObserveAuditAsync("events the upgrade wrote");

        var op = t.Operator;
        var probe = t.NewClient("probe");
        var read = BehaviorLockAuthorization.ReadPermission;
        async Task CheckAsync(string subjectId, string permission, string resourceId, string caption)
            => t.Observe(
                await probe.PostJsonAsync("/__probe/fga/check", new { subjectId, permissionKey = permission, resourceId }),
                caption);

        t.Observe(await op.GetAsync("/sqlos/admin/fga/api/users?search=Ann"), "dashboard: Ann has one FGA user subject, the one the host keyed by her user ID");
        t.Observe(await op.GetAsync($"/sqlos/admin/fga/api/subjects/{seed.Ann.Id}"), "it is in Engineering, where her SCIM subject was");
        t.Observe(await op.GetAsync($"/sqlos/admin/fga/api/subjects/{seed.Ann.Id}/grants"), "it holds the admin grant once and the reader grant her SCIM subject held");
        t.Observe(await op.GetAsync($"/sqlos/admin/fga/api/subjects/{seed.Ann.ScimSubjectId}"), "her SCIM subject is gone");
        await CheckAsync(seed.Ann.Id, read, UpgradeData.RootWorkspace, "a check by Ann's user ID reads the root through the reader grant");
        await CheckAsync(seed.Ann.Id, BehaviorLockAuthorization.WritePermission, UpgradeData.ChildWorkspace, "and writes the child through the admin grant");
        t.Observe(await op.GetAsync("/sqlos/admin/fga/api/users?search=Bob"), "dashboard: Bob's subject is keyed by his user ID");
        await CheckAsync(seed.Bob.Id, read, UpgradeData.ChildWorkspace, "a check by Bob's user ID reads the child through the SCIM group mapping");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/filter", new { subjectId = seed.Bob.Id, permissionKey = read }),
            "so does the list filter by his user ID");
        await CheckAsync(seed.Bob.ScimSubjectId, read, UpgradeData.ChildWorkspace, "a check by his SCIM subject finds no subject");

        var directory = t.NewClient("directory");
        t.Observe(
            await directory.SendAsync(
                new HttpMethod("PATCH"),
                $"{UpgradeData.ScimBasePath}/Groups/{seed.ScimGroupId}",
                ScimJson($$"""
                    {
                      "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
                      "Operations": [{ "op": "remove", "path": "members[value eq \"{{seed.Bob.Id}}\"]" }]
                    }
                    """),
                options => options.Bearer(seed.ScimToken)),
            "the directory removes Bob from Engineering");
        await CheckAsync(seed.Bob.Id, read, UpgradeData.ChildWorkspace, "so a check by his user ID no longer reads the child");

        await t.ObserveAuditAsync("events written after the upgrade");
        await t.ApproveAsync();
    }

    private static StringContent ScimJson(string json)
        => new(json, Encoding.UTF8, "application/scim+json");
}
