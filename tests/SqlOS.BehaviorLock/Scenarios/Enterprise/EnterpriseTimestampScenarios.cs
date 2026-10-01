using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Known defect #325 (7.2.1): a timestamp SqlOS just wrote serializes with a trailing <c>Z</c>, but
/// the same value read back through EF has <c>DateTimeKind.Unspecified</c> and serializes without
/// one, so SCIM clients and the dashboard read it as local time. Transcripts show the two format
/// classes as <c>{datetime:utc-z}</c> and <c>{datetime:unspecified}</c>.
/// </summary>
[TestClass]
public sealed class EnterpriseTimestampScenarios
{
    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("GET /scim/v2/Users/{id}")]
    [Covers("GET /scim/v2/Users")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/token/rotate")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions")]
    public async Task Timestamps_lose_their_utc_marker_once_read_back_CurrentBehavior_KnownDefect_325()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var judy = t.Unique.Email("judy", domain);

        var created = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Users", Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), connection.Token),
            "create Judy: meta.created and meta.lastModified carry Z");
        var id = created.JsonString("id");
        t.Scrub(id, "usr", "judy");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users/{id}?attributes=meta", connection.Token), "read her back: the same instants without Z");
        t.Observe(await directory.GetAsync($"{Scim.Root}/Users?attributes=meta", connection.Token), "the list reads them without Z too");

        var rotated = t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/token/rotate", new { }),
            "rotate the token: tokenRotatedAt carries Z");
        t.ScrubScimToken(rotated.JsonString("token"));
        t.Observe(await t.Operator.GetAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}"), "the connection read back: tokenRotatedAt without Z");

        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions", new { }),
            "issue a setup link: createdAt and expiresAt carry Z");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/sso-portal/sessions?pageSize=1"),
            "the link read back: without Z");

        await t.ApproveAsync();
    }
}
