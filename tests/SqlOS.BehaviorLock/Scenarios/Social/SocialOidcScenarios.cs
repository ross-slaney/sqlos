using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Social;

[TestClass]
public sealed class SocialOidcScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/login/oidc/{connectionId}")]
    [Covers("GET /sqlos/auth/oidc/callback")]
    public async Task Google_sign_in_provisions_a_user_through_the_upstream_provider()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var email = t.Unique.Email("grace");
        var request = t.Urls.Authorize();

        var page = t.Discard(await t.GetAsync(request.Url));
        var googleStart = page.Form("/login/identify")["requestId"];
        var googleLink = System.Text.RegularExpressions.Regex.Match(
            page.ResponseBody,
            @"href=""(?<href>/sqlos/auth/login/oidc/[^""?]+)\?request=[^""]*""[^>]*data-loading-label=""Connecting to Google""").Groups["href"].Value;

        var toGoogle = t.Observe(
            await t.GetAsync($"{googleLink}?request={googleStart}&email="),
            "start Google sign-in: SqlOS redirects to the provider");
        var callback = t.Observe(
            await t.GetAsync(
                $"/sqlos/auth/oidc/callback?code={Uri.EscapeDataString($"success:{email}:{toGoogle.NextUrlParameter("nonce")}")}" +
                $"&state={Uri.EscapeDataString(toGoogle.NextUrlParameter("state"))}"),
            "the provider redirects back with a code");

        var code = callback.NextUrlParameter("code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)),
            "redeem the authorization code");

        await t.ObserveAuditAsync("social sign-in events");
        await t.ApproveAsync();
    }
}
