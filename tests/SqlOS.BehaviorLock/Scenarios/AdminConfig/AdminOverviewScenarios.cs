using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The auth dashboard's overview reads: the summary counts (<c>/stats</c>) and the legacy audit
/// feed (<c>/audit-events</c>), which returns the latest 200 raw audit rows, startup
/// reconciliation included, without filters or paging.
/// </summary>
[TestClass]
public sealed class AdminOverviewScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/stats")]
    [Covers("GET /sqlos/admin/auth/api/audit-events")]
    public async Task The_overview_counts_every_record_and_the_legacy_feed_lists_raw_audit_rows()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback);

        t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/auth/api/stats", options => options.WithoutCredentials()),
            "without operator credentials the summary is not found");
        t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/auth/api/audit-events", options => options.WithoutCredentials()),
            "and neither is the legacy audit feed");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/auth/api/stats"), "a fresh host: the seeded client and startup audit events");

        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.SignInWithPasswordAsync(alice);

        t.Observe(await t.Operator.GetAsync("/sqlos/admin/auth/api/stats"), "after a user, an organization, and a sign-in");

        // The feed orders by OccurredAt alone (SqlOSAdminService.ListAuditEventsAsync). Startup
        // seeds the built-in email templates in one loop, and rows written in the same clock tick
        // (likely on PostgreSQL, which keeps microseconds) come back in storage order. Record the
        // rows in the API's order with only equal-timestamp runs put in a stable order.
        var feed = t.Discard(await t.Operator.GetAsync("/sqlos/admin/auth/api/audit-events"));
        t.ObserveDocument(
            $"GET /sqlos/admin/auth/api/audit-events answered {feed.StatusCode} ({feed.ResponseContentType}): the raw audit rows, newest first",
            StableAuditFeed(feed.ResponseBody));

        await t.ApproveAsync();
    }

    private static string StableAuditFeed(string body)
    {
        var rows = JsonNode.Parse(body)!.AsArray().Select(row => row!.DeepClone()).ToList();
        var ordered = new JsonArray();
        foreach (var run in GroupAdjacent(rows, row => row["occurredAt"]?.GetValue<string>()))
        {
            foreach (var row in run
                         .OrderBy(row => row["action"]?.GetValue<string>(), StringComparer.Ordinal)
                         .ThenBy(row => GeneratedId.Replace(row.ToJsonString(), "id"), StringComparer.Ordinal))
            {
                ordered.Add(row);
            }
        }

        using var document = JsonDocument.Parse(ordered.ToJsonString());
        var rendered = CanonicalJson.Render(document.RootElement, new TranscriptValueSink(new Scrubber()));

        // Raw metadata columns embed the password-login bucket scopes, which
        // SqlOSPasswordLoginAbuseService builds from an EF Include with no ORDER BY (the same
        // source as CanonicalJson's "resetScopes" rule, which cannot see inside a JSON string).
        return ResetScopes.Replace(rendered, match =>
            match.Groups["prefix"].Value
            + string.Join(",", match.Groups["items"].Value.Split(',').Order(StringComparer.Ordinal))
            + "]");
    }

    private static readonly Regex ResetScopes = new(@"(?<prefix>\\""resetScopes\\"":\[)(?<items>[^\]]*)\]", RegexOptions.CultureInvariant);

    private static IEnumerable<List<JsonNode>> GroupAdjacent(List<JsonNode> rows, Func<JsonNode, string?> key)
    {
        var run = new List<JsonNode>();
        foreach (var row in rows)
        {
            if (run.Count > 0 && !string.Equals(key(run[0]), key(row), StringComparison.Ordinal))
            {
                yield return run;
                run = [];
            }

            run.Add(row);
        }

        if (run.Count > 0)
        {
            yield return run;
        }
    }

    /// <summary>SqlOS prefixed IDs, masked when ordering rows that share a timestamp.</summary>
    private static readonly Regex GeneratedId = new("[a-z][a-z0-9]{0,11}_[0-9a-f]{16,32}", RegexOptions.CultureInvariant);
}
