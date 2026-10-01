using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>Social and custom OIDC sign-in started from the headless UI.</summary>
[TestClass]
public sealed class HeadlessProviderScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/provider/start")]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Google_sign_in_starts_from_the_headless_ui_and_provisions_the_user()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var email = t.Unique.Email("grace");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);
        var google = await ProviderConnectionIdAsync(t, requestId, "Google");

        var toGoogle = t.Observe(
            await t.PostJsonAsync($"{Api}/provider/start", new { requestId, connectionId = google, email }),
            "start Google sign-in with the typed email as a login hint: a redirect to the provider");
        var providerUrl = toGoogle.JsonString("redirectUrl");
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(providerUrl).Query);
        var callback = t.Observe(
            await t.GetAsync(
                $"/sqlos/auth/oidc/callback?code={Uri.EscapeDataString($"success:{email}:{parameters["nonce"]}")}" +
                $"&state={Uri.EscapeDataString(parameters["state"].ToString())}"),
            "the provider redirects back with a code: SqlOS provisions the user and redirects with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(callback.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("provider sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/provider/start")]
    [Covers("GET /sqlos/auth/oidc/callback")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_custom_oidc_provider_signs_in_with_mapped_claims_and_an_unknown_connection_is_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var email = t.Unique.Email("olga");
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["login_hint"] = email });
        var requestId = await OpenAuthorizeAsync(t, request);
        var custom = await ProviderConnectionIdAsync(t, requestId, "Custom");

        t.Observe(
            await t.PostJsonAsync($"{Api}/provider/start", new { requestId, connectionId = "oidc_00000000000000000000000000000000" }),
            "start a provider connection that does not exist");
        var toProvider = t.Observe(
            await t.PostJsonAsync($"{Api}/provider/start", new { requestId, connectionId = custom }),
            "start the custom provider without an email: the request's login hint is forwarded");
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(toProvider.JsonString("redirectUrl")).Query);
        var callback = t.Observe(
            await t.GetAsync(
                $"/sqlos/auth/oidc/callback?code={Uri.EscapeDataString($"success:{email}:{parameters["nonce"]}")}" +
                $"&state={Uri.EscapeDataString(parameters["state"].ToString())}"),
            "the custom provider redirects back: its mapped claims provision the user");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(callback.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("custom provider events");
        await t.ApproveAsync();
    }

    /// <summary>The connection ID the request's view model lists for <paramref name="providerType"/>.</summary>
    internal static async Task<string> ProviderConnectionIdAsync(Transcript t, string requestId, string providerType)
    {
        var loaded = t.Discard(await t.GetAsync($"{Api}/requests/{requestId}"));
        return (loaded.Json?["providers"] as JsonArray)?
            .FirstOrDefault(provider => provider?["providerType"]?.GetValue<string>() == providerType)?["connectionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The request lists no {providerType} provider: {loaded.Preview()}");
    }
}
