using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Host.Support;

namespace SqlOS.BehaviorLock.UpgradeSeed;

/// <summary>
/// Builds the upgrade gate's "before" dataset the way a deployment accumulates it: through the
/// host's public HTTP surface (hosted sign-in, consent, DCR, CIMD, device, SCIM, the SSO portal,
/// and the admin API) plus the documented library APIs behind <c>/__probe</c> (FGA and calendar).
/// Nothing is written to tables directly, so the data has exactly the shape the released package
/// produces. Every step must succeed; the first failure aborts the seed with the response.
/// </summary>
internal sealed class UpgradeSeeder
{
    private const string BrowserScopes = "openid profile email offline_access";
    private const string SessionCookieName = "sqlos_auth_page";

    private readonly HttpMessageHandler _handler;
    private readonly BehaviorLockFakes _fakes;
    private readonly SeedHttp _operator;
    private readonly SeedHttp _api;
    private readonly string _sqlosVersion;
    private readonly TextWriter _log;

    public UpgradeSeeder(WebApplication app, string sqlosVersion, TextWriter log)
    {
        _handler = app.GetTestServer().CreateHandler();
        _fakes = app.Services.GetRequiredService<BehaviorLockFakes>();
        _operator = new SeedHttp(_handler, "operator", isBrowser: false, isOperator: true);
        _api = new SeedHttp(_handler, "api", isBrowser: false);
        _sqlosVersion = sqlosVersion;
        _log = log;
    }

