using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Manual signing-key rotation from the admin API: the key list, the rotation and its audit record,
/// the JWKS that relying parties read, and the runtime effect (tokens signed with the retired key
/// keep validating during the grace window; new tokens carry the new key ID).
/// </summary>
[TestClass]
public sealed class SigningKeyAdminScenarios
{
    private const string KeysRoute = "/sqlos/admin/auth/api/signing-keys";

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/signing-keys")]
    [Covers("POST /sqlos/admin/auth/api/signing-keys/rotate")]
    public async Task Operator_rotates_the_signing_key_and_tokens_signed_before_keep_validating()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);

        t.Observe(
            await t.Operator.GetAsync(KeysRoute, options => options.WithoutCredentials()),
            "without operator credentials the key list is not found");
        t.Observe(
            await t.Operator.PostJsonAsync($"{KeysRoute}/rotate", new { }, options => options.WithoutCredentials()),
            "nor can a key be rotated");
        var before = t.Observe(await t.Operator.GetAsync(KeysRoute), "one active key, created at startup");
        var retiredKid = before.JsonString("keys.0.kid");
        t.Observe(await t.Operator.PostJsonAsync($"{KeysRoute}/rotate", new { }), "rotate the signing key");
        t.Observe(await t.Operator.GetAsync(KeysRoute), "the new key is active and the old one is retired");

        // SqlOS builds the JWKS from an unordered signing-key query (SqlOSCryptoService
        // LoadValidationSigningKeysAsync), so the key order is storage order, which varies per run.
        // Record the published keys retired first instead of the raw, order-dependent response.
        var jwks = t.Discard(await t.Api.GetAsync("/sqlos/auth/.well-known/jwks.json"));
        var published = jwks.Json!["keys"]!.AsArray()
            .OrderBy(key => key!["kid"]!.GetValue<string>() == retiredKid ? 0 : 1)
            .Select(key => $"kid={key!["kid"]} alg={key["alg"]} kty={key["kty"]} use={key["use"]} e={key["e"]}");
        t.ObserveDocument(
            $"GET /sqlos/auth/.well-known/jwks.json answered {jwks.StatusCode} and publishes both keys during the grace window (retired key first)",
            string.Join("\n", published));
        // The validation probe names its input "token"; register the JWT as an access token first.
        t.Scrub(session.AccessToken, "access-token");
        t.Observe(
            await t.NewClient("probe").PostJsonAsync("/__probe/auth/validate", new { token = session.AccessToken, audience = BehaviorLockConstants.ApiAudience }),
            "an access token signed with the retired key still validates");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
                ["client_id"] = BehaviorLockConstants.AppClientId
            }),
            "a refreshed access token is signed with the new key");

        await t.ObserveAuditAsync("the manual rotation is audited");
        await t.ApproveAsync();
    }
}
