using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Support;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Starts behavior-lock hosts for the public account API scenarios. Most of these JSON routes let
/// an <see cref="InvalidOperationException"/> from the auth service escape the endpoint (only the
/// public-error mapping of <c>/signup</c>, <c>/mfa/challenge/*</c>, and <c>/account/grants/revoke</c>
/// catches it). A deployed host without exception-handling middleware then answers the way Kestrel
/// does: <c>500</c> with no headers and no body. Every host started here answers that way too
/// (<see cref="ScenarioOptions.AnswerUnhandledExceptionsAsServerErrors"/>), instead of TestServer
/// rethrowing the exception into the scenario.
/// </summary>
internal static class PublicHost
{
    public static Task<Transcript> StartAsync(string profile, Action<ScenarioOptions>? configure = null)
        => Transcript.StartAsync(profile, options =>
        {
            options.AnswerUnhandledExceptionsAsServerErrors = true;
            configure?.Invoke(options);
        });

    /// <summary>
    /// Holds the SQL commands that match <paramref name="barrier"/> until every participant arrives,
    /// so parallel requests interleave the same way on every run (see <see cref="SqlCommandBarrier"/>).
    /// </summary>
    public static Action<IServiceCollection> Interleave(SqlCommandBarrier barrier)
        => services => services.ConfigureDbContext<BehaviorLockDbContext>(db => db.AddInterceptors(barrier));
}

/// <summary>
/// A rendezvous for SQL commands issued by parallel requests. While armed, every command whose
/// text contains all of <see cref="Markers"/> waits until <see cref="Participants"/> such commands
/// have arrived (or <see cref="Timeout"/> passes), then all of them run. It makes a race in
/// SqlOS deterministic: each parallel request has finished its reads before any of them writes,
/// which is the interleaving a real attacker gets by sending the requests together. Commands that
/// arrive after the barrier opened, or while it is disarmed, run immediately.
/// </summary>
internal sealed class SqlCommandBarrier : DbCommandInterceptor
{
    private readonly object _gate = new();
    private TaskCompletionSource? _opened;
    private int _arrived;

    public SqlCommandBarrier(int participants, params string[] markers)
    {
        Participants = participants;
        Markers = markers;
    }

    public int Participants { get; }

    public IReadOnlyList<string> Markers { get; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public void Arm()
    {
        lock (_gate)
        {
            _arrived = 0;
            _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _opened?.TrySetResult();
            _opened = null;
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(command, cancellationToken);
        return result;
    }

    private async Task WaitAsync(DbCommand command, CancellationToken cancellationToken)
    {
        Task opened;
        lock (_gate)
        {
            if (_opened == null
                || _opened.Task.IsCompleted
                || !Markers.All(marker => command.CommandText.Contains(marker, StringComparison.Ordinal)))
            {
                return;
            }

            opened = _opened.Task;
            if (++_arrived >= Participants)
            {
                _opened.TrySetResult();
            }
        }

        try
        {
            await opened.WaitAsync(Timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Fewer requests reached the write than expected (for example, a fixed SqlOS admits
            // fewer of them). Let everyone through; the transcript records what happened.
            Disarm();
        }
    }
}

/// <summary>
/// Unrecorded preconditions the public account scenarios need beyond <see cref="ScenarioSetup"/>.
/// Like it, they go through public HTTP surfaces only, fail loudly, and skip the audit events they
/// cause, so an audit observation shows only the journey's events.
/// </summary>
internal static class PublicSetup
{
    /// <summary>Signs <paramref name="user"/> in through <c>POST /sqlos/auth/password/login</c> and returns the response.</summary>
    public static async Task<JsonNode> PasswordLoginAsync(
        this Transcript t,
        ScenarioUser user,
        string clientId = BehaviorLockConstants.AppClientId,
        string? organizationId = null,
        HttpActor? client = null)
    {
        var login = t.Discard(await (client ?? t.Api).PostJsonAsync("/sqlos/auth/password/login", new
        {
            email = user.Email,
            password = user.Password,
            clientId,
            organizationId
        }));
        EnsureSucceeded(login);
        await t.SkipAuditAsync();
        return login.Json!;
    }

    /// <summary>Creates a user with <paramref name="email"/> through the admin API (for addresses at a scenario domain).</summary>
    public static async Task<ScenarioUser> CreateUserWithEmailAsync(this Transcript t, string name, string email, string? password = null)
    {
        var secret = password ?? t.Unique.Password(name);
        var displayName = char.ToUpperInvariant(name[0]) + name[1..];
        var created = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName, email, password = secret });
        return new ScenarioUser(created.JsonString("id"), email, secret, displayName);
    }

    /// <summary>Creates a user with no password credential (an account that signs in with codes or links only).</summary>
    public static async Task<ScenarioUser> CreatePasswordlessUserAsync(this Transcript t, string name)
    {
        var email = t.Unique.Email(name);
        var displayName = char.ToUpperInvariant(name[0]) + name[1..];
        var created = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName, email });
        return new ScenarioUser(created.JsonString("id"), email, string.Empty, displayName);
    }

