using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Upstream MFA trust: when an organization requires MFA, a SAML sign-in satisfies it only if the
/// connection trusts upstream MFA and the assertion's AuthnContextClassRef is one the connection
/// accepts; otherwise SqlOS asks for its own second factor.
/// </summary>
[TestClass]
public sealed class SamlAssuranceScenarios
{
    private const string MultipleAuthn = "http://schemas.microsoft.com/claims/multipleauthn";
    private const string PasswordProtectedTransport = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport";

    [Scenario]
    [Covers("POST /sqlos/auth/saml/acs/{connectionId}")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_organization_that_requires_mfa_accepts_only_the_upstream_mfa_its_connection_trusts()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Enterprise);
        using var idp = new TestSamlIdentityProvider();
        idp.RegisterWith(t);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.Setup.OperatorPostAsync("/sqlos/admin/auth/api/sso-connections", new
        {
            organizationId = acme.Id,
            displayName = "Acme Entra",
            identityProviderEntityId = idp.EntityId,
            singleSignOnUrl = idp.SingleSignOnUrl,
            x509CertificatePem = idp.CertificatePem,
            autoProvisionUsers = true,
            autoLinkByEmail = true,
            emailAttributeName = "email",
            firstNameAttributeName = "first_name",
            lastNameAttributeName = "last_name",
            trustUpstreamMfa = true,
            acceptedAuthnContextClassRefs = new[] { MultipleAuthn }
        });
        var connectionId = connection.JsonString("id");
        var policy = t.Discard(await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/mfa-policy", new
        {
            isEnabled = true,
            requireMfaForAllUsers = true,
            requireMfaForOwnersAndAdmins = true,
            userSelfEnrollmentEnabled = true,
            recoveryCodesEnabled = true
        }));
        EnterpriseSetup.EnsureSucceeded(policy);
        await t.SkipAuditAsync();
        var ivan = t.Unique.Email("ivan", domain);
        t.Note("Acme requires MFA for everyone; its connection trusts upstream MFA proven by " + MultipleAuthn + ".");

        var withMfa = t.NewBrowser("with-mfa");
        var (request, first) = await SamlJourney.StartAsync(t, withMfa, ivan);
        var accepted = t.Observe(
            await withMfa.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(first, new SamlAssertion("idp-ivan", "Ivan", "Petrov") { Email = ivan, AuthnContextClassRef = MultipleAuthn }), first.RelayState)),
            "the IdP proves MFA with an accepted AuthnContextClassRef: the code is issued");
        t.Observe(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(accepted.NextUrlParameter("code"))), "the tokens record the upstream MFA");

        var passwordOnly = t.NewBrowser("password-only");
        var (_, second) = await SamlJourney.StartAsync(t, passwordOnly, ivan);
        t.Observe(
            await passwordOnly.PostFormAsync($"/sqlos/auth/saml/acs/{connectionId}", SamlJourney.AcsForm(
                idp.BuildResponse(second, new SamlAssertion("idp-ivan", "Ivan", "Petrov") { Email = ivan, AuthnContextClassRef = PasswordProtectedTransport }), second.RelayState)),
            "a password-only assertion does not satisfy the policy: SqlOS asks for its own second factor");

        await t.ObserveAuditAsync("the assurance evaluations");
        await t.ApproveAsync();
    }
}
