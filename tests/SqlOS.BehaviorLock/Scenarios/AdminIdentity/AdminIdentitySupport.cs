using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Surface-local helpers for the admin identity API catalog (<c>AdminIdentityEndpoints</c>): the
/// route table the sweeps walk, the scenario option that answers unhandled exceptions the way a
/// production server does, and setup steps these scenarios share. Nothing here is recorded unless
/// a scenario observes it.
/// </summary>
internal static class AdminIdentity
{
    public const string Api = "/sqlos/admin/auth/api";

    /// <summary>The multi-app profile's third-party client, which asks for consent.</summary>
    public const string PartnerClientId = "atlas-partner";

    public const string PartnerRedirectUri = "https://partner.example.test/callback";

    /// <summary>
    /// Every route <c>AdminIdentityEndpoints</c> maps, with a concrete path for sweeps. Bodies are
    /// <c>{}</c> where the endpoint binds one, so binding succeeds and the operator check decides.
    /// </summary>
    public static IReadOnlyList<AdminRoute> Routes { get; } =
    [
        new("GET", "/users", "/users"),
        new("GET", "/users/{userId}", "/users/usr_missing"),
        new("GET", "/users/{userId}/memberships", "/users/usr_missing/memberships"),
        new("GET", "/users/{userId}/sessions", "/users/usr_missing/sessions"),
        new("GET", "/users/{userId}/applications", "/users/usr_missing/applications"),
        new("GET", "/users/{userId}/grants", "/users/usr_missing/grants"),
        new("POST", "/users/{userId}/grants/{grantId}/revoke", "/users/usr_missing/grants/grant_missing/revoke"),
        new("POST", "/users/{userId}/password-reset-email", "/users/usr_missing/password-reset-email", HasBody: true),
        new("POST", "/users", "/users", HasBody: true),
        new("GET", "/organizations", "/organizations"),
        new("GET", "/organizations/{organizationId}", "/organizations/org_missing"),
        new("GET", "/organizations/{organizationId}/applications", "/organizations/org_missing/applications"),
        new("POST", "/organizations", "/organizations", HasBody: true),
        new("PUT", "/organizations/{organizationId}", "/organizations/org_missing", HasBody: true),
        new("GET", "/memberships", "/memberships"),
        new("GET", "/organizations/{organizationId}/memberships", "/organizations/org_missing/memberships"),
        new("GET", "/organizations/{organizationId}/invitations", "/organizations/org_missing/invitations"),
        new("POST", "/organizations/{organizationId}/invitations", "/organizations/org_missing/invitations", HasBody: true),
        new("POST", "/invitations/{invitationId}/resend", "/invitations/inv_missing/resend"),
        new("POST", "/invitations/{invitationId}/revoke", "/invitations/inv_missing/revoke", HasBody: true),
        new("POST", "/memberships", "/memberships", HasBody: true),
        new("POST", "/organizations/{organizationId}/memberships", "/organizations/org_missing/memberships", HasBody: true),
        new("GET", "/clients", "/clients"),
        new("GET", "/clients/{clientId}", "/clients/cli_missing"),
        new("GET", "/clients/{clientId}/credentials", "/clients/cli_missing/credentials"),
        new("POST", "/clients/{clientId}/credentials", "/clients/cli_missing/credentials", HasBody: true),
        new("DELETE", "/clients/{clientId}/credentials/{credentialId}", "/clients/cli_missing/credentials/clcred_missing"),
        new("GET", "/applications/{applicationId}/assignments", "/applications/cli_missing/assignments"),
        new("POST", "/applications/{applicationId}/access-mode", "/applications/cli_missing/access-mode", HasBody: true),
        new("POST", "/applications/{applicationId}/assignments", "/applications/cli_missing/assignments", HasBody: true),
        new("DELETE", "/applications/{applicationId}/assignments/{assignmentId}", "/applications/cli_missing/assignments/asa_missing"),
        new("GET", "/applications/{applicationId}/access/check", "/applications/cli_missing/access/check"),
        new("POST", "/clients", "/clients", HasBody: true),
        new("POST", "/clients/{clientId}/disable", "/clients/cli_missing/disable", HasBody: true),
        new("POST", "/clients/{clientId}/enable", "/clients/cli_missing/enable"),
        new("POST", "/clients/{clientId}/emergency-disable", "/clients/cli_missing/emergency-disable"),
        new("POST", "/clients/{clientId}/emergency-enable", "/clients/cli_missing/emergency-enable"),
        new("POST", "/clients/{clientId}/revoke", "/clients/cli_missing/revoke", HasBody: true),
        new("GET", "/machine-clients", "/machine-clients"),
        new("POST", "/machine-clients", "/machine-clients", HasBody: true),
        new("POST", "/machine-clients/{clientId}/rotate", "/machine-clients/missing-machine/rotate"),
        new("POST", "/machine-clients/{clientId}/validate", "/machine-clients/missing-machine/validate", HasBody: true),
        new("POST", "/machine-clients/{clientId}/revoke", "/machine-clients/missing-machine/revoke"),
        new("POST", "/machine-clients/{clientId}/emergency-disable", "/machine-clients/missing-machine/emergency-disable"),
        new("POST", "/machine-clients/{clientId}/emergency-enable", "/machine-clients/missing-machine/emergency-enable"),
        new("POST", "/machine-clients/{clientId}/grants", "/machine-clients/missing-machine/grants", HasBody: true),
        new("DELETE", "/machine-clients/{clientId}/grants/{grantId}", "/machine-clients/missing-machine/grants/grant_missing")
    ];

