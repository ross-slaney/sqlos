using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Support;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Journey steps the hosted AuthPage scenarios share. Everything here is either a precondition
/// (discarded, audit skipped) or a way to read a value a real user would read from their
/// mailbox, phone, or authenticator app. Nothing writes to the database directly.
/// </summary>
internal static partial class HostedFlows
{
    /// <summary>The phone number the scenarios sign up and sign in with (each scenario has its own database).</summary>
    public const string PhoneNumber = "+12025550173";

    /// <summary>
    /// Opens <c>/authorize</c> in <paramref name="browser"/> as a precondition and returns the
    /// request with the authorization request ID its hosted forms carry.
    /// </summary>
    public static async Task<HostedRequest> BeginAsync(
        Transcript t,
        HttpActor? browser = null,
        IDictionary<string, string?>? extra = null)
    {
        var request = t.Urls.Authorize(extra: extra);
        var page = t.Discard(await (browser ?? t.Browser).GetAsync(request.Url));
        EnsureStatus(page, 200);
        return new HostedRequest(request, RequestId(page), page);
    }

    /// <summary>The <c>requestId</c> a hosted page's forms post back.</summary>
    public static string RequestId(HttpExchange page)
        => RequestIdInput().Match(page.ResponseBody) is { Success: true } match
            ? match.Groups["id"].Value
            : throw new InvalidOperationException($"Exchange {page.Describe()} has no requestId input.");

    /// <summary>
    /// The form on the hosted password-reset page. Its action is relative (<c>reset/submit</c>), so
    /// it is resolved against the page's URL as a browser would: <c>/sqlos/auth/password/reset/submit</c>.
    /// </summary>
    public static HtmlForm ResetForm(HttpExchange resetPage)
    {
        var form = resetPage.Form("reset/submit");
        var resolved = new Uri(new Uri(new Uri(BehaviorLockConstants.PublicOrigin), resetPage.Target), form.Action);
        return new HtmlForm(resolved.PathAndQuery, form.Fields);
    }

    /// <summary>The six-digit code in the latest email to <paramref name="email"/>, registered as <c>{otp#n}</c>.</summary>
    public static string EmailCode(Transcript t, string email)
    {
        var code = SixDigits().Match(t.LatestEmailTo(email).TextBody ?? string.Empty) is { Success: true } match
            ? match.Value
            : throw new InvalidOperationException($"The latest email to {email} has no code.");
        t.Scrub(code, "otp");
        return code;
    }

    /// <summary>The latest SMS code sent to <paramref name="phoneNumber"/>, registered as <c>{sms-code#n}</c>.</summary>
    public static string SmsCode(Transcript t, string phoneNumber = PhoneNumber)
    {
        var code = t.LatestSmsCodeTo(phoneNumber);
        t.Scrub(code, "sms-code");
        return code;
    }

    /// <summary>The <c>token</c> query value of the link in the latest email to <paramref name="email"/>.</summary>
    public static string LinkToken(Transcript t, string email, string kind = "link-token")
    {
        var token = LinkTokenValue().Match(t.LatestEmailTo(email).TextBody ?? string.Empty) is { Success: true } match
            ? Uri.UnescapeDataString(match.Groups["token"].Value)
            : throw new InvalidOperationException($"The latest email to {email} has no link token.");
        t.Scrub(token, kind);
        return token;
    }

    /// <summary>
    /// A code of the same shape that is certainly not <paramref name="code"/>, registered as
    /// <c>{wrong-code#n}</c> so a reader sees at a glance which attempts were wrong.
    /// </summary>
    public static string WrongCode(Transcript t, string code)
    {
        var wrong = string.Concat(code.Select(digit => (char)('0' + ((digit - '0' + 1) % 10))));
        t.Scrub(wrong, "wrong-code");
        return wrong;
    }

