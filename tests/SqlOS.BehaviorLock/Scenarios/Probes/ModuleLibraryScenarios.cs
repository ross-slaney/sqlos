using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Probes;

/// <summary>
/// The companion-module services hosts call in-process, through the probes: the audit log
/// (<c>ISqlOSAuditLogService.RecordAsync</c>), transactional email
/// (<c>ISqlOSTransactionalEmailService</c>), and calendar connections (<c>SqlOSCalendarService</c>).
/// </summary>
[TestClass]
public sealed class ModuleLibraryScenarios
{
    [Scenario]
    [Covers("POST /__probe/audit")]
    public async Task The_audit_service_records_application_events_once_per_idempotency_key()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Modules);
        var alice = await t.Setup.CreateUserAsync("alice");
        var probe = t.NewClient("probe");
        var invoicePaid = new
        {
            action = "invoice.paid",
            userId = alice.Id,
            actor = new { type = "user", id = alice.Id, displayName = "Alice" },
            targets = new[] { new { type = "invoice", id = "inv-1001", displayName = "Invoice 1001" } },
            context = new { ipAddress = "198.51.100.23", userAgent = "billing-worker/1.0", requestId = "request-1001" },
            metadata = new Dictionary<string, object?>
            {
                ["amount"] = 4200,
                ["currency"] = "USD",
                ["webhookSecret"] = "whsec-not-for-the-log",
                ["gateway"] = new Dictionary<string, object?> { ["api_key"] = "key-not-for-the-log", ["region"] = "eu" }
            },
            idempotencyKey = "invoice-1001-paid"
        };

        t.Observe(await probe.PostJsonAsync("/__probe/audit", invoicePaid), "RecordAsync stores an application event, redacting secret-looking metadata");
        t.Observe(await probe.PostJsonAsync("/__probe/audit", invoicePaid), "the same idempotency key returns the stored event");
        t.Observe(
            await probe.PostJsonAsync("/__probe/audit", invoicePaid with { action = "invoice.refunded" }),
            "the same key under another action is a separate event");
        t.Observe(
            await probe.PostJsonAsync("/__probe/audit", new { action = "report.exported", source = "reports", occurredAt = "2026-01-02T03:04:05Z" }),
            "an event with its own source and occurrence time and no actor");
        t.Observe(await probe.PostJsonAsync("/__probe/audit", new { action = " " }), "an action is required");

        await t.ObserveAuditAsync("the recorded events as the audit API returns them");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/email/send")]
    [Covers("POST /__probe/email/preview")]
    public async Task Transactional_email_renders_and_sends_a_template()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Modules);
        var probe = t.NewClient("probe");
        var bob = t.Unique.Email("bob");
        var variables = new Dictionary<string, object?>
        {
            ["applicationName"] = "Behavior Lock",
            ["logoBase64"] = "",
            ["logoImageDisplay"] = "none",
            ["logoTextDisplay"] = "block",
            ["organizationName"] = "Acme & Sons",
            ["maskedEmail"] = "bo***@example.test",
            ["role"] = "member",
            ["acceptUrl"] = "https://sqlos.example.test/accept?invite=fixture",
            ["expiresInDays"] = "7",
            ["primaryColor"] = "#1d4ed8",
            ["accentColor"] = "#0f172a",
            ["backgroundColor"] = "#f8fafc"
        };
        var incomplete = variables.Where(variable => variable.Key is not ("acceptUrl" or "role")).ToDictionary();

        t.Observe(
            await probe.PostJsonAsync("/__probe/email/preview", new { templateKey = "auth.invitation", to = bob, variables }),
            "PreviewAsync renders the active template");
        t.Observe(
            await probe.PostJsonAsync("/__probe/email/send", new { templateKey = "auth.invitation", to = bob, variables, idempotencyKey = "invite-bob" }),
            "SendAsync renders, sends, and records the delivery");
        t.Observe(
            await probe.PostJsonAsync("/__probe/email/send", new { templateKey = "auth.invitation", to = bob, variables, idempotencyKey = "invite-bob" }),
            "the same idempotency key returns the first delivery without sending again");
        t.Observe(
            await probe.PostJsonAsync("/__probe/email/send", new { templateKey = "auth.invitation", to = bob, variables = incomplete }),
            "a template variable left out is refused by name, and nothing is sent");
        t.Observe(
            await probe.PostJsonAsync("/__probe/email/send", new { templateKey = "no.such-template", to = bob, variables }),
            "an unknown template is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/email/send", new { templateKey = "auth.invitation", to = "not-an-email", variables }),
            "the recipient is not validated: an address that is not one is sent to as given");
        t.Observe(
            await probe.PostJsonAsync("/__probe/email/preview", new { templateKey = "no.such-template", to = bob, variables }),
            "previewing an unknown template is refused");

        await t.ObserveAuditAsync("delivery events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/calendar/connect")]
    [Covers("GET /__probe/calendar/connections")]
    public async Task Calendar_connect_starts_at_the_provider_and_lists_connections()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Modules);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var probe = t.NewClient("probe");
        var providers = t.Discard(await probe.GetAsync("/__probe/auth/providers"));
        var google = ProviderId(providers, "Google");
        var microsoft = ProviderId(providers, "Microsoft");
        const string returnUri = BehaviorLockConstants.PublicOrigin + "/settings/calendar";

        var started = t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = google, mode = "ReadPull", returnUri, userId = alice.Id, loginHintEmail = alice.Email }),
            "StartConnectAsync for a user returns the Google authorization URL");
        t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = microsoft, mode = "ConnectionOnly", returnUri, organizationId = acme.Id, displayName = "Acme rooms" }),
            "and for an organization, the Microsoft one");
        t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = google, mode = "ReadPull", returnUri, userId = alice.Id, organizationId = acme.Id }),
            "a connection belongs to a user or an organization, not both");
        t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = google, mode = "ReadPull", returnUri }),
            "nor neither");
        t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = google, mode = "ReadPull", returnUri = "settings/calendar", userId = alice.Id }),
            "the return URI must be absolute");
        t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = google, mode = "ReadPull", returnUri, userId = "usr_00000000000000000000000000000000" }),
            "the user must exist");
        t.Observe(
            await probe.PostJsonAsync("/__probe/calendar/connect", new { oidcConnectionId = "oidc_00000000000000000000000000000000", mode = "ReadPull", returnUri, userId = alice.Id }),
            "the OIDC connection must exist");

        var authorizationUrl = new Uri(started.JsonString("authorizationUrl"));
        var state = QueryHelpers.ParseQuery(authorizationUrl.Query)["state"].ToString();
        var callback = t.Discard(await t.NewBrowser("provider-redirect").GetAsync(
            $"/sqlos/auth/calendar/callback?code={Uri.EscapeDataString("success:" + alice.Email)}&state={Uri.EscapeDataString(state)}"));
        if (callback.StatusCode != 302)
        {
            throw new InvalidOperationException($"Completing the calendar connection failed: {callback.Describe()} {callback.ResponseBody}");
        }

        t.Note("Google redirected back to the calendar callback with an authorization code; the connection completed (not recorded).");
        t.Observe(await probe.GetAsync($"/__probe/calendar/connections?userId={alice.Id}"), "ListConnectionsAsync for the user");
        t.Observe(await probe.GetAsync($"/__probe/calendar/connections?organizationId={acme.Id}"), "the organization has none yet");

        await t.ObserveAuditAsync("calendar connect events");
        await t.ApproveAsync();
    }

    private static string ProviderId(HttpExchange providers, string displayName)
        => providers.Json!.AsArray()
            .Single(provider => provider!["displayName"]!.GetValue<string>() == displayName)!["connectionId"]!.GetValue<string>();
}
