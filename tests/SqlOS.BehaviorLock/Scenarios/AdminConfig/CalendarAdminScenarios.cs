using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Calendar connections: the SqlOS-owned connect callback (<c>/sqlos/auth/calendar/callback</c>),
/// the calendar admin API (<c>/sqlos/admin/calendar/api</c>), and its dashboard page. A host starts
/// the connect flow through the documented <c>SqlOSCalendarService.StartConnectAsync</c> (the
/// calendar probe); the fake Google upstream answers the code exchange, refreshes, and calendar
/// reads (<c>FakeCalendarUpstream</c>).
/// </summary>
[TestClass]
public sealed class CalendarAdminScenarios
{
    private const string ConnectionsRoute = "/sqlos/admin/calendar/api/connections";
    private const string CallbackRoute = "/sqlos/auth/calendar/callback";
    private const string ReturnUri = BehaviorLockConstants.PublicOrigin + "/calendar/connected";

    /// <summary>
    /// The Google account the fake provider signs in (its codes are <c>success:{email}</c>). It is
    /// fixed rather than the SqlOS user's per-run address: inside an encoded code value the address
    /// follows <c>%3A</c>, where no scrubber pattern can isolate it.
    /// </summary>
    private const string GoogleAccount = "alice@calendar.example.test";

    [Scenario]
    [Covers("GET /sqlos/auth/calendar/callback")]
    [Covers("GET /sqlos/admin/calendar/api/connections")]
    [Covers("GET /sqlos/admin/calendar/api/connections/{connectionId}")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/sync")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/refresh")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/disconnect")]
    [Covers("GET /sqlos/admin/calendar/api/summary")]
    [Covers("GET /sqlos/admin/calendar/{*page}")]
    public async Task A_user_connects_a_google_calendar_and_an_operator_syncs_refreshes_and_disconnects_it()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var alice = await t.Setup.CreateUserAsync("alice");
        var google = await GoogleConnectionAsync(t);

        var started = t.Observe(
            await t.NewClient("host").PostJsonAsync("/__probe/calendar/connect", new
            {
                oidcConnectionId = google,
                mode = "ReadPull",
                returnUri = ReturnUri,
                userId = alice.Id,
                displayName = "Alice's calendar",
                loginHintEmail = alice.Email
            }),
            "the host starts a read-pull calendar connection for Alice");
        var connected = t.Observe(
            await t.GetAsync(Callback(started, $"success:{GoogleAccount}")),
            "Google returns to the SqlOS callback, which stores the connection and returns to the app");
        var connectionId = connected.NextUrlParameter("calendarConnectionId");

