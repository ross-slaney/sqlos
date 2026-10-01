using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// Surface-local helpers for the headless scenarios. They only read values out of exchanges and
/// captured effects (a code in an email, the authorization code in a redirect action), build the
/// JSON bodies the <c>@sqlos/headless</c> package posts, or run unrecorded setup through the
/// admin API, so they never hide behavior from a transcript.
/// </summary>
internal static partial class HeadlessJourney
{
    /// <summary>The default headless API base path.</summary>
    public const string Api = "/sqlos/auth/headless";

    /// <summary>The first-party single-application client the headless profile derives.</summary>
    public const string AppClientId = BehaviorLockConstants.AppClientId;

    /// <summary>A third-party client an operator registered, for consent journeys.</summary>
    public const string PartnerClientId = "partner-app";

    public const string PartnerRedirectUri = "https://partner.example.test/callback";

    /// <summary>A first-party public client allowed to use the device authorization grant.</summary>
    public const string CliClientId = "lock-cli";

    public const string PhoneNumber = "+12025550148";

    public const string SecondPhoneNumber = "+12025550173";

    /// <summary>
    /// Opens <c>/authorize</c> in <paramref name="browser"/> and returns the authorization request
    /// ID the redirect to the app's own UI carries. With no caption the exchange is a precondition.
    /// </summary>
    public static async Task<string> OpenAuthorizeAsync(
        Transcript t,
        AuthorizationRequest request,
        string? caption = null,
        HttpActor? browser = null)
    {
        var exchange = await (browser ?? t.Browser).GetAsync(request.Url);
        if (caption == null)
        {
            t.Discard(exchange);
        }
        else
        {
            t.Observe(exchange, caption);
        }

        return exchange.NextUrlParameter("request");
    }

    /// <summary>The authorization code in a headless <c>redirect</c> action result.</summary>
    public static string RedirectCode(HttpExchange exchange)
        => QueryHelpers.ParseQuery(new Uri(exchange.JsonString("redirectUrl")).Query)["code"].ToString();

    /// <summary>The <c>sub</c> claim of a JWT the scenario received (to address end-state reads).</summary>
    public static string TokenSubject(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var claims = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
        return claims.RootElement.GetProperty("sub").GetString()
            ?? throw new InvalidOperationException("The token has no sub claim.");
    }

    /// <summary>The six-digit code in the latest email to <paramref name="email"/>.</summary>
    public static string EmailCode(Transcript t, string email)
        => SixDigits().Match(t.LatestEmailTo(email).TextBody ?? string.Empty).Value;

    /// <summary>
    /// The <c>token</c> query parameter of the first link in the latest email to
    /// <paramref name="email"/>. Text bodies end the link with sentence punctuation, so only
    /// URL-safe token characters are taken.
    /// </summary>
    public static string EmailLinkToken(Transcript t, string email)
    {
        var body = t.LatestEmailTo(email).TextBody ?? string.Empty;
        var link = LinkToken().Match(body);
        if (!link.Success)
        {
            throw new InvalidOperationException($"The latest email to {email} has no link with a token: {body}");
        }

        return Uri.UnescapeDataString(link.Groups["token"].Value);
    }

