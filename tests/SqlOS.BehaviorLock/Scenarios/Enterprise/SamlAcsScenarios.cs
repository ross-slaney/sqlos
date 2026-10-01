using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// SAML 2.0 service-provider behavior: starting a sign-in (home realm discovery or the
/// authorization-URL API), and the assertion consumer service's request matching, signature and
/// condition checks, replay protection, and the generic public error every refusal returns, whose
/// real reason only the audit log records.
/// </summary>
[TestClass]
public sealed class SamlAcsScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/sso/authorization-url")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_application_starts_saml_with_the_authorization_url_api_and_redeems_the_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        var request = t.Urls.Authorize();

        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/sso/authorization-url", new
            {
                connectionId,
                clientId = request.ClientId,
                redirectUri = request.RedirectUri,
                state = request.State,
                codeChallenge = Challenge(request),
                codeChallengeMethod = "S256"
            }),
            "the application asks for the IdP redirect of Acme's connection");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(started.JsonString("authorizationUrl"));
        t.ObserveDocument("the AuthnRequest", authnRequest.Xml);
        var acs = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov")
                {
                    AuthnContextClassRef = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"
                }),
                authnRequest.RelayState)),
            "the IdP posts Ivan's assertion, with an authentication context");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))), "redeem the code");

        await t.ObserveAuditAsync("sign-in events, including the upstream assurance evaluation");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/sso/authorization-url")]
    public async Task Authorization_url_requests_are_validated()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var draft = await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}/sso-portal/sessions", new { });
        var draftConnectionId = draft.JsonString("connectionId");
        var request = t.Urls.Authorize();
        var challenge = Challenge(request);
        object Body(string? connection = null, string? clientId = null, string? redirectUri = null, string? state = "set", string? codeChallenge = "set", string? method = "S256")
            => new
            {
                connectionId = connection ?? connectionId,
                clientId = clientId ?? request.ClientId,
                redirectUri = redirectUri ?? request.RedirectUri,
                state = state == "set" ? request.State : state,
                codeChallenge = codeChallenge == "set" ? challenge : codeChallenge,
                codeChallengeMethod = method
            };
        Task<HttpExchange> Start(object body) => t.Api.PostJsonAsync("/sqlos/auth/sso/authorization-url", body);

        t.Observe(await Start(Body(state: "")), "no state");
        t.Observe(await Start(Body(codeChallenge: "")), "no PKCE challenge");
        t.Observe(await Start(Body(method: "plain")), "the plain PKCE method");
        t.Observe(await Start(Body(codeChallenge: "too-short")), "a challenge that is not a SHA-256 digest");
        t.Observe(await Start(Body(connection: "sso_ffffffffffffffffffffffffffffffff")), "an unknown connection");
        t.Observe(await Start(Body(connection: draftConnectionId)), "Globex's draft connection, which is not enabled");
        t.Observe(await Start(Body(clientId: "unknown-client")), "an unknown client");
        t.Observe(await Start(Body(redirectUri: "https://attacker.example.test/callback")), "a redirect URI the client did not register");

        await t.ObserveAuditAsync("refusals");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/sso-connections/{connectionId}/disable")]
    public async Task The_acs_only_completes_a_live_request_that_belongs_to_its_connection()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var acmeIdp = new TestSamlIdentityProvider("urn:behavior-lock:acme-idp", "https://acme-idp.example.test/sso");
        using var globexIdp = new TestSamlIdentityProvider("urn:behavior-lock:globex-idp", "https://globex-idp.example.test/sso");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var acmeDomain = await t.VerifyDomainAsync(acme, "acme");
        var acmeConnection = await t.Setup.CreateSamlConnectionAsync(acme, acmeIdp);
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var globexDomain = await t.VerifyDomainAsync(globex, "globex");
        var globexConnection = await t.Setup.CreateSamlConnectionAsync(globex, globexIdp);
        var ivan = t.Unique.Email("ivan", acmeDomain);
        var gus = t.Unique.Email("gus", globexDomain);
        var ivanBrowser = t.NewBrowser("ivan");
        var gusBrowser = t.NewBrowser("gus");
        var (_, ivanRequest) = await SamlJourney.StartAsync(t, ivanBrowser, ivan);
        var (_, gusRequest) = await SamlJourney.StartAsync(t, gusBrowser, gus);
        await t.SkipAuditAsync();
        t.Note("Ivan started at Acme's IdP and Gus at Globex's; each has a live authorization request bound to his organization's connection.");
        var ivanResponse = acmeIdp.BuildResponse(ivanRequest, new SamlAssertion(ivan, "Ivan", "Petrov"));
        var acmeAcs = $"/sqlos/auth/saml/acs/{acmeConnection}";

        t.Observe(await ivanBrowser.PostFormAsync(acmeAcs, new Dictionary<string, string> { ["RelayState"] = ivanRequest.RelayState }), "no SAMLResponse");
        t.Observe(await ivanBrowser.PostFormAsync(acmeAcs, new Dictionary<string, string> { ["SAMLResponse"] = ivanResponse }), "no RelayState");
        t.Observe(await ivanBrowser.PostFormAsync("/sqlos/auth/saml/acs/sso_ffffffffffffffffffffffffffffffff", SamlJourney.AcsForm(ivanResponse, ivanRequest.RelayState)), "an unknown connection");
        t.Observe(await ivanBrowser.PostFormAsync(acmeAcs, SamlJourney.AcsForm(ivanResponse, "req_ffffffffffffffffffffffffffffffff")), "a RelayState that names no request");
        t.Observe(
            await gusBrowser.PostFormAsync(acmeAcs, SamlJourney.AcsForm(acmeIdp.BuildResponse(gusRequest, new SamlAssertion(gus, "Gus", "Grant")), gusRequest.RelayState)),
            "Acme's IdP answers Globex's request at Acme's ACS: the request is not Acme's");
        t.Observe(await ivanBrowser.PostFormAsync(acmeAcs, SamlJourney.AcsForm(ivanResponse, ivanRequest.RelayState)), "Ivan's valid response completes his request");
        t.Observe(await ivanBrowser.PostFormAsync(acmeAcs, SamlJourney.AcsForm(ivanResponse, ivanRequest.RelayState)), "posting it again: the request is no longer active");

        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/sso-connections/{globexConnection}/disable", new { }), "the operator disables Globex's connection");
        t.Observe(
            await gusBrowser.PostFormAsync($"/sqlos/auth/saml/acs/{globexConnection}", SamlJourney.AcsForm(globexIdp.BuildResponse(gusRequest, new SamlAssertion(gus, "Gus", "Grant")), gusRequest.RelayState)),
            "a valid response for a disabled connection");

        await t.ObserveAuditAsync("each refusal's real reason is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task Responses_that_fail_signature_or_condition_checks_are_refused_and_the_reason_is_audited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        var mallory = t.Unique.Email("mallory", domain);
        var (_, authnRequest) = await SamlJourney.StartAsync(t, t.Browser, ivan);
        await t.SkipAuditAsync();
        var forge = new SamlResponseForge(idp);
        var acs = $"/sqlos/auth/saml/acs/{connectionId}";
        var inFuture = DateTime.UtcNow.AddMinutes(30).ToString("o");
        t.Note("Ivan's authorization request stays live through every refusal; each response below answers it.");

        async Task PostAsync(string samlResponse, string caption)
            => t.Observe(await t.PostFormAsync(acs, SamlJourney.AcsForm(samlResponse, authnRequest.RelayState)), caption);

        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { Signing = SamlSigning.None }), "an unsigned response");
        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { Signing = SamlSigning.OtherKey }), "a response signed by a key the connection does not trust");
        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { WrapUnsignedAssertion = mallory }), "an unsigned assertion for Mallory wrapped ahead of the signed one");
        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { Audience = "https://other-sp.example.test" }), "an assertion for another service provider");
        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { Recipient = "https://sqlos.example.test/other-acs" }), "a response addressed to another ACS URL");
        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { InResponseTo = "_another-request" }), "a response to another AuthnRequest");
        await PostAsync(idp.BuildResponse(authnRequest, new SamlAssertion(ivan) { Expired = true }), "an assertion that expired");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.SetText("/samlp:Response/saml:Issuer", "urn:another-idp")), "a response from another issuer");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.Remove("//saml:Assertion/saml:Issuer")), "an assertion without an issuer");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.SetAttribute("//saml:Conditions", "NotBefore", inFuture)), "an assertion that is not valid yet");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.Remove("//saml:Conditions")), "an assertion without conditions");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.RemoveAttribute("//saml:Conditions", "NotOnOrAfter")), "conditions without an expiry");
        await PostAsync(
            forge.Build(authnRequest, ivan, SamlResponseForge.SetAttribute("//saml:SubjectConfirmation", "Method", "urn:oasis:names:tc:SAML:2.0:cm:holder-of-key")),
            "a holder-of-key subject confirmation");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.Remove("//saml:Subject/saml:NameID")), "an assertion without a NameID");
        await PostAsync(forge.Build(authnRequest, ivan, signing: new SamlResponseForge.Signing { SignatureMethod = SignedXml.XmlDsigRSASHA1Url }), "an RSA-SHA1 signature");
        await PostAsync(forge.Build(authnRequest, ivan, signing: new SamlResponseForge.Signing { DigestMethod = SignedXml.XmlDsigSHA1Url }), "a SHA-1 digest");
        await PostAsync(forge.Build(authnRequest, ivan, signing: new SamlResponseForge.Signing { ExtraTransform = true }), "an extra signature transform");
        await PostAsync(forge.Build(authnRequest, ivan, SamlResponseForge.RemoveAttribute("/samlp:Response", "ID")), "a response without an ID");
        await PostAsync(
            forge.Build(authnRequest, ivan, rewrite: xml => xml.Replace("samlp:Response", "samlp:LogoutResponse", StringComparison.Ordinal)),
            "a LogoutResponse instead of a Response");

        await t.ObserveAuditAsync("every refusal records its diagnostic");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task A_response_signed_on_the_assertion_or_carrying_a_failure_status_is_accepted()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp, autoLinkByEmail: true);
        var ivan = t.Unique.Email("ivan", domain);
        var forge = new SamlResponseForge(idp);

        var (_, first) = await SamlJourney.StartAsync(t, t.Browser, ivan);
        t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(first, new SamlAssertion(ivan, "Ivan", "Petrov") { SignAssertion = true }), first.RelayState)),
            "the IdP signs the assertion rather than the response: accepted");
        var second = t.NewBrowser("second-device");
        var (_, secondRequest) = await SamlJourney.StartAsync(t, second, ivan);
        t.Observe(
            await second.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                forge.Build(secondRequest, ivan, SamlResponseForge.SetAttribute("//samlp:StatusCode", "Value", "urn:oasis:names:tc:SAML:2.0:status:Responder")),
                secondRequest.RelayState)),
            "a signed response whose status is Responder but which carries a valid assertion: SqlOS does not read the status");

        await t.ObserveAuditAsync("both sign-ins");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_consumed_response_cannot_be_replayed_even_while_its_request_is_live()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        var outside = t.Unique.Email("ivan.personal");
        var (request, authnRequest) = await SamlJourney.StartAsync(t, t.Browser, ivan);
        await t.SkipAuditAsync();
        var acs = $"/sqlos/auth/saml/acs/{connectionId}";

        var outsideResponse = idp.BuildResponse(authnRequest, new SamlAssertion("idp-ivan", "Ivan", "Petrov") { Email = outside });
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(outsideResponse, authnRequest.RelayState)),
            "the IdP asserts an address outside Acme's verified domains: just-in-time provisioning refuses it (#420)");
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(outsideResponse, authnRequest.RelayState)),
            "replaying that response while the request is still live: it was already consumed");
        var good = t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(authnRequest, new SamlAssertion("idp-ivan", "Ivan", "Petrov") { Email = ivan }), authnRequest.RelayState)),
            "a fresh response asserting Ivan's address at the verified domain completes the request");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(good.NextUrlParameter("code"))), "redeem the code");

        await t.ObserveAuditAsync("link denial, replay refusal, and the sign-in");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task Malformed_saml_responses_fail_with_a_server_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise, EnterpriseHosting.AnswerUnhandledExceptionsLikeKestrel);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        var (_, authnRequest) = await SamlJourney.StartAsync(t, t.Browser, ivan);
        await t.SkipAuditAsync();
        var acs = $"/sqlos/auth/saml/acs/{connectionId}";
        t.Note("The host answers an exception that escapes an endpoint as Kestrel would: 500 with no body.");

        t.Observe(await t.PostFormAsync(acs, SamlJourney.AcsForm("%%% not base64 %%%", authnRequest.RelayState)), "a SAMLResponse that is not base64");
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(Convert.ToBase64String(Encoding.UTF8.GetBytes("not xml")), authnRequest.RelayState)),
            "base64 that is not XML");
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("<!DOCTYPE r [<!ENTITY x \"x\">]><samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\">&x;</samlp:Response>")),
                authnRequest.RelayState)),
            "a document with a DTD");

        await t.ObserveAuditAsync("nothing is audited for these");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Known defect #415 (7.2.1): SAML just-in-time provisioning creates a user and an organization
    /// membership but writes no user or membership audit record; the only records are the
    /// assurance evaluation and <c>user.login.saml</c>, written after the login save.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    public async Task Just_in_time_provisioning_writes_no_user_or_membership_audit_record_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);
        var (_, authnRequest) = await SamlJourney.StartAsync(t, t.Browser, ivan);
        await t.SkipAuditAsync();

        t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov")), authnRequest.RelayState)),
            "Ivan's first sign-in provisions his account and his Acme membership");
        await t.ObserveAuditAsync("no user.created or membership.created record, only the sign-in records");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/memberships", "dashboard: the membership exists");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_request_that_demands_fresh_authentication_asks_the_idp_to_force_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        var ivan = t.Unique.Email("ivan", domain);

        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["prompt"] = "login" });
        var page = t.Discard(await t.GetAsync(request.Url));
        var toIdp = t.Observe(await t.SubmitAsync(page.Form("/login/identify").With("email", ivan)), "identify on a prompt=login request");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(toIdp.NextUrl!);
        t.ObserveDocument("the AuthnRequest carries ForceAuthn", authnRequest.Xml);
        var acs = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(authnRequest, new SamlAssertion(ivan, "Ivan", "Petrov") { AuthnContextClassRef = "urn:oasis:names:tc:SAML:2.0:ac:classes:Password" }),
                authnRequest.RelayState)),
            "the IdP re-authenticates Ivan");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))), "redeem the code: auth_time is the assertion's AuthnInstant");

        await t.ApproveAsync();
    }

    private static string Challenge(AuthorizationRequest request)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
