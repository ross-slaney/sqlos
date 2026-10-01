using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Direct password login through the public account API (<c>POST /sqlos/auth/password/login</c>):
/// a first-party backend or SPA collects the password in its own UI and receives tokens in the
/// JSON response, with no redirect and no consent screen.
/// </summary>
[TestClass]
public sealed class PublicPasswordLoginScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Password_login_returns_tokens_to_a_first_party_client()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new
            {
                email = alice.Email,
                password = alice.Password,
                clientId = BehaviorLockConstants.AppClientId
            }),
            "sign in with email and password; tokens come back in the body");

        await t.ObserveAuditAsync("password sign-in events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "the session the sign-in created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Password_login_failures_answer_with_a_server_error_and_are_audited()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var codeOnly = await t.CreatePasswordlessUserAsync("carol");
        var organization = await t.Setup.CreateOrganizationAsync("acme");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = "Wrong-Password-1!", clientId = BehaviorLockConstants.AppClientId }),
            "a wrong password escapes the endpoint as a server error");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = t.Unique.Email("nobody"), password = "Any-Password-1!", clientId = BehaviorLockConstants.AppClientId }),
            "an unknown email fails the same way");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = codeOnly.Email, password = "Any-Password-1!", clientId = BehaviorLockConstants.AppClientId }),
            "an account without a password fails the same way");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password }),
            "no client id: the request is refused before the password is checked");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "unknown-client" }),
            "an unknown client id: refused before the password is checked");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new
            {
                email = alice.Email,
                password = alice.Password,
                clientId = BehaviorLockConstants.AppClientId,
                organizationId = organization.Id
            }),
            "the right password for an organization the user does not belong to");

        await t.ObserveAuditAsync("failed sign-in events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "no session was created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Five_wrong_passwords_lock_the_account_and_the_right_password_is_then_refused()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = $"Wrong-Password-{attempt}!", clientId = BehaviorLockConstants.AppClientId }),
                $"wrong password, attempt {attempt}");
        }

        await t.ObserveAuditAsync("four failures");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = "Wrong-Password-5!", clientId = BehaviorLockConstants.AppClientId }),
            "wrong password, attempt 5: the email and user buckets lock");
        await t.ObserveAuditAsync("the fifth failure and one lock event per locked bucket", AuditOrder.Content);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "the right password is refused while the account is locked");
        t.Observe(
            await t.Api.PostJsonAsync(
                "/sqlos/auth/password/login",
                new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId },
                options => options.FromAddress("198.51.100.7")),
            "the lock is per account: another address is refused too");

        await t.ObserveAuditAsync("the rejected attempts");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/select-organization")]
    public async Task A_member_of_several_organizations_picks_one_to_finish_signing_in()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var organizations = await t.CreateOrganizationsInIdOrderAsync("acme", "globex");
        var (acme, globex) = (organizations[0], organizations[1]);
        var outsider = await t.Setup.CreateOrganizationAsync("initech");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice, "admin");

        var pending = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "two memberships: the login stops for an organization choice");
        var pendingAuthToken = pending.JsonString("pendingAuthToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/select-organization", new { pendingAuthToken, organizationId = outsider.Id }),
            "choosing an organization the user does not belong to fails");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/select-organization", new { pendingAuthToken, organizationId = globex.Id }),
            "the failed choice consumed the pending token: a valid choice with it fails too");

        var retry = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "sign in again for a fresh pending token");
        var fresh = retry.JsonString("pendingAuthToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/select-organization", new { pendingAuthToken = fresh, organizationId = globex.Id }),
            "choose Globex: tokens for that organization");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/select-organization", new { pendingAuthToken = fresh, organizationId = globex.Id }),
            "the pending token cannot be replayed");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId, organizationId = acme.Id }),
            "naming the organization up front skips the choice");

        await t.ObserveAuditAsync("organization selection events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/select-organization")]
    public async Task Choosing_an_organization_that_requires_mfa_answers_with_a_challenge()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var organizations = await t.CreateOrganizationsInIdOrderAsync("acme", "globex");
        var (acme, globex) = (organizations[0], organizations[1]);
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, alice);
        await t.RequireOrganizationMfaAsync(globex);

        var pending = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "two memberships: the login stops for an organization choice");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/select-organization", new { pendingAuthToken = pending.JsonString("pendingAuthToken"), organizationId = globex.Id }),
            "Globex requires MFA and Alice has no authenticator: the choice answers with an enrollment challenge, not tokens");

        await t.ObserveAuditAsync("the selection is audited although no tokens were issued");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task A_client_that_admits_only_assigned_users_refuses_an_unassigned_user()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.CreateClientAsync("ops-console", "Ops Console", isFirstParty: true, "https://ops.example.test/callback");
        await t.SetApplicationAccessModeAsync("ops-console", "selected_users_groups_roles");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "ops-console" }),
            "the ops console admits selected users only; Alice is not assigned");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = BehaviorLockConstants.AppClientId }),
            "the application's own client admits her");

        await t.ObserveAuditAsync("the access denial and the successful sign-in");
        await t.ApproveAsync();
    }
}
