using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Fga;

[TestClass]
public sealed class FgaProbeScenarios
{
    [Scenario]
    [Covers("POST /__probe/fga/subjects")]
    [Covers("POST /__probe/fga/workspaces")]
    [Covers("POST /__probe/fga/grants")]
    [Covers("POST /__probe/fga/check")]
    [Covers("POST /__probe/fga/filter")]
    public async Task Checks_and_list_filters_follow_a_role_grant_on_a_synced_entity()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "user", subjectId = alice.Id, displayName = alice.DisplayName }),
            "provision the user as an FGA subject");
        var alpha = t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { name = "Alpha", id = "alpha" }),
            "saving an ISqlOSResourceEntity syncs its FGA resource");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { name = "Beta", id = "beta" }),
            "a second workspace");
        var alphaResource = alpha.JsonString("workspace.resourceId");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = alice.Id, resourceId = alphaResource, role = BehaviorLockAuthorization.ReaderRole }),
            "grant the reader role on Alpha");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = BehaviorLockAuthorization.ReadPermission, resourceId = alphaResource }),
            "read is allowed on Alpha");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = BehaviorLockAuthorization.WritePermission, resourceId = alphaResource }),
            "write is denied on Alpha");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/filter", new { subjectId = alice.Id, permissionKey = BehaviorLockAuthorization.ReadPermission }),
            "BuildFilterAsync lists only Alpha");

        await t.ApproveAsync();
    }
}
