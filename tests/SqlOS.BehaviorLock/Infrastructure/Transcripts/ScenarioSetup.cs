using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Preconditions a scenario needs but does not lock: they go through the public admin API (or
/// the library probes when the profile has no operator access), are never recorded, fail loudly,
/// and skip the audit events they cause so <see cref="Transcript.ObserveAuditAsync"/> only shows
/// the journey's events. Setup never writes entities directly, so it compiles and behaves the
/// same against the released package and the refactored source.
/// </summary>
public sealed class ScenarioSetup
{
    private readonly Transcript _transcript;

    internal ScenarioSetup(Transcript transcript)
    {
        _transcript = transcript;
    }

    public async Task<ScenarioUser> CreateUserAsync(string name, string? password = null)
    {
        var email = _transcript.Unique.Email(name);
        var secret = password ?? _transcript.Unique.Password(name);
        var displayName = char.ToUpperInvariant(name[0]) + name[1..];
        var created = await SendAsync(
            "/sqlos/admin/auth/api/users",
            "/__probe/admin/users",
            new { displayName, email, password = secret });
        return new ScenarioUser(created.JsonString("id"), email, secret, displayName);
    }

    public async Task<ScenarioOrganization> CreateOrganizationAsync(string name, string? primaryDomain = null)
    {
        var slug = _transcript.Unique.Slug(name);
        var displayName = char.ToUpperInvariant(name[0]) + name[1..];
        var created = await SendAsync(
            "/sqlos/admin/auth/api/organizations",
            "/__probe/admin/organizations",
            new { name = displayName, slug, primaryDomain });
        return new ScenarioOrganization(created.JsonString("id"), slug, displayName);
    }

    public async Task AddMembershipAsync(ScenarioOrganization organization, ScenarioUser user, string role = "member")
        => await SendAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/memberships",
            $"/__probe/admin/organizations/{organization.Id}/memberships",
            new { userId = user.Id, role });

    /// <summary>Creates a SCIM connection for the organization and returns its bearer token.</summary>
    public async Task<ScenarioScimConnection> CreateScimConnectionAsync(ScenarioOrganization organization, string name = "Directory")
    {
        var created = await SendOperatorAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/scim-connections",
            new { displayName = name, enabled = true });
        var token = created.JsonString("token");
        _transcript.Scrub(token, "scim-token");
        return new ScenarioScimConnection(created.JsonString("connectionId"), token);
    }

    /// <summary>
    /// Signs <paramref name="user"/> in through the hosted password page and redeems the code, as a
    /// precondition (nothing is recorded). Works in profiles that serve the hosted AuthPage.
    /// </summary>
    public async Task<SignedInSession> SignInWithPasswordAsync(ScenarioUser user, AuthorizationRequest? request = null, HttpActor? browser = null)
    {
        request ??= _transcript.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        browser ??= _transcript.Browser;
        var page = _transcript.Discard(await browser.GetAsync(request.Url));
        EnsureSucceeded(page);
        var login = _transcript.Discard(await browser.SubmitAsync(page.Form("/login/password")
            .With("email", user.Email)
            .With("password", user.Password)));
        var code = login.NextUrlParameter("code");
        var token = _transcript.Discard(await _transcript.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)));
        EnsureSucceeded(token);
        await _transcript.SkipAuditAsync();
        return new SignedInSession(
            token.JsonString("access_token"),
            token.JsonString("refresh_token"),
            token.Json?["id_token"]?.GetValue<string>(),
            request);
    }

    /// <summary>
    /// Verifies ownership of a unique domain (<c>{domain:name}</c>) for the organization the way a
    /// customer would: an operator issues an SSO setup link, the portal starts DNS verification,
    /// the TXT record is published in the fake DNS, and the portal confirms it.
    /// </summary>
    public async Task<string> VerifyDomainAsync(ScenarioOrganization organization, string name)
    {
        var domain = _transcript.Unique.Domain(name);
        var session = await SendOperatorAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/sso-portal/sessions",
            new { organizationId = organization.Id });
        var portal = _transcript.NewBrowser("sso-portal-setup");
        var opened = _transcript.Discard(await portal.GetAsync(new Uri(session.JsonString("setupUrl")).PathAndQuery));
        if (opened.StatusCode is not (302 or 200))
        {
            throw new InvalidOperationException($"Opening the SSO setup link failed: {opened.Describe()} {opened.Preview()}");
        }

        var started = _transcript.Discard(await portal.PostJsonAsync(
            "/sqlos/admin/auth/sso-portal/api/domain",
            new { domain },
            options => options.Header("X-SqlOS-Request", "1")));
        EnsureSucceeded(started);
        PublishDnsTxt(
            started.JsonString("domain.ownershipRecord.name"),
            started.JsonString("domain.ownershipRecord.value"));
        var confirmed = _transcript.Discard(await portal.PostJsonAsync(
            $"/sqlos/admin/auth/sso-portal/api/domains/{started.JsonString("domain.id")}/confirm",
            new { },
            options => options.Header("X-SqlOS-Request", "1")));
        EnsureSucceeded(confirmed);
        if (confirmed.JsonString("domain.status") != "active")
        {
            throw new InvalidOperationException($"Domain verification did not activate the domain: {confirmed.Preview()}");
        }

        await _transcript.SkipAuditAsync();
        return domain;
    }

    /// <summary>Creates an enabled SAML connection for the organization, trusting <paramref name="identityProvider"/>.</summary>
    public async Task<string> CreateSamlConnectionAsync(
        ScenarioOrganization organization,
        Fakes.TestSamlIdentityProvider identityProvider,
        bool autoProvisionUsers = true,
        bool autoLinkByEmail = false)
    {
        identityProvider.RegisterWith(_transcript);
        var created = await SendOperatorAsync("/sqlos/admin/auth/api/sso-connections", new
        {
            organizationId = organization.Id,
            displayName = $"{organization.Name} SAML",
            identityProviderEntityId = identityProvider.EntityId,
            singleSignOnUrl = identityProvider.SingleSignOnUrl,
            x509CertificatePem = identityProvider.CertificatePem,
            autoProvisionUsers,
            autoLinkByEmail,
            emailAttributeName = "email",
            firstNameAttributeName = "first_name",
            lastNameAttributeName = "last_name"
        });
        return created.JsonString("id");
    }

    /// <summary>Publishes a DNS TXT record in the host's fake DNS.</summary>
    public void PublishDnsTxt(string recordName, string value) => _transcript.Fakes.Dns.Publish(recordName, value);

    /// <summary>Serves a client ID metadata document at <paramref name="url"/> on the fake network.</summary>
    public void PublishClientMetadata(string url, string json) => _transcript.Fakes.Cimd.Publish(url, json);

    /// <summary>Runs an arbitrary operator call as setup: it must succeed and is not recorded.</summary>
    public Task<HttpExchange> OperatorPostAsync(string target, object body) => SendOperatorAsync(target, body);

    private async Task<HttpExchange> SendAsync(string adminTarget, string probeTarget, object body)
        => _transcript.Profile.OperatorAccess == OperatorAccess.None
            ? await SendThroughProbeAsync(probeTarget, body)
            : await SendOperatorAsync(adminTarget, body);

    private async Task<HttpExchange> SendOperatorAsync(string target, object body)
    {
        var exchange = _transcript.Discard(await _transcript.Operator.PostJsonAsync(target, body));
        EnsureSucceeded(exchange);
        await _transcript.SkipAuditAsync();
        return exchange;
    }

    private async Task<HttpExchange> SendThroughProbeAsync(string target, object body)
    {
        var exchange = _transcript.Discard(await _transcript.Api.PostJsonAsync(target, body));
        EnsureSucceeded(exchange);
        return exchange;
    }

    private static void EnsureSucceeded(HttpExchange exchange)
    {
        if (exchange.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Setup call failed: {exchange.Describe()} {exchange.Preview()}");
        }
    }
}