    public async Task<UpgradeManifest> SeedAsync()
    {
        Step("organization, users, and memberships");
        var organizationId = (await _operator.PostJsonAsync(
                "/sqlos/admin/auth/api/organizations",
                new { name = UpgradeData.OrganizationName, slug = UpgradeData.OrganizationSlug }))
            .EnsureSuccess()
            .JsonString("id");
        var aliceId = await CreateUserAsync("Alice", UpgradeData.AliceEmail, UpgradeData.AlicePassword);
        var carolId = await CreateUserAsync("Carol", UpgradeData.CarolEmail, UpgradeData.CarolPassword);
        await AddMembershipAsync(organizationId, aliceId, "admin");
        await AddMembershipAsync(organizationId, carolId, "member");

        Step("verified domain");
        await VerifyDomainAsync(organizationId);

        Step("SAML connection");
        var samlConnectionId = await CreateSamlConnectionAsync(organizationId);

        Step("FGA tree and direct grants");
        await ProbeAsync("/__probe/fga/subjects", new { type = "user", subjectId = aliceId, displayName = "Alice", email = UpgradeData.AliceEmail, organizationId });
        await ProbeAsync("/__probe/fga/subjects", new { type = "user", subjectId = carolId, displayName = "Carol", email = UpgradeData.CarolEmail, organizationId });
        await ProbeAsync("/__probe/fga/workspaces", new { name = "Acme", id = UpgradeData.RootWorkspaceId });
        await ProbeAsync("/__probe/fga/workspaces", new { name = "Acme Projects", id = UpgradeData.ChildWorkspaceId, parentResourceId = UpgradeData.RootWorkspace });
        await ProbeAsync("/__probe/fga/grants", new { subjectId = aliceId, resourceId = UpgradeData.RootWorkspace, role = BehaviorLockAuthorization.AdminRole });
        await ProbeAsync("/__probe/fga/grants", new { subjectId = carolId, resourceId = UpgradeData.ChildWorkspace, role = BehaviorLockAuthorization.ReaderRole });

        Step("SCIM connection with a grant boundary, a group mapping, and a provisioned group");
        var scim = (await _operator.PostJsonAsync(
                $"/sqlos/admin/auth/api/organizations/{organizationId}/scim-connections",
                new { displayName = "Acme Directory", enabled = true, grantBoundaryResourceId = UpgradeData.RootWorkspace }))
            .EnsureSuccess();
        var scimConnectionId = scim.JsonString("connectionId");
        var scimToken = scim.JsonString("token");
        var scimMappingId = (await _operator.PostJsonAsync(
                $"/sqlos/admin/auth/api/scim-connections/{scimConnectionId}/mappings",
                new
                {
                    matchType = "display_name",
                    groupDisplayName = UpgradeData.ScimGroup,
                    roleKey = BehaviorLockAuthorization.ReaderRole,
                    resourceId = UpgradeData.ChildWorkspace,
                    description = "Engineering reads Acme projects"
                }))
            .EnsureSuccess()
            .JsonString("id");
        var bobId = (await ScimAsync(scimToken, "/Users", new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
                ["externalId"] = "directory-bob",
                ["userName"] = UpgradeData.BobEmail,
                ["name"] = new JsonObject { ["givenName"] = "Bob", ["familyName"] = "Builder" },
                ["emails"] = new JsonArray(new JsonObject { ["value"] = UpgradeData.BobEmail, ["primary"] = true, ["type"] = "work" }),
                ["active"] = true
            }))
            .JsonString("id");
        var scimGroupId = (await ScimAsync(scimToken, "/Groups", new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
                ["externalId"] = "directory-engineering",
                ["displayName"] = UpgradeData.ScimGroup,
                ["members"] = new JsonArray(new JsonObject { ["value"] = bobId })
            }))
            .JsonString("id");
        var bobSubjectId = await FgaSubjectIdAsync(UpgradeData.BobEmail);

        Step("Alice signs in to the first-party portal and enrolls TOTP (the MFA policy requires it)");
        var browser = new SeedHttp(_handler, "alice-browser", isBrowser: true);
        var portal = SeedAuthorization.Create(UpgradeData.PortalClientId, UpgradeData.PortalRedirectUri, BrowserScopes, view: "password");
        var passwordPage = (await browser.GetAsync(portal.Url)).EnsureSuccess();
        var enrollment = (await browser.SubmitAsync(passwordPage.Form("/login/password")
                .With("email", UpgradeData.AliceEmail)
                .With("password", UpgradeData.AlicePassword)))
            .EnsureSuccess();
        if (!enrollment.HasForm("/mfa/totp/enroll/verify"))
        {
            throw new InvalidOperationException($"Expected the TOTP enrollment page after the password step: {enrollment.Preview}");
        }

        var totpSecret = enrollment.Code("^[A-Z2-7]{16,}=*$");
        // The previous step's code, which SqlOS accepts within its default clock skew of one step.
        // SqlOS then records that step as used, so the gate can sign in with the current code
        // straight away instead of waiting for the next step to begin.
        var totpStep = await Totp.StableCurrentStepAsync() - 1;
        var enrolled = (await browser.SubmitAsync(enrollment.Form("/mfa/totp/enroll/verify").With("code", Totp.Code(totpSecret, totpStep))))
            .EnsureSuccess();
        var portalRefreshToken = await RedeemAsync(portal, await FollowToCodeAsync(browser, enrolled, portal, organizationId));
        var sessionCookie = browser.CookieValue(SessionCookieName);

        Step("Alice consents to the third-party partner client");
        var partnerRefreshToken = await AuthorizeWithSessionAsync(browser, UpgradeData.PartnerClientId, UpgradeData.PartnerRedirectUri, organizationId);

        Step("a client registers dynamically and Alice consents");
        var dynamicClientId = (await _api.PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
            {
                ["client_name"] = "Upgrade Desktop (DCR)",
                ["redirect_uris"] = new[] { UpgradeData.DynamicClientRedirectUri },
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none",
                ["scope"] = "openid profile offline_access"
            }))
            .EnsureSuccess()
            .JsonString("client_id");
        var dynamicRefreshToken = await AuthorizeWithSessionAsync(browser, dynamicClientId, UpgradeData.DynamicClientRedirectUri, organizationId, "openid profile offline_access");

        Step("a client ID metadata document client authorizes and Alice consents");
        _fakes.Cimd.Publish(UpgradeData.CimdClientId, UpgradeData.CimdDocument);
        var cimdRefreshToken = await AuthorizeWithSessionAsync(browser, UpgradeData.CimdClientId, UpgradeData.CimdRedirectUri, organizationId);

        Step("a CLI starts a device authorization that stays pending");
        var device = (await _api.PostFormAsync("/sqlos/auth/device_authorization", new Dictionary<string, string>
            {
                ["client_id"] = UpgradeData.CliClientId,
                ["scope"] = "openid profile offline_access"
            }))
            .EnsureSuccess();

        Step("Alice connects a Google calendar");
        var calendarConnectionId = await ConnectCalendarAsync(browser, aliceId);

        return new UpgradeManifest(
            _sqlosVersion,
            new UpgradeUser(aliceId, UpgradeData.AliceEmail, aliceId, UpgradeData.AlicePassword, totpSecret, totpStep),
            new UpgradeUser(bobId, UpgradeData.BobEmail, bobSubjectId, Password: null, TotpSecret: null, LastTotpStep: 0),
            new UpgradeUser(carolId, UpgradeData.CarolEmail, carolId, UpgradeData.CarolPassword, TotpSecret: null, LastTotpStep: 0),
            organizationId,
            samlConnectionId,
            scimConnectionId,
            scimToken,
            scimGroupId,
            scimMappingId,
            sessionCookie,
            [
                new UpgradeRefreshToken("portal", UpgradeData.PortalClientId, portalRefreshToken),
                new UpgradeRefreshToken("partner", UpgradeData.PartnerClientId, partnerRefreshToken),
                new UpgradeRefreshToken("dynamic", dynamicClientId, dynamicRefreshToken),
                new UpgradeRefreshToken("cimd", UpgradeData.CimdClientId, cimdRefreshToken)
            ],
            dynamicClientId,
            new UpgradeDeviceAuthorization(UpgradeData.CliClientId, device.JsonString("device_code"), device.JsonString("user_code")),
            calendarConnectionId);
    }

    /// <summary>
    /// Seeds <see cref="UpgradeData.DirectorySubjectsDataset"/>: the FGA subjects a SCIM directory
    /// gave SqlOS users before 8.0 (#448), described by <see cref="DirectorySubjectsManifest"/>.
    /// </summary>
    public async Task<DirectorySubjectsManifest> SeedDirectorySubjectsAsync()
    {
        Step("organization with a verified domain, and the FGA workspaces");
        var organizationId = (await _operator.PostJsonAsync(
                "/sqlos/admin/auth/api/organizations",
                new { name = UpgradeData.OrganizationName, slug = UpgradeData.OrganizationSlug }))
            .EnsureSuccess()
            .JsonString("id");
        await VerifyDomainAsync(organizationId);
        await ProbeAsync("/__probe/fga/workspaces", new { name = "Acme", id = UpgradeData.RootWorkspaceId });
        await ProbeAsync("/__probe/fga/workspaces", new { name = "Acme Projects", id = UpgradeData.ChildWorkspaceId, parentResourceId = UpgradeData.RootWorkspace });

        Step("SCIM connection whose Engineering mapping grants reader on the child workspace");
        var scim = (await _operator.PostJsonAsync(
                $"/sqlos/admin/auth/api/organizations/{organizationId}/scim-connections",
                new { displayName = "Acme Directory", enabled = true, grantBoundaryResourceId = UpgradeData.RootWorkspace }))
            .EnsureSuccess();
        var scimConnectionId = scim.JsonString("connectionId");
        var scimToken = scim.JsonString("token");
        var scimMappingId = (await _operator.PostJsonAsync(
                $"/sqlos/admin/auth/api/scim-connections/{scimConnectionId}/mappings",
                new
                {
                    matchType = "display_name",
                    groupDisplayName = UpgradeData.ScimGroup,
                    roleKey = BehaviorLockAuthorization.ReaderRole,
                    resourceId = UpgradeData.ChildWorkspace,
                    description = "Engineering reads Acme projects"
                }))
            .EnsureSuccess()
            .JsonString("id");

        Step("the directory provisions Bob and Ann into Engineering");
        var bobId = (await ScimAsync(scimToken, "/Users", ScimUser("directory-bob", UpgradeData.BobEmail, "Bob", "Builder"))).JsonString("id");
        var annId = (await ScimAsync(scimToken, "/Users", ScimUser("directory-ann", UpgradeData.AnnEmail, "Ann", "Archer"))).JsonString("id");
        var scimGroupId = (await ScimAsync(scimToken, "/Groups", new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
                ["externalId"] = "directory-engineering",
                ["displayName"] = UpgradeData.ScimGroup,
                ["members"] = new JsonArray(new JsonObject { ["value"] = bobId }, new JsonObject { ["value"] = annId })
            }))
            .JsonString("id");
        var bobSubjectId = await FgaSubjectIdAsync(UpgradeData.BobEmail);
        var annSubjectId = await FgaSubjectIdAsync(UpgradeData.AnnEmail);

        Step("the host provisions Ann's subject by her user ID and grants it admin on the child workspace");
        await ProbeAsync("/__probe/fga/subjects", new { type = "user", subjectId = annId, displayName = "Ann Archer (app)", email = UpgradeData.AnnEmail, organizationId });
        await ProbeAsync("/__probe/fga/grants", new { subjectId = annId, resourceId = UpgradeData.ChildWorkspace, role = BehaviorLockAuthorization.AdminRole });

        Step("an operator grants Ann's SCIM subject admin on the child workspace and reader on the root");
        (await _operator.PostJsonAsync(
                "/sqlos/admin/fga/api/grants",
                new { subjectId = annSubjectId, roleId = BehaviorLockAuthorization.AdminRole, resourceId = UpgradeData.ChildWorkspace }))
            .EnsureSuccess();
        (await _operator.PostJsonAsync(
                "/sqlos/admin/fga/api/grants",
                new { subjectId = annSubjectId, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = UpgradeData.RootWorkspace }))
            .EnsureSuccess();

        return new DirectorySubjectsManifest(
            _sqlosVersion,
            organizationId,
            scimConnectionId,
            scimToken,
            scimGroupId,
            scimMappingId,
            new DirectoryUser(bobId, bobSubjectId),
            new DirectoryUser(annId, annSubjectId));
    }

    private static JsonObject ScimUser(string externalId, string email, string givenName, string familyName)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["externalId"] = externalId,
            ["userName"] = email,
            ["name"] = new JsonObject { ["givenName"] = givenName, ["familyName"] = familyName },
            ["emails"] = new JsonArray(new JsonObject { ["value"] = email, ["primary"] = true, ["type"] = "work" }),
            ["active"] = true
        };

    private void Step(string description) => _log.WriteLine($"- {description}");

    private async Task<string> CreateUserAsync(string displayName, string email, string password)
        => (await _operator.PostJsonAsync("/sqlos/admin/auth/api/users", new { displayName, email, password }))
            .EnsureSuccess()
            .JsonString("id");

    private async Task AddMembershipAsync(string organizationId, string userId, string role)
        => (await _operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{organizationId}/memberships", new { userId, role }))
            .EnsureSuccess();

    /// <summary>
    /// Verifies <see cref="UpgradeData.Domain"/> the way a customer does: an operator issues an SSO
    /// setup link, the portal starts DNS verification, the TXT record appears in the fake DNS,
    /// and the portal confirms it.
    /// </summary>
    private async Task VerifyDomainAsync(string organizationId)
    {
        var session = (await _operator.PostJsonAsync(
                $"/sqlos/admin/auth/api/organizations/{organizationId}/sso-portal/sessions",
                new { organizationId }))
            .EnsureSuccess();
        var portal = new SeedHttp(_handler, "sso-portal", isBrowser: true);
        (await portal.GetAsync(new Uri(session.JsonString("setupUrl")).PathAndQuery)).EnsureSuccess();
        var started = (await portal.PostJsonAsync(
                "/sqlos/admin/auth/sso-portal/api/domain",
                new { domain = UpgradeData.Domain },
                ("X-SqlOS-Request", "1")))
            .EnsureSuccess();
        _fakes.Dns.Publish(started.JsonString("domain.ownershipRecord.name"), started.JsonString("domain.ownershipRecord.value"));
        var confirmed = (await portal.PostJsonAsync(
                $"/sqlos/admin/auth/sso-portal/api/domains/{started.JsonString("domain.id")}/confirm",
                new { },
                ("X-SqlOS-Request", "1")))
            .EnsureSuccess();
        if (confirmed.JsonString("domain.status") != "active")
        {
            throw new InvalidOperationException($"Domain verification did not activate the domain: {confirmed.Preview}");
        }
    }

    private async Task<string> CreateSamlConnectionAsync(string organizationId)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Acme Upgrade IdP", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (await _operator.PostJsonAsync("/sqlos/admin/auth/api/sso-connections", new
            {
                organizationId,
                displayName = "Acme SAML",
                identityProviderEntityId = "urn:acme:idp",
                singleSignOnUrl = "https://idp.acme.example.test/sso",
                x509CertificatePem = certificate.ExportCertificatePem(),
                autoProvisionUsers = true,
                autoLinkByEmail = false,
                emailAttributeName = "email",
                firstNameAttributeName = "first_name",
                lastNameAttributeName = "last_name"
            }))
            .EnsureSuccess()
            .JsonString("id");
    }

    /// <summary>The FGA subject the FGA dashboard lists for <paramref name="email"/>.</summary>
    private async Task<string> FgaSubjectIdAsync(string email)
    {
        var page = (await _operator.GetAsync($"/sqlos/admin/fga/api/users?search={Uri.EscapeDataString(email)}")).EnsureSuccess();
        var rows = page.Json["data"]?.AsArray() ?? throw new InvalidOperationException($"The FGA users page has no data: {page.Preview}");
        var matches = rows
            .Where(row => string.Equals(row?["email"]?.GetValue<string>(), email, StringComparison.OrdinalIgnoreCase))
            .Select(row => row!["subjectId"]!.GetValue<string>())
            .ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected one FGA user for {email}: {page.Preview}");
    }

    private async Task<SeedResponse> ProbeAsync(string target, object body)
        => (await _api.PostJsonAsync(target, body)).EnsureSuccess();

    private async Task<SeedResponse> ScimAsync(string token, string resource, JsonObject body)
    {
        var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/scim+json");
        return (await _api.SendAsync(HttpMethod.Post, UpgradeData.ScimBasePath + resource, content, ("Authorization", $"Bearer {token}")))
            .EnsureSuccess();
    }

    /// <summary>Runs an authorization request in a browser that already holds a SqlOS session and redeems the code.</summary>
    private async Task<string> AuthorizeWithSessionAsync(
        SeedHttp browser,
        string clientId,
        string redirectUri,
        string organizationId,
        string scope = BrowserScopes)
    {
        var request = SeedAuthorization.Create(clientId, redirectUri, scope);
        var response = (await browser.GetAsync(request.Url)).EnsureSuccess();
        return await RedeemAsync(request, await FollowToCodeAsync(browser, response, request, organizationId));
    }

    /// <summary>
    /// Follows redirects, approves consent, and picks the organization until the browser lands on
    /// the client's redirect URI, and returns the authorization code.
    /// </summary>
    private static async Task<string> FollowToCodeAsync(SeedHttp browser, SeedResponse response, SeedAuthorization request, string organizationId)
    {
        for (var hop = 0; hop < 10; hop++)
        {
            if (response.IsRedirect)
            {
                var next = response.NextUrl;
                if (next.StartsWith(request.RedirectUri, StringComparison.Ordinal))
                {
                    var query = QueryHelpers.ParseQuery(new Uri(next).Query);
                    if (query.ContainsKey("error") || query["state"].ToString() != request.State)
                    {
                        throw new InvalidOperationException($"The authorization for {request.ClientId} failed: {next}");
                    }

                    return query["code"].ToString();
                }

                response = (await browser.GetAsync(next)).EnsureSuccess();
            }
            else if (response.HasForm("/consent/approve"))
            {
                response = (await browser.SubmitAsync(response.Form("/consent/approve"))).EnsureSuccess();
            }
            else if (response.HasForm("/login/select-organization"))
            {
                response = (await browser.SubmitAsync(response.Form("/login/select-organization").With("organizationId", organizationId))).EnsureSuccess();
            }
            else
            {
                throw new InvalidOperationException($"The authorization for {request.ClientId} stopped on an unexpected page: {response.Description} {response.Status} {response.Preview}");
            }
        }

        throw new InvalidOperationException($"The authorization for {request.ClientId} did not reach its redirect URI.");
    }

    private async Task<string> RedeemAsync(SeedAuthorization request, string code)
        => (await _api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(code)))
            .EnsureSuccess()
            .JsonString("refresh_token");

    /// <summary>
    /// Connects Alice's Google calendar through <c>SqlOSCalendarService.StartConnectAsync</c> and the
    /// SqlOS-owned callback, with the fake Google answering the token exchange.
    /// </summary>
    private async Task<string> ConnectCalendarAsync(SeedHttp browser, string aliceId)
    {
        var providers = (await _api.GetAsync("/__probe/auth/providers")).EnsureSuccess().Json.AsArray();
        var google = providers.FirstOrDefault(provider => provider?["providerType"]?.GetValue<string>() == "Google")?["connectionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The upgrade profile has no enabled Google connection: {providers.ToJsonString()}");
        var started = await ProbeAsync("/__probe/calendar/connect", new
        {
            oidcConnectionId = google,
            mode = "ConnectionOnly",
            returnUri = UpgradeData.CalendarReturnUri,
            userId = aliceId,
            displayName = "Alice's calendar",
            loginHintEmail = UpgradeData.AliceEmail
        });
        var state = QueryHelpers.ParseQuery(new Uri(started.JsonString("authorizationUrl")).Query)["state"].ToString();
        var callback = (await browser.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/calendar/callback", new Dictionary<string, string?>
            {
                ["state"] = state,
                ["code"] = $"success:{UpgradeData.AliceEmail}"
            })))
            .EnsureSuccess();
        return callback.NextParameter("calendarConnectionId");
    }
}

/// <summary>An authorization-code request with PKCE, state, and nonce.</summary>
internal sealed record SeedAuthorization(string Url, string ClientId, string RedirectUri, string CodeVerifier, string State)
{
    public static SeedAuthorization Create(string clientId, string redirectUri, string scope, string? view = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = state,
            ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["view"] = view
        };
        return new SeedAuthorization(
            QueryHelpers.AddQueryString(BehaviorLockConstants.AuthBasePath + "/authorize", parameters.Where(pair => pair.Value != null)!),
            clientId,
            redirectUri,
            verifier,
            state);
    }

    public Dictionary<string, string> TokenRequest(string code) => new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["client_id"] = ClientId,
        ["redirect_uri"] = RedirectUri,
        ["code_verifier"] = CodeVerifier
    };

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
