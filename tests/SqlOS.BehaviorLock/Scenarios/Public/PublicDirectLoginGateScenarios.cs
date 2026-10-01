using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// #419: direct login returns tokens to the caller with no consent screen, so only first-party
/// clients may use it. Every other client (admin-created third-party, dynamically registered, or a
/// client ID metadata document) is refused with <c>400 invalid_client</c> and one
/// <c>oauth.direct_login.rejected</c> audit event, before any token, email, or session exists.
/// <para>
/// The organization-selection, MFA-challenge, and link-completion gates are unreachable from
/// outside in 7.2.1: their artifacts are only minted after the start routes' gate, and a hosted
/// link fails the public route's authorization-request binding first (see
/// <see cref="PublicMagicLinkScenarios"/> and <see cref="PublicMfaChallengeScenarios"/>).
/// </para>
/// </summary>
[TestClass]
public sealed class PublicDirectLoginGateScenarios
{
    private const string PartnerClientId = "partner-app";
    private const string PartnerRedirectUri = "https://partner.example.test/callback";

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/email-otp/start")]
    [Covers("POST /sqlos/auth/magic-link/start")]
    public async Task An_admin_created_third_party_client_is_refused_before_anything_is_issued_or_sent()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.CreateClientAsync(PartnerClientId, "Partner App", isFirstParty: false, PartnerRedirectUri);
        var newcomer = t.Unique.Email("newcomer");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = PartnerClientId }),
            "the partner's own UI collects Alice's password: refused before the password is checked");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Newcomer", email = newcomer, password = t.Unique.Password("newcomer"), clientId = PartnerClientId }),
            "signup through the partner: refused before an account is created");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = PartnerClientId }),
            "an email code for the partner: refused before a code is sent");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = PartnerClientId }),
            "a sign-in link for the partner: refused before a link is sent");

        await t.ObserveAuditAsync("one rejection event per attempt");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "no session exists");
        t.Observe(
            await t.Operator.GetAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(newcomer)}"),
            "no account was created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/email-otp/verify")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task An_email_code_from_a_third_party_authorization_cannot_finish_as_a_direct_login()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await t.CreateClientAsync(PartnerClientId, "Partner App", isFirstParty: false, PartnerRedirectUri);
        var request = t.Urls.Authorize(clientId: PartnerClientId, redirectUri: PartnerRedirectUri);

        var authorize = t.Observe(await t.GetAsync(request.Url), "the partner starts an authorization request");
        var requestId = authorize.Form("/login/identify")["requestId"];
        var codePage = t.Observe(await t.GetAsync($"/sqlos/auth/login/email-otp?request={requestId}"), "Alice chooses an email code");
        var sent = t.Observe(
            await t.SubmitAsync(codePage.Form("/login/email-otp/start").With("email", alice.Email)),
            "the hosted page emails a code bound to the partner's authorization request");
        var verifyForm = sent.Form("/login/email-otp/verify");
        var code = t.LatestEmailCode(alice.Email);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/verify", new { challengeToken = verifyForm["challengeToken"], code }),
            "the partner replays the challenge at the public API: the code is accepted and the address claimed, then the client is refused");
        t.Observe(
            await t.SubmitAsync(verifyForm.With("code", code)),
            "the refused attempt consumed the code: the hosted page cannot use it either");

        await t.ObserveAuditAsync("the code was verified and the direct login refused");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "no session exists");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/register")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/email-otp/start")]
    public async Task Dynamically_registered_and_metadata_document_clients_are_refused_direct_login()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Dcr);
        var alice = await t.Setup.CreateUserAsync("alice");
        const string cimdClientId = "https://" + BehaviorLockConstants.CimdClientHost + "/direct-login-client.json";
        t.Setup.PublishClientMetadata(cimdClientId, $$"""
            {
              "client_id": "{{cimdClientId}}",
              "client_name": "Metadata Document Client",
              "redirect_uris": ["https://{{BehaviorLockConstants.CimdClientHost}}/callback"],
              "grant_types": ["authorization_code", "refresh_token"],
              "response_types": ["code"],
              "token_endpoint_auth_method": "none",
              "scope": "openid profile email offline_access"
            }
            """);

        var registered = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/register", new Dictionary<string, object>
            {
                ["client_name"] = "Registered Client",
                ["redirect_uris"] = new[] { "http://127.0.0.1/callback/registered" },
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none",
                ["scope"] = "openid profile offline_access"
            }),
            "anyone can register a client");
        var dcrClientId = registered.JsonString("client_id");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = dcrClientId }),
            "the registered client collects Alice's password: refused");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/signup", new { displayName = "Newcomer", email = t.Unique.Email("newcomer"), password = t.Unique.Password("newcomer"), clientId = dcrClientId }),
            "signup through the registered client: refused");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = cimdClientId }),
            "a metadata-document client: SqlOS fetches its document, then refuses it");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/email-otp/start", new { email = alice.Email, clientId = dcrClientId }),
            "with email codes off in this deployment, the factor check answers before the client check");

        await t.ObserveAuditAsync("registration and rejection events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "no session exists");
        await t.ApproveAsync();
    }
}