public sealed record ScenarioUser(string Id, string Email, string Password, string DisplayName);

public sealed record ScenarioOrganization(string Id, string Slug, string Name);

public sealed record ScenarioScimConnection(string Id, string Token);

public sealed record SignedInSession(string AccessToken, string RefreshToken, string? IdToken, AuthorizationRequest Request);

/// <summary>
/// Per-run unique values. Each is registered with the scrubber under a fixed name, so the
/// transcript reads <c>{email:alice}</c> no matter which random suffix this run used.
/// </summary>
public sealed class UniqueValues
{
    private readonly Transcript _transcript;

    internal UniqueValues(Transcript transcript)
    {
        _transcript = transcript;
    }

    public string Email(string name, string domain = "example.test")
    {
        // At a scenario's own (already unique) domain the local part needs no suffix.
        var value = domain == "example.test" ? $"{name}-{Suffix()}@{domain}" : $"{name}@{domain}";
        _transcript.Scrubber.RegisterNamed(value, "email", name);
        // SqlOS stores and audits normalized (upper-case) addresses; name that form too.
        _transcript.Scrubber.RegisterNamed(value.ToUpperInvariant(), "email", name.ToUpperInvariant());
        return value;
    }

    /// <summary>A unique domain under <c>example.test</c>, named <c>{domain:name}</c> in transcripts.</summary>
    public string Domain(string name)
    {
        var value = $"{name}-{Suffix()}.example.test";
        _transcript.Scrubber.RegisterNamed(value, "domain", name);
        return value;
    }