    /// <summary>
    /// Scenario option: answer an unhandled exception the way Kestrel does in production (the
    /// response is cleared and becomes <c>500</c> with no body) instead of TestServer's default of
    /// rethrowing it into the test. Several admin routes let service exceptions escape; this makes
    /// that outcome an ordinary, approvable exchange. Only scenarios that lock such a path use it.
    /// </summary>
    public static void AnswerUnhandledExceptionsAsServerErrors(ScenarioOptions options)
    {
        var previous = options.ConfigureServices;
        options.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.AddSingleton<IStartupFilter, ServerErrorStartupFilter>();
        };
    }

    /// <summary>Sends a request to a route of the table, with <c>{}</c> as the body where it binds one.</summary>
    public static Task<HttpExchange> SendAsync(HttpActor actor, AdminRoute route, Action<RequestOptions>? configure = null)
        => actor.SendAsync(
            new HttpMethod(route.Method),
            Api + route.SamplePath,
            route.HasBody ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json") : null,
            configure);

    /// <summary>
    /// Creates a client through the admin API as setup (unrecorded) and returns its internal ID.
    /// </summary>
    public static async Task<string> CreateClientAsync(Transcript t, object request)
    {
        var created = await t.Setup.OperatorPostAsync(Api + "/clients", request);
        return created.JsonString("id");
    }

    /// <summary>
    /// Signs <paramref name="user"/> in to a first-party client through the hosted password page
    /// in <paramref name="browser"/> (a fresh one by default) and redeems the code (unrecorded setup).
    /// </summary>
    public static async Task<SignedInSession> SignInAsync(
        Transcript t,
        ScenarioUser user,
        string clientId = BehaviorLockConstants.AppClientId,
        string redirectUri = BehaviorLockConstants.AppRedirectUri,
        HttpActor? browser = null)
    {
        var request = t.Urls.Authorize(clientId, redirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        return await t.Setup.SignInWithPasswordAsync(user, request, browser ?? t.NewBrowser("sign-in-" + user.DisplayName.ToLowerInvariant()));
    }

    /// <summary>
    /// Signs <paramref name="user"/> in to a client that asks for consent: the hosted password
    /// page, then the consent page's approve form, then the code exchange (unrecorded setup).
    /// </summary>
    public static async Task<SignedInSession> SignInWithConsentAsync(
        Transcript t,
        ScenarioUser user,
        string clientId,
        string redirectUri,
        HttpActor? browser = null)
    {
        browser ??= t.NewBrowser("consent-" + user.DisplayName.ToLowerInvariant());
        var request = t.Urls.Authorize(clientId, redirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Discard(await browser.GetAsync(request.Url));
        var login = t.Discard(await browser.SubmitAsync(page.Form("/login/password")
            .With("email", user.Email)
            .With("password", user.Password)));
        var consent = login.ResponseBody.Contains("/consent/approve", StringComparison.Ordinal)
            ? login
            : t.Discard(await browser.GetAsync(login.NextUrl ?? throw new InvalidOperationException($"No consent step: {login.Describe()}")));
        var approved = t.Discard(await browser.SubmitAsync(consent.Form("/consent/approve")));
        var token = t.Discard(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))));
        if (token.StatusCode != 200)
        {
            throw new InvalidOperationException($"Consent sign-in failed: {token.Describe()} {token.ResponseBody}");
        }

        await t.SkipAuditAsync();
        return new SignedInSession(
            token.JsonString("access_token"),
            token.JsonString("refresh_token"),
            token.Json?["id_token"]?.GetValue<string>(),
            request);
    }

    /// <summary>
    /// Signs <paramref name="user"/> in with the public direct password login
    /// (<c>POST /sqlos/auth/password/login</c>) for a first-party client, optionally into
    /// <paramref name="organizationId"/> (unrecorded setup).
    /// </summary>
    public static async Task<SignedInSession> PasswordLoginAsync(
        Transcript t,
        ScenarioUser user,
        string? organizationId = null,
        string clientId = BehaviorLockConstants.AppClientId)
    {
        var login = t.Discard(await t.Api.PostJsonAsync(
            "/sqlos/auth/password/login",
            new { email = user.Email, password = user.Password, clientId, organizationId }));
        if (login.StatusCode != 200)
        {
            throw new InvalidOperationException($"Password login failed: {login.Describe()} {login.ResponseBody}");
        }

        await t.SkipAuditAsync();
        return new SignedInSession(login.JsonString("tokens.accessToken"), login.JsonString("tokens.refreshToken"), null, t.Urls.Authorize(clientId));
    }

    /// <summary>
    /// The hosted invitation page prints the invitation's expiry as local text ("Expires
    /// 10/07/2026 20:52 UTC."), a clock-derived value the scrubber's timestamp patterns do not
    /// recognize. Registers the printed value under its digit mask, for example
    /// <c>{displayed-expiry:nn/nn/nnnn_nn:nn}</c>, so the transcript stays deterministic and a
    /// change of format still shows.
    /// </summary>
    public static HttpExchange ScrubDisplayedExpiry(Transcript t, HttpExchange page)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(page.ResponseBody, @"Expires (?<value>[^<]+?) UTC\."))
        {
            var value = match.Groups["value"].Value;
            var mask = new string(value.Select(character => char.IsDigit(character) ? 'n' : character == ' ' ? '_' : character).ToArray());
            t.Scrub(value, "displayed-expiry", mask);
        }

        return page;
    }

    /// <summary>
    /// SqlOS resolves a slug collision by appending a hyphen and eight random hex characters (the
    /// start of a GUID). Registers those characters, after checking the slug has exactly that
    /// shape, so the transcript reads <c>acme-{slug-suffix#1}</c>.
    /// </summary>
    public static void ScrubSlugSuffix(Transcript t, string slug, string expectedBase)
    {
        var suffix = slug.StartsWith(expectedBase + "-", StringComparison.Ordinal) ? slug[(expectedBase.Length + 1)..] : string.Empty;
        if (suffix.Length != 8 || !suffix.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Expected '{expectedBase}' plus a random suffix, got '{slug}'.");
        }

        t.Scrub(suffix, "slug-suffix");
    }

    /// <summary>The token-endpoint form that refreshes <paramref name="refreshToken"/> for a public client.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Refresh(
        string refreshToken,
        string clientId = BehaviorLockConstants.AppClientId)
    {
        yield return new("grant_type", "refresh_token");
        yield return new("refresh_token", refreshToken);
        yield return new("client_id", clientId);
    }

    /// <summary>A Basic authorization header value for client authentication at the token endpoint.</summary>
    public static string Basic(string clientId, string secret)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(secret)));

    /// <summary>
    /// Kestrel's answer to an exception that escapes the application before the response starts:
    /// the response is reset to an empty <c>500</c>. Registered through
    /// <see cref="ScenarioOptions.ConfigureServices"/>, after <c>AddSqlOS</c>, so it wraps the
    /// application's routing and endpoints inside SqlOS's pipeline.
    /// </summary>
    private sealed class ServerErrorStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                try
                {
                    await nextMiddleware(context);
                }
                catch (Exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            });
            next(app);
        };
    }
}

/// <summary>One route of <c>AdminIdentityEndpoints</c>: its method, template below the admin API base, and a concrete path.</summary>
internal sealed record AdminRoute(string Method, string Template, string SamplePath, bool HasBody = false)
{
    public string Covers => $"{Method} {AdminIdentity.Api}{Template}";
}
