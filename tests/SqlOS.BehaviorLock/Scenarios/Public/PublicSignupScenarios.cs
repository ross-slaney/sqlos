using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Password signup through the public account API (<c>POST /sqlos/auth/signup</c>). Unlike the
/// other direct-login routes, it maps every <see cref="InvalidOperationException"/> to the public
/// JSON error envelope: allow-listed messages are shown, anything else becomes the generic
/// "request could not be completed" answer with an <c>auth.public_error.mapped</c> audit event.
/// </summary>
[TestClass]
public sealed class PublicSignupScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/signup")]
    public async Task Signup_creates_an_unverified_account_and_signs_it_in()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var dana = t.Unique.Email("dana");
        var erin = t.Unique.Email("erin");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new
            {
                displayName = "Dana",
                email = dana,
                password = t.Unique.Password("dana"),
                clientId = BehaviorLockConstants.AppClientId
            }),
            "sign up without an organization");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new
            {
                displayName = "  Erin  ",
                email = erin,
                password = t.Unique.Password("erin"),
                organizationName = "  Erin Labs  ",
                clientId = BehaviorLockConstants.AppClientId
            }),
            "sign up and create an organization (trimmed names; the user becomes its owner)");

        await t.ObserveAuditAsync("signup events");
        var userId = await FindUserIdAsync(t, dana);
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{userId}", "the new user: its email is not verified");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup")]
    public async Task Signup_rejects_invalid_input_with_public_messages()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var existing = await t.Setup.CreateUserAsync("alice");
        var organization = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("frank");
        var password = t.Unique.Password("frank");
        const string client = BehaviorLockConstants.AppClientId;

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = " ", email, password, clientId = client }),
            "a blank display name");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = new string('d', 201), email, password, clientId = client }),
            "a display name over 200 characters");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Frank", email = "", password, clientId = client }),
            "a missing email");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Frank", email = "frank at example", password, clientId = client }),
            "an email that is not an address: the message is not public");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Frank", email, password = "", clientId = client }),
            "a missing password");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Frank", email, password, organizationName = new string('o', 201), clientId = client }),
            "an organization name over 200 characters");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Frank", email, password, organizationId = organization.Id, clientId = client }),
            "joining an existing organization needs an invitation");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Frank", email, password, clientId = "unknown-client" }),
            "an unknown client id");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Alice", email = existing.Email, password, clientId = client }),
            "an email that already has an account answers generically");

        await t.ObserveAuditAsync("only the non-public failures are audited, with their diagnostic");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(email)}"),
            "no account was created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup")]
    public async Task Signup_through_a_client_with_selected_access_is_rolled_back_and_the_denial_is_audited()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        await t.CreateClientAsync("ops-console", "Ops Console", isFirstParty: true, "https://ops.example.test/callback");
        await t.SetApplicationAccessModeAsync("ops-console", "selected_users_groups_roles");
        var email = t.Unique.Email("gina");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new
            {
                displayName = "Gina",
                email,
                password = t.Unique.Password("gina"),
                clientId = "ops-console"
            }),
            "the ops console admits only assigned users, so the new account may not sign in");

        await t.ObserveAuditAsync("the access denial survives the rolled-back signup");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(email)}"),
            "the signup was rolled back: no account exists");
        await t.ApproveAsync();
    }

    private static async Task<string> FindUserIdAsync(Transcript t, string email)
    {
        var users = t.Discard(await t.Operator.GetAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(email)}"));
        return users.JsonString("data.0.id");
    }
}
