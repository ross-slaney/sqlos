using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// What the protocol scenarios send: token-endpoint forms, authorization URLs, client metadata
/// documents, and the multi-step preconditions (signing in, registering a client) several
/// journeys start from. Preconditions discard their exchanges and skip the audit events they
/// cause; recording stays with the scenario.
/// </summary>
internal static class ProtocolForms
{
    public const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>A refresh-token grant form, with optional extra fields (resource, organization_id).</summary>
    public static IEnumerable<KeyValuePair<string, string>> Refresh(string clientId, string refreshToken, params (string Name, string Value)[] extra)
    {
        yield return new("grant_type", "refresh_token");
        yield return new("refresh_token", refreshToken);
        yield return new("client_id", clientId);
        foreach (var (name, value) in extra)
        {
            yield return new(name, value);
        }
    }

    /// <summary>An arbitrary form, in the order given.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Form(params (string Name, string Value)[] fields)
        => fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value));

    /// <summary>A device-code polling form.</summary>
    public static IEnumerable<KeyValuePair<string, string>> DevicePoll(string clientId, string deviceCode)
        => Form(("grant_type", DeviceCodeGrant), ("device_code", deviceCode), ("client_id", clientId));

    /// <summary>An HTTP Basic credential for client_secret_basic (RFC 6749 §2.3.1 form-encodes both parts).</summary>
    public static string Basic(string clientId, string secret)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(secret)));

    /// <summary>
    /// A client ID metadata document (draft-ietf-oauth-client-id-metadata-document) for a public
    /// PKCE client, served by the fake network at <paramref name="clientId"/>.
    /// </summary>
    public static string ClientMetadataDocument(
        string clientId,
        string clientName,
        string redirectUri,
        string scope = "openid profile email offline_access",
        string tokenEndpointAuthMethod = "none")
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["client_id"] = clientId,
            ["client_name"] = clientName,
            ["redirect_uris"] = new[] { redirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = tokenEndpointAuthMethod,
            ["scope"] = scope
        });

    /// <summary>A dynamic client registration request body for a public PKCE client.</summary>
    public static Dictionary<string, object> PublicRegistration(
        string clientName,
        string redirectUri,
        string scope = "openid profile email offline_access")
        => new()
        {
            ["client_name"] = clientName,
            ["redirect_uris"] = new[] { redirectUri },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = scope
        };

    /// <summary>
    /// Registers a public client through dynamic client registration as a precondition (not
    /// recorded) and returns its client ID.
    /// </summary>
    public static async Task<string> RegisterPublicClientAsync(Transcript t, string clientName, string redirectUri, string scope = "openid profile email offline_access")
    {
        var registered = t.Discard(await t.NewClient("registrar").PostJsonAsync("/sqlos/auth/register", PublicRegistration(clientName, redirectUri, scope)));
        if (registered.StatusCode != 201)
        {
            throw new InvalidOperationException($"Registering {clientName} failed: {registered.Describe()} {registered.Preview()}");
        }

        await t.SkipAuditAsync();
        return registered.JsonString("client_id");
    }

    /// <summary>
    /// Signs <paramref name="user"/> in through the hosted password page and redeems the code with
    /// an optional resource indicator, as a precondition (nothing is recorded).
    /// <c>ScenarioSetup.SignInWithPasswordAsync</c> cannot pass <c>resource</c>, and SqlOS refuses a
    /// code issued for a resource when the redemption omits it.
    /// </summary>
    public static async Task<SignedInSession> SignInWithPasswordAsync(
        Transcript t,
        ScenarioUser user,
        AuthorizationRequest request,
        HttpActor browser,
        string? resource = null)
    {
        var page = t.Discard(await browser.GetAsync(request.Url));
        if (page.StatusCode != 200)
        {
            throw new InvalidOperationException($"Opening the password page failed: {page.Describe()} {page.Preview()}");
        }

        var login = t.Discard(await browser.SubmitAsync(page.Form("/login/password")
            .With("email", user.Email)
            .With("password", user.Password)));
        var token = t.Discard(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(login.NextUrlParameter("code"), resource)));
        if (token.StatusCode != 200)
        {
            throw new InvalidOperationException($"Redeeming the code failed: {token.Describe()} {token.Preview()}");
        }

        await t.SkipAuditAsync();
        return new SignedInSession(
            token.JsonString("access_token"),
            token.JsonString("refresh_token"),
            token.Json?["id_token"]?.GetValue<string>(),
            request);
    }

    /// <summary>
    /// Signs <paramref name="user"/> in on the hosted password page for <paramref name="request"/>
    /// and returns the authorization code, as a precondition (nothing is recorded). Fails when the
    /// sign-in stops on another page (consent, organization selection, MFA).
    /// </summary>
    public static async Task<string> CodeWithPasswordAsync(Transcript t, ScenarioUser user, AuthorizationRequest request, HttpActor browser)
    {
        var page = t.Discard(await browser.GetAsync(request.Url));
        var login = t.Discard(await browser.SubmitAsync(page.Form("/login/password")
            .With("email", user.Email)
            .With("password", user.Password)));
        await t.SkipAuditAsync();
        return login.NextUrlParameter("code");
    }

    /// <summary>
    /// Runs <paramref name="request"/> in a browser that already holds a SqlOS session and returns
    /// the code of the silent redirect, as a precondition (nothing is recorded).
    /// </summary>
    public static async Task<string> CodeWithSessionAsync(Transcript t, AuthorizationRequest request, HttpActor? browser = null)
    {
        var redirect = t.Discard(await (browser ?? t.Browser).GetAsync(request.Url));
        await t.SkipAuditAsync();
        return redirect.NextUrlParameter("code");
    }

    /// <summary>The authorization URL with parameters replaced (or removed when the value is null).</summary>
    public static string Modified(AuthorizationRequest request, params (string Name, string? Value)[] changes)
    {
        var query = QueryHelpers.ParseQuery(new Uri(new Uri(BehaviorLockConstants.PublicOrigin), request.Url).Query)
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.Ordinal);
        foreach (var (name, value) in changes)
        {
            query[name] = value;
        }

        return QueryHelpers.AddQueryString(
            BehaviorLockConstants.AuthBasePath + "/authorize",
            query.Where(pair => pair.Value != null)!);
    }

    /// <summary>The query parameters of <paramref name="url"/>, as form fields.</summary>
    public static IEnumerable<KeyValuePair<string, string>> QueryOf(string url)
        => QueryHelpers.ParseQuery(new Uri(new Uri(BehaviorLockConstants.PublicOrigin), url).Query)
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.ToString()));

    /// <summary>The CIMD client URL the fake network serves under the trusted host.</summary>
    public static string CimdClientId(string name) => $"https://{BehaviorLockConstants.CimdClientHost}/{name}.json";

    /// <summary>A redirect URI on the trusted CIMD host.</summary>
    public static string CimdRedirectUri(string name) => $"https://{BehaviorLockConstants.CimdClientHost}/{name}/callback";
}
