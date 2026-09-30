using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

[TestClass]
public sealed class HostedPasswordScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Password_sign_in_redeems_the_code_for_tokens()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.GetAsync(request.Url), "open the hosted password sign-in page");

        var login = t.Observe(
            await t.SubmitAsync(page.Form("/login/password")
                .With("email", alice.Email)
                .With("password", alice.Password)),
            "submit email and password");

        var code = login.NextUrlParameter("code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)),
            "redeem the authorization code");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }
}
