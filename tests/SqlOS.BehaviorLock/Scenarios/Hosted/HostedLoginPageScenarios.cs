using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// <c>GET /login</c> and the identify form's remaining branches, and what the hosted pages and
/// forms do with an invitation token SqlOS cannot resolve: the GET pages and several forms leave
/// the exception unhandled, which Kestrel answers with a bare 500.
/// </summary>
[TestClass]
public sealed class HostedLoginPageScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    public async Task Unknown_status_values_and_unknown_requests_fall_back_to_the_sign_in_page()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.GetAsync("/sqlos/auth/login?status=something-else"), "an unknown status shows the sign-in form");
        t.Observe(
            await t.GetAsync("/sqlos/auth/login?request=not-a-request&email=someone%40example.test"),
            "a request ID SqlOS does not know still renders providers bound to it");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("GET /sqlos/auth/login/phone-otp")]
    [Covers("GET /sqlos/auth/signup/phone-otp")]
    public async Task Values_reflected_from_the_query_are_encoded_on_hosted_pages()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var payload = Uri.EscapeDataString("\"><script>alert(1)</script>");

        t.Observe(await t.GetAsync($"/sqlos/auth/login?email={payload}&request={payload}"), "the sign-in page");
        t.Observe(await t.GetAsync($"/sqlos/auth/login/email-otp?email={payload}"), "the email-code page");
        t.Observe(await t.GetAsync($"/sqlos/auth/login/phone-otp?phoneNumber={payload}"), "the phone-code page");
        t.Observe(await t.GetAsync($"/sqlos/auth/signup/phone-otp?displayName={payload}"), "the phone sign-up page");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("POST /sqlos/auth/login/identify")]
    public async Task Identify_without_an_authorization_request_goes_to_the_preferred_factor()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        var login = t.Observe(await t.GetAsync("/sqlos/auth/login"), "open the sign-in page directly");
        t.Observe(
            await t.SubmitAsync(login.Form("/login/identify").With("email", alice.Email)),
            "identify: the email-code form, with no request and no providers");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/identify")]
    public async Task Identify_answers_the_same_for_an_unknown_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);

        t.Observe(
            await t.SubmitAsync(begun.Page.Form("/login/identify").With("email", t.Unique.Email("nobody"))),
            "identify an address with no account: the same email-code form");

        await t.ObserveAuditAsync("identify writes nothing");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("GET /sqlos/auth/signup/phone-otp")]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("GET /sqlos/auth/login/magic-link")]
    public async Task An_unresolvable_invitation_token_on_a_hosted_page_is_an_unhandled_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options => options.AnswerUnhandledExceptionsAsServerErrors = true);

        t.Observe(await t.GetAsync("/sqlos/auth/login?invitationToken=not-an-invitation"), "the sign-in page");
        t.Observe(
            await t.GetAsync("/sqlos/auth/login?token=not-an-invitation"),
            "any 'token' query value is read as an invitation token too");
        t.Observe(await t.GetAsync("/sqlos/auth/signup?invitation_token=not-an-invitation"), "the sign-up page");
        t.Observe(await t.GetAsync("/sqlos/auth/signup/phone-otp?invitationToken=not-an-invitation"), "the phone sign-up page");
        t.Observe(await t.GetAsync("/sqlos/auth/login/email-otp?invitationToken=not-an-invitation"), "the email-code page");
        t.Observe(await t.GetAsync("/sqlos/auth/login/magic-link?invitationToken=not-an-invitation"), "the magic-link page");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    [Covers("POST /sqlos/auth/signup/submit")]
    public async Task An_unresolvable_invitation_token_in_a_hosted_form_is_an_unhandled_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options => options.AnswerUnhandledExceptionsAsServerErrors = true);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var fields = begun.Page.Form("/login/password").With("invitationToken", "not-an-invitation");

        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/login/identify", fields.Fields).With("email", alice.Email)),
            "identify with the bad invitation inside an authorization request");
        t.Observe(
            await t.SubmitAsync(fields.With("email", alice.Email).With("password", alice.Password)),
            "the password form: the error page itself fails to render");
        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/login/email-otp/start", fields.Fields).With("email", alice.Email)),
            "the email-code form");
        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/login/magic-link/start", fields.Fields).With("email", alice.Email)),
            "the magic-link form");
        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/signup/submit", fields.Fields)
                .With("displayName", "Dana")
                .With("email", t.Unique.Email("dana"))
                .With("password", t.Unique.Password("dana"))),
            "the sign-up form");

        await t.ObserveAuditAsync("nothing is audited");
        await t.ApproveAsync();
    }
}
