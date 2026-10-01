using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Who a valid SAML assertion signs in: a bound subject, an existing account linked by a verified
/// or claimed email at the organization's domain, or a just-in-time user, and every lifecycle and
/// ownership rule that refuses one (#420, #423).
/// </summary>
[TestClass]
public sealed class SamlIdentityScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task A_returning_subject_signs_in_by_binding_and_a_new_subject_cannot_take_over_the_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp, autoLinkByEmail: true);
        var ivan = t.Unique.Email("ivan", domain);
        var renamed = t.Unique.Email("ivan.renamed", domain);
        var acs = $"/sqlos/auth/saml/acs/{connectionId}";

        var (_, first) = await SamlJourney.StartAsync(t, t.NewBrowser("first-visit"), ivan);
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(first, new SamlAssertion("idp-subject-1", "Ivan", "Petrov") { Email = ivan }), first.RelayState)),
            "first sign-in: subject idp-subject-1 is provisioned and bound");
        var (_, second) = await SamlJourney.StartAsync(t, t.NewBrowser("second-visit"), ivan);
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(second, new SamlAssertion("idp-subject-1", "Ivan", "Petrov") { Email = renamed }), second.RelayState)),
            "the same subject with a new email attribute still signs in as Ivan; the binding decides, not the email");
        var (_, third) = await SamlJourney.StartAsync(t, t.NewBrowser("third-visit"), ivan);
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(third, new SamlAssertion("idp-subject-2", "Mallory", "Impostor") { Email = ivan }), third.RelayState)),
            "a different subject asserting Ivan's address is refused: the connection already has Ivan's binding");

        await t.ObserveAuditAsync("sign-ins and the link denial");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/sso/authorization-url")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_second_connection_links_the_existing_verified_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var entra = new TestSamlIdentityProvider("urn:behavior-lock:entra", "https://entra.example.test/sso");
        using var okta = new TestSamlIdentityProvider("urn:behavior-lock:okta", "https://okta.example.test/sso");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var entraConnection = await t.Setup.CreateSamlConnectionAsync(acme, entra);
        var ivan = t.Unique.Email("ivan", domain);
        var (_, first) = await SamlJourney.StartAsync(t, t.NewBrowser("before-migration"), ivan);
        var provisioned = t.Discard(await t.PostFormAsync($"/sqlos/auth/saml/acs/{entraConnection}", SamlJourney.AcsForm(
            entra.BuildResponse(first, new SamlAssertion("entra-ivan", "Ivan", "Petrov") { Email = ivan }), first.RelayState)));
        _ = provisioned.NextUrlParameter("code");
        var oktaConnection = await t.Setup.CreateSamlConnectionAsync(acme, okta, autoProvisionUsers: false);
        await t.SkipAuditAsync();
        t.Note("Ivan was provisioned through Acme's Entra connection; Acme then added an Okta connection without just-in-time provisioning.");

        var request = t.Urls.Authorize();
        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/sso/authorization-url", new
            {
                connectionId = oktaConnection,
                clientId = request.ClientId,
                redirectUri = request.RedirectUri,
                state = request.State,
                codeChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                codeChallengeMethod = "S256"
            }),
            "start at the Okta connection");
        var authnRequest = TestSamlIdentityProvider.ReadRedirect(started.JsonString("authorizationUrl"));
        var acs = t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{oktaConnection}", SamlJourney.AcsForm(
                okta.BuildResponse(authnRequest, new SamlAssertion("okta-ivan", "Ivan", "Petrov") { Email = ivan }), authnRequest.RelayState)),
            "Okta's new subject is linked to Ivan's verified account at Acme's domain");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))), "redeem the code: the same user");

        await t.ObserveAuditAsync("the link and sign-in");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /__probe/auth/password-login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_saml_sign_in_claims_an_unverified_account_and_revokes_what_was_attached_before_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var ceoEmail = t.Unique.Email("ceo", domain);
        var squatterPassword = t.Unique.Password("squatter");
        var created = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/users", new { displayName = "Squatter", email = ceoEmail, password = squatterPassword });
        var account = new ScenarioUser(created.JsonString("id"), ceoEmail, squatterPassword, "Squatter");
        t.Scrub(account.Id, "usr", "ceo");
        var squatterSession = await t.Setup.SignInWithPasswordAsync(account, browser: t.NewBrowser("squatter"));
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp);
        t.Note("Before Acme set up SSO, someone registered the CEO's address with a password and never verified it; that session is still live.");

        var probe = t.NewClient("probe");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = ceoEmail, password = squatterPassword, clientId = BehaviorLockConstants.AppClientId }),
            "the squatter's password works before the claim");
        var ceo = t.NewBrowser("ceo");
        var (request, authnRequest) = await SamlJourney.StartAsync(t, ceo, ceoEmail);
        var acs = t.Observe(
            await ceo.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(authnRequest, new SamlAssertion("idp-ceo", "Cara", "Ceo") { Email = ceoEmail }), authnRequest.RelayState)),
            "Acme's IdP asserts the CEO's address: the sign-in claims the unverified account");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(acs.NextUrlParameter("code"))), "the CEO redeems the code");
        t.Observe(
            await probe.PostJsonAsync("/__probe/auth/password-login", new { email = ceoEmail, password = squatterPassword, clientId = BehaviorLockConstants.AppClientId }),
            "the squatter's password was revoked by the claim");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = squatterSession.RefreshToken,
                ["client_id"] = BehaviorLockConstants.AppClientId
            }),
            "and so was the squatter's session");

        await t.ObserveAuditAsync("the claim names what it revoked");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/sso/authorization-url")]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    public async Task Assertions_that_cannot_prove_an_email_or_provision_a_user_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        idp.RegisterWith(t);
        var connection = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/sso-connections", new
        {
            organizationId = acme.Id,
            displayName = "Acme SAML",
            identityProviderEntityId = idp.EntityId,
            singleSignOnUrl = idp.SingleSignOnUrl,
            x509CertificatePem = idp.CertificatePem,
            autoProvisionUsers = false,
            autoLinkByEmail = false,
            emailAttributeName = "mail",
            firstNameAttributeName = "first_name",
            lastNameAttributeName = "last_name"
        });
        var connectionId = connection.JsonString("id");
        var forge = new SamlResponseForge(idp);
        var ivan = t.Unique.Email("ivan", domain);
        t.Note("Acme's connection reads the email from a 'mail' attribute and does not provision users just in time.");

        async Task<SamlAuthnRequest> StartAsync(string caption)
        {
            var request = t.Urls.Authorize();
            var started = t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/sso/authorization-url", new
                {
                    connectionId,
                    clientId = request.ClientId,
                    redirectUri = request.RedirectUri,
                    state = request.State,
                    codeChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    codeChallengeMethod = "S256"
                }),
                caption);
            return TestSamlIdentityProvider.ReadRedirect(started.JsonString("authorizationUrl"));
        }

        var first = await StartAsync("start a sign-in at Acme's connection");
        t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(idp.BuildResponse(first, new SamlAssertion(ivan, "Ivan", "Petrov")), first.RelayState)),
            "the assertion has no 'mail' attribute: nothing proves an email");
        var second = await StartAsync("start another");
        t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                forge.Build(second, ivan, SamlResponseForge.SetAttribute("//saml:Attribute[@Name='email']", "Name", "mail")), second.RelayState)),
            "'mail' asserts Ivan's address, but Ivan has no account and provisioning is off");
        var third = await StartAsync("start a third");
        t.Observe(
            await t.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                forge.Build(third, ivan, (document, ns) =>
                {
                    SamlResponseForge.SetAttribute("//saml:Attribute[@Name='email']", "Name", "mail")(document, ns);
                    SamlResponseForge.SetText("//saml:Attribute[@Name='mail']/saml:AttributeValue", "not an email address")(document, ns);
                }), third.RelayState)),
            "'mail' carries something that is not an address");

        await t.ObserveAuditAsync("each link denial names its reason");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /scim/v2/Users")]
    [Covers("PATCH /scim/v2/Users/{id}")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    public async Task Inactive_users_memberships_and_organizations_cannot_sign_in_through_saml()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        using var idp = new TestSamlIdentityProvider();
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var connectionId = await t.Setup.CreateSamlConnectionAsync(acme, idp, autoLinkByEmail: true);
        var directory = await t.CreateDirectoryAsync(acme);
        var scim = t.NewClient("directory");
        var ann = t.Unique.Email("ann", domain);
        var bob = t.Unique.Email("bob", domain);
        var ivan = t.Unique.Email("ivan", domain);
        var acs = $"/sqlos/auth/saml/acs/{connectionId}";

        var annId = t.Observe(await scim.PostAsync($"{Scim.Root}/Users", Scim.User(ann, "directory-ann", "Ann", "Archer", ann), directory.Token), "the directory provisions Ann").JsonString("id");
        t.Scrub(annId, "usr", "ann");
        var bobId = t.Observe(await scim.PostAsync($"{Scim.Root}/Users", Scim.User(bob, "directory-bob", "Bob", "Baker", bob), directory.Token), "and Bob").JsonString("id");
        t.Scrub(bobId, "usr", "bob");
        t.Observe(
            await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/organizations/{globex.Id}/memberships", new { userId = bobId, role = "member" }),
            "Bob also belongs to Globex");
        t.Observe(await scim.PatchAsync($"{Scim.Root}/Users/{annId}", Scim.Patch(("replace", "active", JsonValue.Create(false))), directory.Token), "the directory deactivates Ann: her account is deactivated");
        t.Observe(await scim.PatchAsync($"{Scim.Root}/Users/{bobId}", Scim.Patch(("replace", "active", JsonValue.Create(false))), directory.Token), "and Bob: only his Acme membership ends");

        var (_, annRequest) = await SamlJourney.StartAsync(t, t.NewBrowser("ann"), ann);
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(annRequest, new SamlAssertion("idp-ann", "Ann", "Archer") { Email = ann }), annRequest.RelayState)),
            "Ann's assertion: her account is inactive");
        var (_, bobRequest) = await SamlJourney.StartAsync(t, t.NewBrowser("bob"), bob);
        t.Observe(
            await t.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(bobRequest, new SamlAssertion("idp-bob", "Bob", "Baker") { Email = bob }), bobRequest.RelayState)),
            "Bob's assertion: his Acme membership is inactive");
        var ivanBrowser = t.NewBrowser("ivan");
        var (_, ivanRequest) = await SamlJourney.StartAsync(t, ivanBrowser, ivan);
        t.Observe(
            await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}", new { name = acme.Name, slug = acme.Slug, isActive = false }),
            "while Ivan is at the IdP, the operator deactivates Acme");
        t.Observe(
            await ivanBrowser.PostFormAsync(acs, SamlJourney.AcsForm(idp.BuildResponse(ivanRequest, new SamlAssertion("idp-ivan", "Ivan", "Petrov") { Email = ivan }), ivanRequest.RelayState)),
            "Ivan's assertion: the organization is inactive");

        await t.ObserveAuditAsync("lifecycle denials");
        await t.ApproveAsync();
    }
}