    /// <summary>
    /// The <c>/headless/start</c> body the <c>@sqlos/headless</c> package posts for a native app,
    /// built from <paramref name="request"/>'s client, redirect URI, PKCE challenge, state, nonce,
    /// and scope (all registered with the scrubber). Optional fields are omitted when null, as the
    /// package omits undefined fields.
    /// </summary>
    public static Dictionary<string, object?> NativeStart(
        AuthorizationRequest request,
        string? view = null,
        string? loginHint = null,
        string? prompt = null,
        object? uiContext = null,
        string? invitationToken = null,
        string? resource = null,
        string? maxAge = null,
        string responseType = "code",
        string? codeChallengeMethod = "S256",
        bool omitCodeChallenge = false)
    {
        var query = QueryHelpers.ParseQuery(new Uri(new Uri(BehaviorLockConstants.PublicOrigin), request.Url).Query);
        var body = new Dictionary<string, object?>
        {
            ["responseType"] = responseType,
            ["clientId"] = request.ClientId,
            ["redirectUri"] = request.RedirectUri,
            ["state"] = request.State,
            ["scope"] = query["scope"].ToString(),
            ["codeChallenge"] = omitCodeChallenge ? null : query["code_challenge"].ToString(),
            ["codeChallengeMethod"] = codeChallengeMethod,
            ["resource"] = resource,
            ["loginHint"] = loginHint,
            ["prompt"] = prompt,
            ["nonce"] = request.Nonce,
            ["view"] = view,
            ["uiContext"] = uiContext,
            ["invitationToken"] = invitationToken,
            ["maxAge"] = maxAge
        };
        return body.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>Registers an operator-created client (unrecorded setup) and returns its client ID.</summary>
    public static async Task<string> CreateClientAsync(
        Transcript t,
        string clientId,
        string name,
        IReadOnlyList<string> redirectUris,
        bool isFirstParty,
        bool allowNativeHeadlessAuth = false,
        bool allowDeviceAuthorization = false,
        string clientType = "public_pkce",
        string audience = BehaviorLockConstants.ApiAudience)
    {
        await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/clients", new
        {
            clientId,
            name,
            audience,
            redirectUris,
            allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
            requirePkce = true,
            isFirstParty,
            allowNativeHeadlessAuth,
            allowDeviceAuthorization,
            clientType
        });
        return clientId;
    }

    /// <summary>The operator-registered third-party client used by consent journeys.</summary>
    public static Task<string> CreatePartnerClientAsync(Transcript t)
        => CreateClientAsync(t, PartnerClientId, "Partner App", [PartnerRedirectUri], isFirstParty: false);

    /// <summary>The operator-registered first-party CLI that uses the device authorization grant.</summary>
    public static Task<string> CreateCliClientAsync(Transcript t)
        => CreateClientAsync(t, CliClientId, "Lock CLI", [], isFirstParty: true, allowDeviceAuthorization: true);

    /// <summary>Turns on TOTP MFA for every user through the admin API (unrecorded setup).</summary>
    public static async Task RequireTotpForAllUsersAsync(Transcript t)
    {
        var updated = t.Discard(await t.Operator.PutJsonAsync("/sqlos/admin/auth/api/settings/mfa", new
        {
            enabled = true,
            totpEnabled = true,
            userSelfEnrollmentEnabled = true,
            recoveryCodesEnabled = true,
            requireForAllUsers = true,
            requireForOwnersAndAdmins = false,
            requiredRoles = Array.Empty<string>(),
            availableFactors = new[] { "totp", "recovery_code" }
        }));
        if (updated.StatusCode != 200)
        {
            throw new InvalidOperationException($"Setup call failed: {updated.Describe()} {updated.Preview()}");
        }

        await t.SkipAuditAsync();
    }

    /// <summary>
    /// Signs <paramref name="user"/> in through the headless password route for a fresh
    /// authorization request of the app and redeems the code, as a precondition: nothing is
    /// recorded. The browser keeps the issuer session cookie the sign-in sets. (The shared
    /// <c>t.Setup.SignInWithPasswordAsync</c> drives the hosted page, which a headless host
    /// does not serve.)
    /// </summary>
    public static async Task<SignedInSession> SignInAsync(Transcript t, ScenarioUser user, HttpActor? browser = null)
    {
        browser ??= t.Browser;
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request, caption: null, browser);
        var login = t.Discard(await browser.PostJsonAsync($"{Api}/password/login", new { requestId, email = user.Email, password = user.Password }));
        if (login.Json?["type"]?.GetValue<string>() != "redirect")
        {
            throw new InvalidOperationException($"Setup sign-in did not redirect: {login.Describe()} {login.Preview()}");
        }

        var token = t.Discard(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))));
        if (token.StatusCode != 200)
        {
            throw new InvalidOperationException($"Setup token exchange failed: {token.Describe()} {token.Preview()}");
        }

        await t.SkipAuditAsync();
        return new SignedInSession(
            token.JsonString("access_token"),
            token.JsonString("refresh_token"),
            token.Json?["id_token"]?.GetValue<string>(),
            request);
    }

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)")]
    private static partial Regex SixDigits();

    [GeneratedRegex(@"https?://[^\s""<>?]+\?(?:[^\s""<>]*&)?token=(?<token>[A-Za-z0-9\-_~%]+)")]
    private static partial Regex LinkToken();
}
