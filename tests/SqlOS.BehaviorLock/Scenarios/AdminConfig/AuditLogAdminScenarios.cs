using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The audit-log admin API (<c>/sqlos/admin/audit/api</c>) and its dashboard page. A host records
/// application events through the documented <c>ISqlOSAuditLogService.RecordAsync</c> (the audit
/// probe) with fixed occurrence times, so ordering and date-window filters are exact; the operator
/// then filters, pages, reads, and exports them.
/// </summary>
[TestClass]
public sealed partial class AuditLogAdminScenarios
{
    private const string EventsRoute = "/sqlos/admin/audit/api/events";
    private const string RecordProbe = "/__probe/audit";

    [Scenario]
    [Covers("GET /sqlos/admin/audit/api/events")]
    [Covers("GET /sqlos/admin/audit/{*page}")]
    public async Task Operator_filters_host_recorded_audit_events()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var host = t.NewClient("host");
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");

        t.Observe(
            await host.PostJsonAsync(RecordProbe, new
            {
                action = "invoice.paid",
                source = "billing",
                organizationId = acme.Id,
                applicationKey = BehaviorLockConstants.AppClientId,
                actor = new { type = "user", id = alice.Id, displayName = "Alice" },
                targets = new[] { new { type = "invoice", id = "inv-1001", displayName = "Invoice 1001" } },
                context = new { ipAddress = "198.51.100.7", userAgent = "BillingWorker/1.0", requestId = "req-1001", correlationId = "corr-1001" },
                metadata = new Dictionary<string, object?> { ["amount"] = 42, ["currency"] = "USD", ["apiKey"] = "not-to-be-stored" },
                occurredAt = "2026-01-01T10:00:00Z"
            }),
            "the host records a billing event for Acme; the application key resolves to the client and the API key is redacted");
        t.Observe(
            await host.PostJsonAsync(RecordProbe, new
            {
                action = "invoice.refunded",
                source = "billing",
                organizationId = acme.Id,
                actor = new { type = "service", id = "billing-worker" },
                targets = new[] { new { type = "invoice", id = "inv-1001" } },
                metadata = new Dictionary<string, object?> { ["result"] = "partial" },
                occurredAt = "2026-01-01T10:01:00Z"
            }),
            "a service refunds part of it");
        t.Observe(
            await host.PostJsonAsync(RecordProbe, new
            {
                action = "report.exported",
                source = "application",
                actor = new { type = "user", id = alice.Id },
                targets = new[] { new { type = "report", id = "rpt-7" } },
                metadata = new Dictionary<string, object?> { ["format"] = "csv", ["userPassword"] = "hunter2" },
                idempotencyKey = "export-7",
                occurredAt = "2026-01-01T10:02:00Z"
            }),
            "an application event with an idempotency key");
        t.Observe(
            await host.PostJsonAsync(RecordProbe, new
            {
                action = "report.exported",
                source = "application",
                actor = new { type = "user", id = alice.Id },
                idempotencyKey = "export-7",
                occurredAt = "2026-01-01T10:09:00Z"
            }),
            "replaying the idempotency key returns the first event instead of recording another");
        t.Observe(
            await host.PostJsonAsync(RecordProbe, new
            {
                action = "workspace.deleted",
                source = "application",
                organizationId = globex.Id,
                actor = new { type = "admin" },
                occurredAt = "2026-01-01T10:03:00Z"
            }),
            "an event in another organization");

        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}?source=billing", options => options.WithoutCredentials()),
            "without operator credentials the audit API is not found");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?source=billing"), "by source, newest first");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?organizationId={acme.Id}"), "by organization");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?action=report.exported"), "by action");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?actorType=service"), "by actor type");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?actorId={alice.Id}&source=billing"), "by actor and source");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?targetType=report"), "by target type");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?targetId=inv-1001&actorType=user"), "by target ID and actor type");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?result=partial"), "by a value in the metadata");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?search=rpt-7"), "by free-text search");
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}?application={BehaviorLockConstants.AppClientId}"),
            "by application, matching the application key");
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}?source=billing&occurredAtFrom=2026-01-01T10:00:30Z&occurredAtTo=2026-01-01T10:05:00Z"),
            "by occurrence window");
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}?source=application&from=2026-01-01T10:02:30Z&to=2026-01-01T10:05:00Z"),
            "the from and to aliases");

        t.Observe(await t.Operator.GetAsync("/sqlos/admin/audit/events"), "the dashboard's audit page is the dashboard shell");
        t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/audit/events", options => options.WithoutCredentials()),
            "which is not found without operator credentials");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/audit/api/events")]
    public async Task Audit_event_lists_page_with_cursors_bound_to_their_filters()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var host = t.NewClient("host");
        foreach (var (action, minute) in new[] { ("job.started", 0), ("job.progressed", 1), ("job.finished", 2) })
        {
            t.Discard(await host.PostJsonAsync(RecordProbe, new
            {
                action,
                source = "scheduler",
                actor = new { type = "service", id = "nightly" },
                occurredAt = $"2026-01-01T02:0{minute}:00Z"
            }));
        }

        t.Note("The host recorded job.started, job.progressed, and job.finished one minute apart (source scheduler).");
        var first = t.Observe(await t.Operator.GetAsync($"{EventsRoute}?source=scheduler&pageSize=2&page=1"), "the first page of two");
        var cursor = Uri.EscapeDataString(first.JsonString("nextCursor"));
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?source=scheduler&pageSize=2&cursor={cursor}"), "the cursor continues");
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}?source=billing&pageSize=2&cursor={cursor}"),
            "a cursor is bound to the filters it was issued for");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?source=scheduler&page=2"), "offset paging is refused");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?source=scheduler&cursor=%24%24%24"), "a malformed cursor is refused");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}?source=scheduler&pageSize=1000"), "the page size is capped at 100");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/audit/api/events/{id}")]
    [Covers("GET /sqlos/admin/audit/api/events/export.csv")]
    public async Task Operator_reads_one_audit_event_and_exports_a_window_as_csv()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var host = t.NewClient("host");
        var recorded = t.Discard(await host.PostJsonAsync(RecordProbe, new
        {
            action = "invoice.paid",
            source = "billing",
            actor = new { type = "service", id = "billing-worker", displayName = "Billing, \"nightly\"" },
            targets = new[] { new { type = "invoice", id = "inv-1001", displayName = (string?)"Invoice 1001" }, new { type = "customer", id = "cus-9", displayName = (string?)null } },
            context = new { ipAddress = "198.51.100.7", requestId = "req-1001", correlationId = "corr-1001" },
            metadata = new Dictionary<string, object?> { ["amount"] = 42, ["note"] = "line one\nline two" },
            occurredAt = "2026-01-01T10:00:00Z"
        }));
        t.Discard(await host.PostJsonAsync(RecordProbe, new
        {
            action = "invoice.voided",
            source = "billing",
            occurredAt = "2025-06-01T10:00:00Z"
        }));
        t.Note("The host recorded invoice.paid (2026-01-01, with a quoted display name, two targets, and a multi-line note) and invoice.voided (2025-06-01), both from source billing.");
        var eventId = recorded.JsonString("eventId");

        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/{eventId}", options => options.WithoutCredentials()),
            "without operator credentials an event is not found");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}/{eventId}"), "read one event");
        t.Observe(await t.Operator.GetAsync($"{EventsRoute}/evt_00000000000000000000000000000000"), "an unknown event is not found");
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?source=billing", options => options.WithoutCredentials()),
            "without operator credentials there is no export");

        var export = t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?source=billing&occurredAtFrom=2025-12-31T00:00:00Z&occurredAtTo=2026-01-02T00:00:00Z"),
            "export a window: one CSV row per event, quoted where needed");
        RegisterExportFileName(t, export);
        var wider = t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?source=billing&from=2025-06-01T00:00:00Z&to=2026-01-02T00:00:00Z"),
            "the from and to aliases select a wider window");
        RegisterExportFileName(t, wider);
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?occurredAtFrom=2026-01-02T00:00:00Z&occurredAtTo=2026-01-01T00:00:00Z"),
            "an export window that ends before it starts");
        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?occurredAtFrom=2024-01-01T00:00:00Z&occurredAtTo=2026-01-01T00:00:00Z"),
            "an export window longer than 366 days");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/audit/api/events/{id}")]
    [Covers("GET /sqlos/admin/audit/api/events/export.csv")]
    public async Task Audit_event_times_are_read_back_with_a_utc_marker()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var recorded = t.Observe(
            await t.NewClient("host").PostJsonAsync(RecordProbe, new
            {
                action = "clock.checked",
                source = "clock",
                occurredAt = "2026-01-01T10:00:00Z"
            }),
            "the host records an event at 10:00 UTC");

        t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/{recorded.JsonString("eventId")}"),
            "the API returns occurredAt and ingestedAt with a UTC marker, as the host recorded them (#325)");
        var export = t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?source=clock&occurredAtFrom=2025-12-31T00:00:00Z&occurredAtTo=2026-01-02T00:00:00Z"),
            "the CSV export of the same event writes UTC with a Z");
        RegisterExportFileName(t, export);

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/audit/api/events/export.csv")]
    public async Task Audit_csv_export_does_not_neutralize_spreadsheet_formulas_CurrentBehavior_KnownDefect_236()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        t.Observe(
            await t.NewClient("host").PostJsonAsync(RecordProbe, new
            {
                action = "profile.updated",
                source = "profiles",
                actor = new { type = "user", id = "attacker", displayName = "=HYPERLINK(\"https://evil.example.test\",\"Open\")" },
                targets = new[] { new { type = "profile", id = "+1+cmd|' /C calc'!A0" } },
                metadata = new Dictionary<string, object?> { ["bio"] = "@SUM(1+1)" },
                occurredAt = "2026-01-01T10:00:00Z"
            }),
            "a user-controlled display name, target, and metadata begin with spreadsheet formula characters");
        var export = t.Observe(
            await t.Operator.GetAsync($"{EventsRoute}/export.csv?source=profiles&occurredAtFrom=2025-12-31T00:00:00Z&occurredAtTo=2026-01-02T00:00:00Z"),
            "known defect #236: the export writes them unchanged, so a spreadsheet evaluates them as formulas");
        RegisterExportFileName(t, export);

        await t.ApproveAsync();
    }

    /// <summary>
    /// The export names its file after the export time (<c>sqlos-audit-yyyyMMddHHmmss.csv</c>),
    /// which no scrubber pattern recognizes; name the time stamp instead.
    /// </summary>
    private static void RegisterExportFileName(Transcript t, HttpExchange export)
    {
        var disposition = export.Header("Content-Disposition") ?? string.Empty;
        var stamp = ExportFileStamp().Match(disposition);
        if (stamp.Success)
        {
            t.Scrub(stamp.Groups["stamp"].Value, "export-time", "yyyyMMddHHmmss");
        }
    }

    [GeneratedRegex(@"sqlos-audit-(?<stamp>\d{14})\.csv")]
    private static partial Regex ExportFileStamp();
}