    /// <summary>
    /// Creates organizations whose generated IDs ascend in the order of <paramref name="names"/>.
    /// SqlOS lists a user's organizations with no <c>ORDER BY</c> (<c>GetUserOrganizationsAsync</c>):
    /// SQL Server returns them in organization-ID order and PostgreSQL in insertion order. With IDs
    /// that ascend in creation order, and memberships added in that order, both providers return
    /// them in the order of <paramref name="names"/> on every run. An organization created out of
    /// order is left without members and never appears in the scenario.
    /// </summary>
    public static async Task<IReadOnlyList<ScenarioOrganization>> CreateOrganizationsInIdOrderAsync(this Transcript t, params string[] names)
    {
        var organizations = new List<ScenarioOrganization>();
        foreach (var name in names)
        {
            ScenarioOrganization organization;
            do
            {
                organization = await t.Setup.CreateOrganizationAsync(name);
            }
            while (organizations.Count > 0 && string.CompareOrdinal(organization.Id, organizations[^1].Id) <= 0);

            organizations.Add(organization);
        }

        return organizations;
    }

    /// <summary>
    /// Registers a client through the admin API, as an operator does in the dashboard. Admin-created
    /// clients are third-party unless marked first-party; confidential ones authenticate with
    /// <c>client_secret_basic</c> once they have a credential (<see cref="CreateClientSecretAsync"/>).
    /// </summary>
    public static Task<HttpExchange> CreateClientAsync(
        this Transcript t,
        string clientId,
        string name,
        bool isFirstParty,
        string redirectUri,
        string clientType = "public_pkce")
        => t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/clients", new
        {
            clientId,
            name,
            audience = BehaviorLockConstants.ApiAudience,
            redirectUris = new[] { redirectUri },
            allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
            isFirstParty,
            clientType
        });

    /// <summary>Issues a client secret through the admin API and returns it (shown once, as in the dashboard).</summary>
    public static async Task<string> CreateClientSecretAsync(this Transcript t, string clientId)
    {
        var created = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/clients/{clientId}/credentials", new { displayName = "Behavior lock" });
        var secret = created.JsonString("clientSecret");
        t.Scrub(secret, "client-secret");
        return secret;
    }

    /// <summary>Sets a client's application access mode (for example <c>selected_users_groups_roles</c>).</summary>
    public static Task<HttpExchange> SetApplicationAccessModeAsync(this Transcript t, string clientId, string accessMode)
        => t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/applications/{clientId}/access-mode", new { accessMode });

    /// <summary>
    /// Signs <paramref name="user"/> in to a third-party client through the hosted AuthPage in a
    /// fresh browser, approves the consent screen (which remembers the grant), redeems the code,
    /// and returns the client's refresh token. Nothing is recorded.
    /// </summary>
    public static async Task<string> ConsentThroughHostedSignInAsync(this Transcript t, ScenarioUser user, string clientId, string redirectUri)
    {
        var browser = t.NewBrowser($"{clientId}-setup");
        var request = t.Urls.Authorize(clientId: clientId, redirectUri: redirectUri, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Discard(await browser.GetAsync(request.Url));
        EnsureSucceeded(page);
        var consent = t.Discard(await browser.SubmitAsync(page.Form("/login/password").With("email", user.Email).With("password", user.Password)));
        EnsureSucceeded(consent);
        var approved = t.Discard(await browser.SubmitAsync(consent.Form("/consent/approve")));
        var token = t.Discard(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(approved.NextUrlParameter("code"))));
        EnsureSucceeded(token);
        await t.SkipAuditAsync();
        return token.JsonString("refresh_token");
    }

    /// <summary>Requires MFA for every member of <paramref name="organization"/>, with TOTP and recovery codes.</summary>
    public static Task RequireOrganizationMfaAsync(this Transcript t, ScenarioOrganization organization)
        => t.OperatorPutAsync($"/sqlos/admin/auth/api/organizations/{organization.Id}/mfa-policy", new
        {
            isEnabled = true,
            requireMfaForAllUsers = true,
            requireMfaForOwnersAndAdmins = false,
            userSelfEnrollmentEnabled = true,
            recoveryCodesEnabled = true,
            requiredRoles = new[] { "owner", "admin" },
            availableFactors = new[] { "totp", "recovery_code" }
        });

    /// <summary>Sets the refresh-token grace window, keeping the other security settings as they are.</summary>
    public static async Task SetRefreshTokenGraceWindowAsync(this Transcript t, int seconds)
    {
        var current = t.Discard(await t.Operator.GetAsync("/sqlos/admin/auth/api/settings/security"));
        EnsureSucceeded(current);
        var settings = current.Json!.AsObject().DeepClone().AsObject();
        settings["refreshTokenGraceWindowSeconds"] = seconds;
        settings.Remove("updatedAt");
        await t.OperatorPutAsync("/sqlos/admin/auth/api/settings/security", settings.ToJsonString());
    }

    /// <summary>Runs an operator PUT as setup: it must succeed and is not recorded.</summary>
    public static async Task<HttpExchange> OperatorPutAsync(this Transcript t, string target, object body)
    {
        var exchange = t.Discard(await t.Operator.PutJsonAsync(target, body));
        EnsureSucceeded(exchange);
        await t.SkipAuditAsync();
        return exchange;
    }

    /// <summary>
    /// Enrolls an authenticator app for <paramref name="user"/> through the forced-enrollment
    /// challenge of an organization that requires MFA, and returns its secret and the TOTP step
    /// the enrollment used. The enrollment answers with the previous step's code (inside SqlOS's
    /// one-step clock skew), so the current step is still unused for the next challenge.
    /// </summary>
    public static async Task<EnrolledAuthenticator> EnrollAuthenticatorAsync(this Transcript t, ScenarioUser user, ScenarioOrganization organization)
    {
        var login = await t.PasswordLoginAsync(user, organizationId: organization.Id);
        var mfaToken = login["mfaToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Expected an MFA enrollment challenge: {login.ToJsonString()}");
        var started = t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/start", new { mfaToken }));
        EnsureSucceeded(started);
        var secret = started.JsonString("secret");
        var step = await Totp.StableCurrentStepAsync() - 1;
        var verified = t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new
        {
            enrollmentToken = started.JsonString("enrollmentToken"),
            code = Totp.Code(secret, step),
            mfaToken
        }));
        EnsureSucceeded(verified);
        await t.SkipAuditAsync();
        var recoveryCodes = verified.Json?["recoveryCodes"]?.AsArray().Select(code => code!.GetValue<string>()).ToList() ?? [];
        foreach (var recoveryCode in recoveryCodes)
        {
            // The enrollment response is not recorded, so name the codes before a scenario posts
            // one (a code of letters only would not look secret to the scrubber).
            t.Scrub(recoveryCode, "recovery-code");
        }

        return new EnrolledAuthenticator(secret, step, recoveryCodes);
    }

    /// <summary>
    /// Validates <paramref name="accessToken"/> for the host API audience with the documented
    /// <c>ValidateAccessTokenAsync</c> (the SqlOS scheme's check, including the session lookup)
    /// through the host's probe, and records the exchange. The token is named an access token
    /// first, because the probe's <c>token</c> field would otherwise name it after a link token.
    /// </summary>
    public static async Task<HttpExchange> ObserveAccessTokenValidationAsync(this Transcript t, string accessToken, string caption)
    {
        t.Scrub(accessToken, "access-token");
        return t.Observe(
            await t.Api.PostJsonAsync("/__probe/auth/validate", new { token = accessToken, audience = BehaviorLockConstants.ApiAudience }),
            caption);
    }

    /// <summary>
    /// The form as a browser submits it from this page: a relative <c>action</c> resolves against
    /// the page's own URL (the harness's <see cref="HttpExchange.Form"/> keeps it as written).
    /// </summary>
    public static HtmlForm BrowserForm(this HttpExchange page, string actionContains)
    {
        var form = page.Form(actionContains);
        var pageUrl = new Uri(new Uri(BehaviorLockConstants.PublicOrigin), page.Target);
        return new HtmlForm(new Uri(pageUrl, form.Action).PathAndQuery, form.Fields);
    }

    /// <summary>The six-digit sign-in code in the latest email to <paramref name="email"/>.</summary>
    public static string LatestEmailCode(this Transcript t, string email)
    {
        var text = t.LatestEmailTo(email).TextBody ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)\d{6}(?!\d)");
        return match.Success ? match.Value : throw new InvalidOperationException($"No sign-in code in the email to {email}: {text}");
    }

    /// <summary>The <c>token</c> query value of the link in the latest email to <paramref name="email"/>.</summary>
    public static string LatestEmailLinkToken(this Transcript t, string email)
    {
        var message = t.LatestEmailTo(email);
        var text = (message.TextBody ?? string.Empty) + "\n" + System.Net.WebUtility.HtmlDecode(message.HtmlBody);
        // Tokens are base64url (percent-encoded at most); the class stops at the sentence's own
        // punctuation, since the built-in templates end the text part with "{url}. This link ...".
        var link = System.Text.RegularExpressions.Regex.Match(text, @"https?://[^\s""'<>]+[?&]token=(?<token>[A-Za-z0-9_\-%]+)");
        return link.Success
            ? Uri.UnescapeDataString(link.Groups["token"].Value)
            : throw new InvalidOperationException($"No link with a token in the email to {email}: {text}");
    }

    /// <summary>A six-digit code that no step near now accepts for <paramref name="secret"/> (SqlOS allows one step of skew).</summary>
    public static string WrongTotpCode(string secret)
    {
        var step = Totp.CurrentStep();
        var accepted = Enumerable.Range(-2, 5).Select(offset => Totp.Code(secret, step + offset)).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, 10)
            .Select(digit => new string((char)('0' + digit), 6))
            .First(candidate => !accepted.Contains(candidate));
    }

    /// <summary>A six-digit code that is guaranteed not to be <paramref name="code"/>.</summary>
    public static string WrongCode(string code, int offset = 1)
        => ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + offset) % 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    internal static void EnsureSucceeded(HttpExchange exchange)
    {
        if (exchange.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Setup call failed: {exchange.Describe()} {exchange.ResponseBody}");
        }
    }
}

/// <param name="Secret">The authenticator's base32 secret.</param>
/// <param name="EnrolledStep">The TOTP step the enrollment consumed; later codes must use a later step.</param>
/// <param name="RecoveryCodes">The recovery codes the enrollment issued.</param>
internal sealed record EnrolledAuthenticator(string Secret, long EnrolledStep, IReadOnlyList<string> RecoveryCodes);
