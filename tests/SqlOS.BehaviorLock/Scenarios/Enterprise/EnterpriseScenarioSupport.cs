using System.Text;
using System.Text.Json.Nodes;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// SCIM 2.0 request bodies and verbs for the enterprise scenarios. Directories send
/// <c>application/scim+json</c>; every helper takes the connection's bearer token so a scenario
/// reads like the directory's own traffic.
/// </summary>
internal static class Scim
{
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string PatchOpSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    /// <summary>The SCIM base path of the <c>enterprise-scim-path</c> profile, outside the dashboard prefix.</summary>
    public const string Root = "/scim/v2";

    /// <summary>The documented default SCIM base path, served in the <c>enterprise</c> profile.</summary>
    public const string DefaultRoot = "/sqlos/scim/v2";

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/scim+json");

    public static StringContent Json(JsonNode node) => Json(node.ToJsonString());

    /// <summary>A SCIM core user with a single primary work email.</summary>
    public static JsonObject User(
        string userName,
        string? externalId = null,
        string? givenName = null,
        string? familyName = null,
        string? email = null,
        bool? active = true)
    {
        var user = new JsonObject
        {
            ["schemas"] = new JsonArray(UserSchema),
            ["userName"] = userName
        };
        if (externalId != null)
        {
            user["externalId"] = externalId;
        }

        if (givenName != null || familyName != null)
        {
            var name = new JsonObject();
            if (givenName != null)
            {
                name["givenName"] = givenName;
            }

            if (familyName != null)
            {
                name["familyName"] = familyName;
            }

            user["name"] = name;
        }

        if (email != null)
        {
            user["emails"] = new JsonArray(new JsonObject { ["value"] = email, ["primary"] = true, ["type"] = "work" });
        }

        if (active != null)
        {
            user["active"] = active.Value;
        }

        return user;
    }

    /// <summary>A SCIM core group whose members are SCIM user IDs (or externalIds).</summary>
    public static JsonObject Group(string displayName, string? externalId = null, params string[] members)
    {
        var group = new JsonObject
        {
            ["schemas"] = new JsonArray(GroupSchema),
            ["displayName"] = displayName,
            ["members"] = new JsonArray(members.Select(member => (JsonNode)new JsonObject { ["value"] = member }).ToArray())
        };
        if (externalId != null)
        {
            group["externalId"] = externalId;
        }

        return group;
    }

    /// <summary>A PatchOp message; each operation is <c>(op, path, value)</c> with a null path or value omitted.</summary>
    public static JsonObject Patch(params (string Op, string? Path, JsonNode? Value)[] operations)
        => new()
        {
            ["schemas"] = new JsonArray(PatchOpSchema),
            ["Operations"] = new JsonArray(operations.Select(operation =>
            {
                var item = new JsonObject { ["op"] = operation.Op };
                if (operation.Path != null)
                {
                    item["path"] = operation.Path;
                }

                if (operation.Value != null)
                {
                    item["value"] = operation.Value;
                }

                return (JsonNode)item;
            }).ToArray())
        };

    public static JsonArray Members(params string[] ids)
        => new(ids.Select(id => (JsonNode)new JsonObject { ["value"] = id }).ToArray());

    public static Task<HttpExchange> GetAsync(this HttpActor directory, string target, string token)
        => directory.GetAsync(target, options => options.Bearer(token));

    public static Task<HttpExchange> PostAsync(this HttpActor directory, string target, JsonNode body, string token)
        => directory.SendAsync(HttpMethod.Post, target, Json(body), options => options.Bearer(token));

    public static Task<HttpExchange> PutAsync(this HttpActor directory, string target, JsonNode body, string token)
        => directory.SendAsync(HttpMethod.Put, target, Json(body), options => options.Bearer(token));

    public static Task<HttpExchange> PatchAsync(this HttpActor directory, string target, JsonNode body, string token)
        => directory.SendAsync(HttpMethod.Patch, target, Json(body), options => options.Bearer(token));

    public static Task<HttpExchange> DeleteAsync(this HttpActor directory, string target, string token)
        => directory.DeleteAsync(target, options => options.Bearer(token));

