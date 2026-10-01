using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Password reset: <c>POST /sqlos/auth/password/forgot</c> (and its alias
/// <c>/password/reset-email</c>) always answers generically and emails a link to eligible accounts;
/// the link opens the hosted page <c>GET /sqlos/auth/password/reset</c>, whose form posts to
/// <c>/password/reset/submit</c> (hosted-form antiforgery applies); an application with its own UI
/// posts the token to the JSON route <c>POST /sqlos/auth/password/reset</c> instead.
/// </summary>
[TestClass]
public sealed class PublicPasswordResetScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/password/forgot")]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task A_reset_link_opens_the_hosted_page_that_sets_a_new_password()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var newPassword = t.Unique.Password("alice-new");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = Client }),
            "ask for a reset link; the answer is generic");
        var token = t.LatestEmailLinkToken(alice.Email);
        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"),
            "open the emailed link");
        var mismatch = t.Observe(
            await t.SubmitAsync(page.BrowserForm("reset/submit").With("newPassword", newPassword).With("confirmPassword", newPassword + "x")),
            "passwords that do not match: the page is rendered again with the error");
        t.Observe(
            await t.SubmitAsync(mismatch.BrowserForm("reset/submit").With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "retrying from that page: its relative form action resolves to .../password/reset/reset/submit, which is not a SqlOS route");
        var retry = t.Observe(
            await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"),
            "reopen the emailed link");
        t.Observe(
            await t.SubmitAsync(retry.BrowserForm("reset/submit").With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "matching passwords: the password is updated");
        var reopened = t.Observe(
            await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"),
            "the used link still renders the form");
        t.Observe(
            await t.SubmitAsync(reopened.BrowserForm("reset/submit").With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "but submitting it again fails");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "the old password no longer signs in");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = newPassword, clientId = Client }),
            "the new one does");

        await t.ObserveAuditAsync("reset events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}", "completing the reset proved the mailbox: the email is verified");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/reset-email")]
    [Covers("POST /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/token/refresh")]
    [Covers("POST /__probe/auth/validate")]
    public async Task Resetting_through_the_json_api_ends_existing_sessions_and_older_links()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.PasswordLoginAsync(alice);
        var newPassword = t.Unique.Password("alice-new");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset-email", new { email = alice.Email }),
            "ask for a reset email (the alias route)");
        var older = t.LatestEmailLinkToken(alice.Email);
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset-email", new { email = alice.Email }),
            "ask again: a new link");
        var newer = t.LatestEmailLinkToken(alice.Email);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token = older, newPassword }),
            "the older link was invalidated by the newer one");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token = newer, newPassword }),
            "the newer link resets the password");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token = newer, newPassword }),
            "it cannot be used twice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/token/refresh", new { refreshToken = session["tokens"]!["refreshToken"]!.GetValue<string>() }),
            "the session from before the reset cannot refresh");
        await t.ObserveAccessTokenValidationAsync(
            session["tokens"]!["accessToken"]!.GetValue<string>(),
            "and its access token no longer validates");

        await t.ObserveAuditAsync("reset events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task A_blank_new_password_is_refused_before_the_link_is_spent()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var newPassword = t.Unique.Password("alice-new");
        t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = Client }));
        var token = t.LatestEmailLinkToken(alice.Email);
        await t.SkipAuditAsync();

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token, newPassword = string.Empty }),
            "an empty new password is refused before the link is spent; this route answers every refused reset unhandled (#456), and 7.2.1 stored the empty password");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/reset", new { token, newPassword }),
            "the same link then sets a real password");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = string.Empty, clientId = Client }),
            "an empty password does not sign in");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = newPassword, clientId = Client }),
            "the new password does");

        await t.ObserveAuditAsync("reset and sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/forgot")]
    public async Task Reset_requests_answer_generically_and_are_throttled_per_address()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var codeOnly = await t.CreatePasswordlessUserAsync("carol");
        var nobody = t.Unique.Email("nobody");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = codeOnly.Email, clientId = Client }),
            "an account without a password: the generic answer, no email");
        for (var request = 1; request <= 5; request++)
        {
            t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = nobody, clientId = Client }),
                $"an unknown address, request {request}: the generic answer, no email");
        }

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = nobody, clientId = Client }),
            "the sixth request for the address this hour is throttled, with the same generic answer");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = "  " }),
            "a blank address escapes the endpoint");

        await t.ObserveAuditAsync("requests record eligibility, and the throttled one its retry time");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    public async Task The_reset_form_needs_the_pages_antiforgery_proof()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var newPassword = t.Unique.Password("alice-new");
        t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = Client }));
        await t.SkipAuditAsync();
        var token = t.LatestEmailLinkToken(alice.Email);

        var page = t.Observe(await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"), "open the reset page");
        var form = page.BrowserForm("reset/submit").With("newPassword", newPassword).With("confirmPassword", newPassword);
        t.Observe(
            await t.SubmitAsync(form.Without("__RequestVerificationToken")),
            "a post without the form's request token");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithOrigin("https://attacker.example.test")),
            "a cross-site post");
        t.Observe(
            await t.SubmitAsync(form, options => options.WithoutCookies()),
            "a post without the antiforgery cookie");
        t.Observe(
            await t.GetAsync("/sqlos/auth/password/reset"),
            "the page without a token renders an empty token field");
        t.Observe(
            await t.SubmitAsync(form),
            "the rejected posts did not consume the link: the genuine post resets the password");

        await t.ObserveAuditAsync("only the completed reset is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("GET /sqlos/auth/email/verify")]
    public async Task Hostile_link_tokens_are_not_reflected_as_markup()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        const string hostile = "\"><script>alert(1)</script><input name=\"newPassword\" value=\"x";

        t.Observe(
            await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(hostile)}"),
            "a crafted reset link: the token stays an encoded value of the hidden field");
        t.Observe(
            await t.GetAsync($"/sqlos/auth/email/verify?token={Uri.EscapeDataString(hostile)}"),
            "a crafted verification link: the page shows a generic failure and never echoes the token");

        await t.ObserveAuditAsync("the verification failure is audited with its diagnostic");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    public async Task Signing_in_again_after_a_reset_in_the_same_browser_starts_a_new_session()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var newPassword = t.Unique.Password("alice-new");
        await t.Setup.SignInWithPasswordAsync(alice);

        t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email, clientId = Client }));
        await t.SkipAuditAsync();
        var token = t.LatestEmailLinkToken(alice.Email);
        var page = t.Observe(await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"), "the signed-in browser opens the reset link");
        t.Observe(
            await t.SubmitAsync(page.BrowserForm("reset/submit").With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "the reset revokes the browser's AuthPage session; its cookie stays behind");

        var deadCookie = t.Browser.Cookies!.GetCookies(new Uri(BehaviorLockConstants.PublicOrigin))["sqlos_auth_page"]?.Value
            ?? throw new InvalidOperationException("The browser has no AuthPage session cookie to replay.");
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var signIn = t.Observe(
            await t.GetAsync(request.Url),
            "the same browser starts a new authorization: the dead session shows the sign-in page and deletes the cookie");
        t.Observe(
            await t.SubmitAsync(
                signIn.Form("/login/password").With("email", alice.Email).With("password", newPassword),
                options => options.Cookie($"sqlos_auth_page={deadCookie}")),
            "signing in with the new password while still presenting the dead cookie is not blocked (#434): a new session starts");

        await t.ObserveAuditAsync("reset and sign-in events");
        await t.ApproveAsync();
    }
}