    public string Slug(string name)
    {
        var value = $"{name}-{Suffix()}";
        _transcript.Scrubber.RegisterNamed(value, "slug", name);
        return value;
    }

    /// <summary>A strong fixture password, stable per name so transcripts read <c>{password:alice}</c>.</summary>
    public string Password(string name)
    {
        var value = $"Lock-{char.ToUpperInvariant(name[0])}{name[1..]}-2468!";
        _transcript.Scrubber.RegisterNamed(value, "password", name);
        return value;
    }

    private static string Suffix() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

/// <summary>
/// Protocol URL builders. PKCE pairs, state, and nonce are generated per call and registered
/// with the scrubber, so transcripts show <c>{state#1}</c> rather than random text.
/// </summary>
public sealed class ScenarioUrls
{
    private readonly Transcript _transcript;

    internal ScenarioUrls(Transcript transcript)
    {
        _transcript = transcript;
    }

    /// <summary>Builds an authorization request and returns it with the PKCE verifier needed to redeem the code.</summary>
    public AuthorizationRequest Authorize(
        string clientId = BehaviorLockConstants.AppClientId,
        string redirectUri = BehaviorLockConstants.AppRedirectUri,
        string scope = "openid profile email offline_access",
        IDictionary<string, string?>? extra = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        _transcript.Scrubber.Register(verifier, "pkce-verifier");
        _transcript.Scrubber.Register(challenge, "pkce-challenge");
        _transcript.Scrubber.Register(state, "state");
        _transcript.Scrubber.Register(nonce, "nonce");
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            parameters[key] = value;
        }

        return new AuthorizationRequest(
            QueryHelpers.AddQueryString(BehaviorLockConstants.AuthBasePath + "/authorize", parameters.Where(pair => pair.Value != null)!),
            clientId,
            redirectUri,
            verifier,
            state,
            nonce);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record AuthorizationRequest(
    string Url,
    string ClientId,
    string RedirectUri,
    string CodeVerifier,
    string State,
    string Nonce)
{
    /// <summary>The token-endpoint form that redeems <paramref name="code"/> for this request.</summary>
    public IEnumerable<KeyValuePair<string, string>> TokenRequest(string code, string? resource = null)
    {
        yield return new("grant_type", "authorization_code");
        yield return new("code", code);
        yield return new("client_id", ClientId);
        yield return new("redirect_uri", RedirectUri);
        yield return new("code_verifier", CodeVerifier);
        if (resource != null)
        {
            yield return new("resource", resource);
        }
    }
}