    /// <summary>
    /// Encodes a SCIM filter for a query string the way directories send it: spaces and quotes are
    /// percent-encoded, while <c>@</c> stays literal, so a unique domain after it is still
    /// recognized and named in the transcript. (Random IDs directly after an encoded quote render
    /// as a detected <c>{token#n}</c>.)
    /// </summary>
    public static string Filter(string filter) => filter.Replace(" ", "%20", StringComparison.Ordinal).Replace("\"", "%22", StringComparison.Ordinal);
}

/// <summary>
/// Enterprise preconditions the shared <see cref="ScenarioSetup"/> does not offer: FGA resources
/// for grant boundaries, SCIM connections with a boundary, group mappings, and opened SSO portal
/// sessions. Like the shared setup they go through the admin API or the library probes, are never
/// recorded, and skip the audit events they cause.
/// </summary>
internal static class EnterpriseSetup
{
    /// <summary>Creates a workspace FGA resource with a fixed ID through the documented <c>CreateResourceWithIdAsync</c>.</summary>
    public static async Task<string> CreateWorkspaceResourceAsync(this Transcript t, string resourceId, string name, string? parentResourceId = null)
    {
        var created = t.Discard(await t.Api.PostJsonAsync("/__probe/fga/resources", new
        {
            mode = "create-with-id",
            resourceTypeId = BehaviorLockAuthorization.WorkspaceType,
            name,
            resourceId,
            parentResourceId
        }));
        EnsureSucceeded(created);
        return created.JsonString("id");
    }

    /// <summary>
    /// Verifies a unique domain through the shared setup and also names its upper-case form, which
    /// appears in masked emails (<c>CE***@ACME-….EXAMPLE.TEST</c>) in password sign-in audit events.
    /// </summary>
    public static async Task<string> VerifyDomainAsync(this Transcript t, ScenarioOrganization organization, string name)
    {
        var domain = await t.Setup.VerifyDomainAsync(organization, name);
        t.Scrub(domain.ToUpperInvariant(), "domain", name.ToUpperInvariant());
        return domain;
    }

    /// <summary>
    /// Creates an enabled SCIM connection through the shared setup and also names its token prefix,
    /// which is <c>scim_</c> plus seven random characters and so is not always recognizably random.
    /// </summary>
    public static async Task<ScenarioScimConnection> CreateDirectoryAsync(this Transcript t, ScenarioOrganization organization, string name = "Directory")
    {
        var connection = await t.Setup.CreateScimConnectionAsync(organization, name);
        t.ScrubScimToken(connection.Token);
        return connection;
    }

    /// <summary>Names a SCIM bearer token and the prefix the admin API shows for it.</summary>
    public static void ScrubScimToken(this Transcript t, string token)
    {
        t.Scrub(token, "scim-token");
        if (token.Length > 12)
        {
            t.Scrub(token[..12], "token-prefix");
        }
    }

    /// <summary>Creates an enabled SCIM connection, optionally bounded to a grant boundary resource, and returns its token.</summary>
    public static async Task<ScenarioScimConnection> CreateBoundedScimConnectionAsync(
        this Transcript t,
        ScenarioOrganization organization,
        string? grantBoundaryResourceId,
        string name = "Directory")
    {
        var created = await t.Setup.OperatorPostAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/scim-connections",
            new { displayName = name, enabled = true, grantBoundaryResourceId });
        var token = created.JsonString("token");
        t.ScrubScimToken(token);
        return new ScenarioScimConnection(created.JsonString("connectionId"), token);
    }

    /// <summary>Creates a group mapping on a SCIM connection and returns the mapping ID.</summary>
    public static async Task<string> CreateScimMappingAsync(this Transcript t, ScenarioScimConnection connection, object mapping)
    {
        var created = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings", mapping);
        return created.JsonString("id");
    }

    /// <summary>
    /// Issues an SSO setup link for <paramref name="organization"/> and opens it in a new browser,
    /// which then holds the portal session cookie. Nothing is recorded.
    /// </summary>
    public static async Task<PortalVisit> OpenSsoPortalAsync(this Transcript t, ScenarioOrganization organization, string browserName = "customer-admin")
    {
        var session = await t.Setup.OperatorPostAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/sso-portal/sessions",
            new { organizationId = organization.Id });
        var browser = t.NewBrowser(browserName);
        var opened = t.Discard(await browser.GetAsync(new Uri(session.JsonString("setupUrl")).PathAndQuery));
        if (opened.StatusCode != 302)
        {
            throw new InvalidOperationException($"Opening the SSO setup link failed: {opened.Describe()}");
        }

        await t.SkipAuditAsync();
        return new PortalVisit(browser, session.JsonString("id"), session.JsonString("setupUrl"));
    }

    /// <summary>
    /// Registers a started domain verification's TXT record value, which embeds a random token the
    /// generic token pattern only recognizes when it happens to contain a digit.
    /// </summary>
    public static void ScrubDomainVerification(this Transcript t, HttpExchange started, string path = "domain.ownershipRecord.value")
    {
        var value = started.JsonString(path);
        t.Scrub(value, "dns-verification");
        var separator = value.IndexOf('=', StringComparison.Ordinal);
        if (separator >= 0 && separator < value.Length - 1)
        {
            t.Scrub(value[(separator + 1)..], "dns-verification-token");
        }
    }

    public static void EnsureSucceeded(HttpExchange exchange)
    {
        if (exchange.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Setup call failed: {exchange.Describe()} {exchange.ResponseBody}");
        }
    }
}