        t.Observe(
            await t.Operator.GetAsync($"{ConnectionsRoute}/{connectionId}", options => options.WithoutCredentials()),
            "without operator credentials the calendar admin API is not found");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/calendar/api/summary"), "the summary counts one active connection");
        t.Observe(await t.Operator.GetAsync(ConnectionsRoute), "the connection list");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}/{connectionId}"), "no calendar is enrolled before the first sync");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/sync", new { }),
            "the first sync enrolls the primary calendar and pulls its events");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/sync", new { }),
            "the next sync is incremental, from the provider's sync token");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}/{connectionId}"), "the detail shows per-calendar sync health");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/refresh", new { }),
            "force a token refresh");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/disconnect", new { }),
            "disconnect: the stored tokens are cleared");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/disconnect", new { }),
            "disconnecting again changes nothing");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/refresh", new { }),
            "a disconnected connection cannot be refreshed");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/sync", new { }),
            "or synchronized");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?includeRevoked=false"), "the active-only list excludes it");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?search=Alice"), "a search still finds it");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/calendar/api/summary"), "the summary after the disconnect");
        t.Observe(await t.Operator.GetAsync("/sqlos/admin/calendar/connections"), "the dashboard's calendar page is the dashboard shell");

        await t.ObserveAuditAsync("the connection lifecycle");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/calendar/callback")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/sync")]
    [Covers("GET /sqlos/admin/calendar/api/connections/{connectionId}")]
    public async Task An_organization_microsoft_calendar_syncs_through_graph_delta_links()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var microsoft = await ProviderConnectionAsync(t, "Microsoft");
        var started = t.Observe(
            await t.NewClient("host").PostJsonAsync("/__probe/calendar/connect", new
            {
                oidcConnectionId = microsoft,
                mode = "TwoWay",
                returnUri = ReturnUri,
                organizationId = acme.Id
            }),
            "the host starts a two-way Microsoft calendar connection owned by Acme");
        var connected = t.Observe(
            await t.GetAsync(Callback(started, "success:calendar@acme.example.test")),
            "Microsoft returns to the SqlOS callback");
        var connectionId = connected.NextUrlParameter("calendarConnectionId");

        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/sync", new { }),
            "the first sync enrolls the default calendar and reads a Graph delta window");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/sync", new { }),
            "the next sync follows the stored delta link: one event removed, one moved");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}/{connectionId}"), "the organization's connection and its sync health");

        await t.ObserveAuditAsync("the organization's calendar lifecycle");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/calendar/callback")]
    public async Task Calendar_connect_callback_failures_return_to_the_app_or_render_an_error()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var alice = await t.Setup.CreateUserAsync("alice");
        var google = await GoogleConnectionAsync(t);
        var host = t.NewClient("host");

        async Task<HttpExchange> StartAsync(string mode = "ReadPull")
            => t.Discard(await host.PostJsonAsync("/__probe/calendar/connect", new
            {
                oidcConnectionId = google,
                mode,
                returnUri = ReturnUri,
                userId = alice.Id,
                loginHintEmail = alice.Email
            }));

        t.Observe(await t.GetAsync(CallbackRoute), "a callback without state renders an error page");
        t.Observe(await t.GetAsync($"{CallbackRoute}?state=not-a-state&code=anything"), "a callback with an unknown state renders an error page");

        var denied = await StartAsync();
        t.Observe(
            await t.GetAsync(QueryHelpers.AddQueryString(CallbackRoute, new Dictionary<string, string?>
            {
                ["state"] = State(denied),
                ["error"] = "access_denied",
                ["error_description"] = "The user denied access."
            })),
            "a provider error returns to the app with the provider's description");
        t.Observe(
            await t.GetAsync(Callback(denied, $"success:{GoogleAccount}")),
            "the state is single use, even after an error");

        var noCode = await StartAsync();
        t.Observe(await t.GetAsync($"{CallbackRoute}?state={Uri.EscapeDataString(State(noCode))}"), "a callback without a code returns to the app with an error");

        var badCode = await StartAsync();
        t.Observe(await t.GetAsync(Callback(badCode, "bad-code")), "a code the provider rejects returns to the app with the exchange error");

        var noRefresh = await StartAsync();
        t.Observe(
            await t.GetAsync(Callback(noRefresh, $"norefresh:{GoogleAccount}")),
            "a read-pull connection without a refresh token is refused");

        await t.ObserveAuditAsync("connect starts and failed completions");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/calendar/api/connections/{connectionId}")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/sync")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/refresh")]
    [Covers("POST /sqlos/admin/calendar/api/connections/{connectionId}/disconnect")]
    [Covers("GET /sqlos/admin/calendar/api/connections")]
    public async Task Connection_only_calendars_neither_sync_nor_refresh_without_a_refresh_token()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var alice = await t.Setup.CreateUserAsync("alice");
        var google = await GoogleConnectionAsync(t);
        var started = t.Discard(await t.NewClient("host").PostJsonAsync("/__probe/calendar/connect", new
        {
            oidcConnectionId = google,
            mode = "ConnectionOnly",
            returnUri = ReturnUri,
            userId = alice.Id
        }));
        var connected = t.Observe(
            await t.GetAsync(Callback(started, $"norefresh:{GoogleAccount}")),
            "a connection-only calendar connects without a refresh token");
        var connectionId = connected.NextUrlParameter("calendarConnectionId");
        const string unknown = "cal_00000000000000000000000000000000";

        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}/{connectionId}"), "it holds only an access token");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/sync", new { }),
            "connection-only calendars do not synchronize events");
        t.Observe(
            await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{connectionId}/refresh", new { }),
            "and cannot be refreshed without a refresh token");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}/{unknown}"), "an unknown connection");
        t.Observe(await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{unknown}/sync", new { }), "syncing an unknown connection");
        t.Observe(await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{unknown}/refresh", new { }), "refreshing an unknown connection");
        t.Observe(await t.Operator.PostJsonAsync($"{ConnectionsRoute}/{unknown}/disconnect", new { }), "disconnecting an unknown connection");
        t.Observe(await t.Operator.GetAsync($"{ConnectionsRoute}?page=2"), "offset paging is refused");

        await t.ObserveAuditAsync("the connection, and no events for refused operations");
        await t.ApproveAsync();
    }

    private static Task<string> GoogleConnectionAsync(Transcript t) => ProviderConnectionAsync(t, "Google");

    private static async Task<string> ProviderConnectionAsync(Transcript t, string providerType)
    {
        var providers = t.Discard(await t.NewClient("setup").GetAsync("/__probe/auth/providers"));
        return providers.Json!.AsArray()
            .Single(provider => provider!["providerType"]!.GetValue<string>() == providerType)!["connectionId"]!
            .GetValue<string>();
    }

    private static string State(HttpExchange started)
        => QueryHelpers.ParseQuery(new Uri(started.JsonString("authorizationUrl")).Query)["state"].ToString();

    private static string Callback(HttpExchange started, string code)
        => QueryHelpers.AddQueryString(CallbackRoute, new Dictionary<string, string?> { ["state"] = State(started), ["code"] = code });
}