    /// <summary>Creates a user with an exact email address through the admin API (addresses outside <c>example.test</c>).</summary>
    public static async Task<ScenarioUser> CreateUserWithEmailAsync(Transcript t, string displayName, string email, string? password = null)
    {
        var created = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName, email, password });
        return new ScenarioUser(created.JsonString("id"), email, password ?? string.Empty, displayName);
    }

    /// <summary>
    /// A returning email-code user: created by the operator, then signed in once by email code in a
    /// browser of its own. That first code proves the address, so the account now has a verified
    /// email and no password, like an account that signed up with an email code. The first visit
    /// runs inside an authorization request of its own, so its code never shares a resend-cooldown
    /// context with the scenario's (standalone codes all share one).
    /// </summary>
    public static async Task<ScenarioUser> CreateVerifiedEmailUserAsync(Transcript t, string name)
    {
        var user = await t.Setup.CreateUserAsync(name);
        var browser = t.NewBrowser($"{name}-first-visit");
        var firstVisit = await BeginAsync(t, browser);
        var page = t.Discard(await browser.GetAsync($"/sqlos/auth/login/email-otp?request={firstVisit.RequestId}"));
        var started = t.Discard(await browser.SubmitAsync(page.Form("/login/email-otp/start").With("email", user.Email)));
        EnsureStatus(started, 200);
        var code = SixDigits().Match(t.LatestEmailTo(user.Email).TextBody ?? string.Empty).Value;
        EnsureStatus(t.Discard(await browser.SubmitAsync(started.Form("/login/email-otp/verify").With("code", code))), 302);
        await t.SkipAuditAsync();
        return user with { Password = string.Empty };
    }

    /// <summary>Signs a new user up with a phone code in a browser of its own, as a precondition.</summary>
    public static async Task SignUpWithPhoneAsync(Transcript t, string displayName, string phoneNumber = PhoneNumber)
    {
        var browser = t.NewBrowser("phone-sign-up");
        var page = t.Discard(await browser.GetAsync("/sqlos/auth/signup/phone-otp"));
        var started = t.Discard(await browser.SubmitAsync(page.Form("/signup/phone-otp/start")
            .With("displayName", displayName)
            .With("phoneNumber", phoneNumber)));
        EnsureStatus(started, 200);
        var verified = t.Discard(await browser.SubmitAsync(started.Form("/signup/phone-otp/verify")
            .With("code", t.LatestSmsCodeTo(phoneNumber))));
        EnsureStatus(verified, 302);
        await t.SkipAuditAsync();
    }

    /// <summary>The ID of the user with <paramref name="email"/>, read through the admin API.</summary>
    public static async Task<string> FindUserIdAsync(Transcript t, string email)
    {
        var found = t.Discard(await t.Operator.GetAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(email)}"));
        EnsureStatus(found, 200);
        return found.JsonString("data.0.id");
    }

    /// <summary>
    /// Invites <paramref name="email"/> to <paramref name="organization"/> through the admin API
    /// (the invitation email is sent, as by default) and returns the raw invitation token from the
    /// accept link, registered as <c>{invitation-token#n}</c>.
    /// </summary>
    public static async Task<string> InviteAsync(Transcript t, ScenarioOrganization organization, string email, string role = "member")
    {
        var created = await t.Setup.OperatorPostAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/invitations",
            new { email, role, sendEmail = true });
        var inviteUrl = created.JsonString("inviteUrl");
        var token = Uri.UnescapeDataString(LinkTokenValue().Match(inviteUrl).Groups["token"].Value);
        t.Scrub(token, "invitation-token");
        // The hosted invitation card prints the expiry as "Expires {ExpiresAt:g, invariant} UTC."
        // (SqlOSAuthPageRenderer), which the generic timestamp patterns do not recognize. Name that
        // exact rendering; a different format would no longer match and would show as a diff.
        var expiresAt = DateTime.Parse(
            created.JsonString("expiresAt"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        t.Scrub(expiresAt.ToString("g", CultureInfo.InvariantCulture), "datetime", "invariant-g");
        return token;
    }

    /// <summary>
    /// Makes <paramref name="user"/> a member of two organizations, so the hosted sign-in asks which
    /// one to continue into. SqlOS lists a user's organizations without an ORDER BY
    /// (<c>SqlOSAdminService.GetUserOrganizationsAsync</c>), so the chooser's order follows the
    /// database (random organization IDs on SQL Server). Both organizations are therefore named
    /// "Branch", share the <c>{slug:branch}</c> placeholder, and give the same role: the chooser
    /// renders identically in either order, and scenarios pick options by their position on the
    /// page (<see cref="OrganizationOptions"/>), whose IDs the transcript numbers in page order.
    /// </summary>
    public static async Task AddToTwoOrganizationsAsync(Transcript t, ScenarioUser user)
    {
        var first = await t.Setup.CreateOrganizationAsync("branch");
        var second = await t.Setup.CreateOrganizationAsync("branch");
        await t.Setup.AddMembershipAsync(first, user);
        await t.Setup.AddMembershipAsync(second, user);
    }

    /// <summary>The organization IDs the hosted chooser offers, in page order.</summary>
    public static IReadOnlyList<string> OrganizationOptions(HttpExchange chooser)
        => OrganizationOption().Matches(chooser.ResponseBody).Select(match => match.Groups["id"].Value).ToList();

    /// <summary>Requires a TOTP authenticator for every user, as an operator would in the dashboard.</summary>
    public static async Task RequireTotpForAllUsersAsync(Transcript t)
    {
        var updated = t.Discard(await t.Operator.PutJsonAsync("/sqlos/admin/auth/api/settings/mfa", new
        {
            enabled = true,
            totpEnabled = true,
            userSelfEnrollmentEnabled = true,
            recoveryCodesEnabled = false,
            requireForAllUsers = true,
            requireForOwnersAndAdmins = false,
            requiredRoles = Array.Empty<string>(),
            availableFactors = new[] { "totp" }
        }));
        EnsureStatus(updated, 200);
        await t.SkipAuditAsync();
    }

    /// <summary>
    /// The authenticator setup key the hosted enrollment page shows, registered as
    /// <c>{totp-secret#n}</c> so neither it nor the provisioning URI leaks run-specific text.
    /// </summary>
    public static string TotpSecret(Transcript t, HttpExchange enrollmentPage)
    {
        var secret = SetupKey().Match(enrollmentPage.ResponseBody) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups["secret"].Value)
            : throw new InvalidOperationException($"Exchange {enrollmentPage.Describe()} shows no authenticator setup key.");
        t.Scrub(secret, "totp-secret");
        return secret;
    }

    /// <summary>The current authenticator code for <paramref name="secret"/>, registered as <c>{totp#n}</c>.</summary>
    public static async Task<(string Code, long Step)> TotpCodeAsync(Transcript t, string secret, long? lastUsedStep = null)
    {
        var step = lastUsedStep is { } used
            ? await Totp.NextUnusedStepAsync(used)
            : await Totp.StableCurrentStepAsync();
        var code = Totp.Code(secret, step);
        t.Scrub(code, "totp");
        return (code, step);
    }

    /// <summary>
    /// TestServer rethrows an unhandled application exception into the calling client, where
    /// Kestrel answers <c>500</c> with no headers and no body. Scenarios that lock a route's
    /// unhandled failure install this so the transcript shows what a browser receives.
    /// </summary>
    public static void AnswerUnhandledExceptionsLikeKestrel(ScenarioOptions options)
    {
        var previous = options.ConfigureServices;
        options.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.AddSingleton<IStartupFilter, KestrelErrorResponseStartupFilter>();
        };
    }

    /// <summary>The IDs of every audit event written so far, read through the admin audit API.</summary>
    public static async Task<HashSet<string>> AuditEventIdsAsync(Transcript t)
        => (await ReadAuditEventsAsync(t)).Select(item => item["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Records the audit events written since <paramref name="seen"/> as notes, sorted by action and
    /// then by content, instead of in write order. Only for events SqlOS writes in an unspecified
    /// order: the password-login abuse service writes one <c>password.login.locked</c> event per
    /// locked bucket, in the order EF loads <c>reservation.Buckets</c> (an Include with no ORDER BY;
    /// PostgreSQL returns them in heap order, which changes once earlier reservations are deleted),
    /// and lists <c>lockedScopes</c> the same way. Arrays of scopes are sorted too.
    /// </summary>
    public static async Task ObserveAuditSortedAsync(Transcript t, IReadOnlySet<string> seen, string caption)
    {
        var fresh = (await ReadAuditEventsAsync(t))
            .Where(item => !seen.Contains(item["id"]!.GetValue<string>()))
            .Select(item => $"{item["action"]!.GetValue<string>()} {CanonicalMetadata(item["metadata"])}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();
        t.Note($"audit, sorted by action and content (SqlOS writes these in unspecified order): {caption}");
        foreach (var line in fresh)
        {
            t.Note($"  - {line}");
        }

        await t.SkipAuditAsync();
    }

    private static async Task<List<JsonNode>> ReadAuditEventsAsync(Transcript t)
    {
        var events = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var target = "/sqlos/admin/audit/api/events?pageSize=200" + (cursor == null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = t.Discard(await t.Operator.GetAsync(target));
            EnsureStatus(page, 200);
            events.AddRange(page.Json!["data"]!.AsArray().Select(item => item!.DeepClone()));
            cursor = page.Json!["hasNextPage"]?.GetValue<bool>() == true ? page.Json!["nextCursor"]?.GetValue<string>() : null;
        }
        while (cursor != null);

        return events;
    }

    /// <summary>JSON with object members sorted and arrays of scope names sorted.</summary>
    private static string CanonicalMetadata(JsonNode? node)
        => node switch
        {
            JsonObject item => "{" + string.Join(",", item
                .OrderBy(member => member.Key, StringComparer.Ordinal)
                .Select(member => $"\"{member.Key}\":{CanonicalMetadata(member.Value)}")) + "}",
            JsonArray items => "[" + string.Join(",", items
                .Select(CanonicalMetadata)
                .OrderBy(value => value, StringComparer.Ordinal)) + "]",
            null => "null",
            _ => node.ToJsonString()
        };

    /// <summary>Fails the scenario when a precondition exchange did not answer <paramref name="status"/>.</summary>
    public static void EnsureStatus(HttpExchange exchange, int status)
    {
        if (exchange.StatusCode != status)
        {
            throw new InvalidOperationException($"Precondition failed: {exchange.Describe()}, expected {status}. {exchange.Preview()}");
        }
    }

    [GeneratedRegex("""name="requestId" value="(?<id>[^"]+)""")]
    private static partial Regex RequestIdInput();

    [GeneratedRegex("""name="organizationId" value="(?<id>[^"]+)""")]
    private static partial Regex OrganizationOption();

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)")]
    private static partial Regex SixDigits();

    // The built-in templates end a sentence right after the URL ("...?token=abc. This link..."),
    // so a trailing period is punctuation, never part of the token.
    [GeneratedRegex(@"[?&]token=(?<token>[^\s&""'<>]*[^\s&""'<>.])")]
    private static partial Regex LinkTokenValue();

    [GeneratedRegex(@"<span>Setup key</span>\s*<code>(?<secret>[^<]+)</code>")]
    private static partial Regex SetupKey();

    /// <summary>Innermost startup filter: turns an exception the endpoints left unhandled into Kestrel's bare 500.</summary>
    private sealed class KestrelErrorResponseStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                try
                {
                    await nextMiddleware(context);
                }
                catch (Exception) when (!context.Response.HasStarted)
                {
                    // Kestrel resets the response headers and answers 500 with Content-Length: 0.
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentLength = 0;
                }
            });
            next(app);
        };
    }
}

/// <summary>An authorization request opened in the browser: the request, the ID its hosted forms post, and the page.</summary>
internal sealed record HostedRequest(AuthorizationRequest Request, string RequestId, HttpExchange Page);
