using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Tokens;

[TestClass]
public sealed class RefreshTokenScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/token")]
    public async Task Refresh_rotates_the_token_and_a_replay_inside_the_grace_window_gets_a_sibling()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);

        var first = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(session.RefreshToken)),
            "redeem the first refresh token");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(first.JsonString("refresh_token"))),
            "redeem the rotated refresh token");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(session.RefreshToken)),
            "replay the first refresh token inside the grace window");

        await t.ObserveAuditAsync("refresh events");
        await t.ApproveAsync();
    }

    private static IEnumerable<KeyValuePair<string, string>> Refresh(string refreshToken)
    {
        yield return new("grant_type", "refresh_token");
        yield return new("refresh_token", refreshToken);
        yield return new("client_id", BehaviorLockConstants.AppClientId);
    }
}