/// <summary>An opened SSO portal session: the browser that holds its cookie, and the operator-side IDs.</summary>
internal sealed record PortalVisit(HttpActor Browser, string SessionId, string SetupUrl)
{
    public const string PortalPath = "/sqlos/admin/auth/sso-portal";

    public const string ApiPath = PortalPath + "/api";

    public const string SetupApiPath = PortalPath + "/api/setup";

    /// <summary>The same-origin proof the portal's JavaScript sends on every mutation.</summary>
    public static void SameOrigin(RequestOptions options) => options.Header("X-SqlOS-Request", "1");

    public Task<HttpExchange> PostAsync(string path, object? body)
        => Browser.PostJsonAsync(path, body ?? new { }, SameOrigin);

    public Task<HttpExchange> PutAsync(string path, object body)
        => Browser.PutJsonAsync(path, body, SameOrigin);
}

/// <summary>Builds the IdP federation metadata documents the portal imports.</summary>
internal static class SamlMetadata
{
    /// <summary>IdP federation metadata for <paramref name="entityId"/>, as Entra, Okta, and Google export it.</summary>
    public static string For(string entityId, string singleSignOnUrl, string certificateBase64, string binding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect")
        => $"""
            <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" entityID="{entityId}">
              <md:IDPSSODescriptor protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <md:KeyDescriptor use="signing">
                  <ds:KeyInfo><ds:X509Data><ds:X509Certificate>{certificateBase64}</ds:X509Certificate></ds:X509Data></ds:KeyInfo>
                </md:KeyDescriptor>
                <md:SingleSignOnService Binding="{binding}" Location="{singleSignOnUrl}" />
              </md:IDPSSODescriptor>
            </md:EntityDescriptor>
            """;
}

/// <summary>Starts SAML sign-ins the way a browser does, through home realm discovery on the hosted page.</summary>
internal static class SamlJourney
{
    /// <summary>
    /// Opens the hosted sign-in page in <paramref name="browser"/>, identifies with
    /// <paramref name="email"/>, and returns the authorization request with the AuthnRequest the
    /// IdP received. Nothing is recorded.
    /// </summary>
    public static async Task<(AuthorizationRequest Request, SamlAuthnRequest AuthnRequest)> StartAsync(
        Transcript t,
        HttpActor browser,
        string email,
        IDictionary<string, string?>? extra = null)
    {
        var request = t.Urls.Authorize(extra: extra);
        var page = t.Discard(await browser.GetAsync(request.Url));
        var toIdp = t.Discard(await browser.SubmitAsync(page.Form("/login/identify").With("email", email)));
        var next = toIdp.NextUrl ?? throw new InvalidOperationException($"Identify did not redirect: {toIdp.Describe()} {toIdp.ResponseBody}");
        return (request, TestSamlIdentityProvider.ReadRedirect(next));
    }

    /// <summary>The ACS form an IdP auto-posts from the user's browser.</summary>
    public static Dictionary<string, string> AcsForm(string samlResponse, string relayState)
        => new() { ["SAMLResponse"] = samlResponse, ["RelayState"] = relayState };
}
