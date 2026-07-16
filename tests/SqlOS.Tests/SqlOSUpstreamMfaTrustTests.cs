using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Security;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSUpstreamMfaTrustTests
{
    [TestMethod]
    public void OidcMfaClaim_WithoutExplicitTrust_IsNotAccepted()
    {
        var connection = new SqlOSOidcConnection
        {
            TrustUpstreamMfa = false,
            AcceptedAmrValuesJson = """["mfa"]"""
        };
        var principal = Principal(new Claim("amr", "mfa"));

        var decision = SqlOSUpstreamMfaTrust.EvaluateOidc(connection, principal);

        decision.EvidencePresent.Should().BeTrue();
        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Be("trust_disabled");
    }

    [TestMethod]
    public void OidcTrustedAmrArray_IsAcceptedAndSatisfiesStrongMfa()
    {
        var connection = new SqlOSOidcConnection
        {
            TrustUpstreamMfa = true,
            AcceptedAmrValuesJson = """["mfa"]"""
        };
        var principal = Principal(new Claim("amr", """["pwd","mfa"]"""));

        var decision = SqlOSUpstreamMfaTrust.EvaluateOidc(connection, principal);
        var policy = new SqlOSMfaPolicyService(Options.Create(new SqlOSAuthServerOptions()));

        decision.Accepted.Should().BeTrue();
        decision.AcceptedClaim.Should().Be("amr");
        decision.AcceptedValue.Should().Be("mfa");
        policy.SatisfiesStrongMfa("oidc+upstream_mfa").Should().BeTrue();
    }

    [TestMethod]
    public void OidcTrustedAcr_UsesExactConfiguredValue()
    {
        var connection = new SqlOSOidcConnection
        {
            TrustUpstreamMfa = true,
            AcceptedAcrValuesJson = """["urn:example:loa:2"]"""
        };

        var accepted = SqlOSUpstreamMfaTrust.EvaluateOidc(
            connection,
            Principal(new Claim("acr", "urn:example:loa:2")));
        var rejected = SqlOSUpstreamMfaTrust.EvaluateOidc(
            connection,
            Principal(new Claim("acr", "URN:EXAMPLE:LOA:2")));

        accepted.Accepted.Should().BeTrue();
        rejected.Accepted.Should().BeFalse();
        rejected.Reason.Should().Be("evidence_unrecognized");
    }

    [TestMethod]
    public void SamlAuthnContext_RequiresExactPerConnectionTrust()
    {
        var connection = new SqlOSSsoConnection
        {
            TrustUpstreamMfa = true,
            AcceptedAuthnContextClassRefsJson =
                """["urn:oasis:names:tc:SAML:2.0:ac:classes:TimeSyncToken"]"""
        };

        var accepted = SqlOSUpstreamMfaTrust.EvaluateSaml(
            connection,
            ["urn:oasis:names:tc:SAML:2.0:ac:classes:TimeSyncToken"]);
        var rejected = SqlOSUpstreamMfaTrust.EvaluateSaml(
            connection,
            ["urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"]);

        accepted.Accepted.Should().BeTrue();
        accepted.AcceptedClaim.Should().Be("saml_authn_context");
        rejected.Accepted.Should().BeFalse();
    }

    [TestMethod]
    public void MissingOrMalformedPolicy_FailsClosed()
    {
        var connection = new SqlOSOidcConnection
        {
            TrustUpstreamMfa = true,
            AcceptedAmrValuesJson = "{not-json"
        };

        var decision = SqlOSUpstreamMfaTrust.EvaluateOidc(
            connection,
            Principal(new Claim("amr", "mfa")));

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Be("evidence_unrecognized");
    }

    [TestMethod]
    public void OidcOversizedEvidence_FailsClosedWithoutRetainingOversizedValue()
    {
        var connection = new SqlOSOidcConnection
        {
            TrustUpstreamMfa = true,
            AcceptedAmrValuesJson = """["mfa"]"""
        };
        var principal = Principal(
            new Claim("amr", "mfa"),
            new Claim("amr", new string('x', 501)));

        var decision = SqlOSUpstreamMfaTrust.EvaluateOidc(connection, principal);

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Be("evidence_limits_exceeded");
        decision.AmrValues.Should().Equal("mfa");
    }

    [TestMethod]
    public void SamlTooManyEvidenceValues_FailsClosedAndBoundsAuditEvidence()
    {
        var connection = new SqlOSSsoConnection
        {
            TrustUpstreamMfa = true,
            AcceptedAuthnContextClassRefsJson = """["context-0"]"""
        };
        var evidence = Enumerable.Range(0, 33).Select(index => $"context-{index}").ToArray();

        var decision = SqlOSUpstreamMfaTrust.EvaluateSaml(connection, evidence);

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Be("evidence_limits_exceeded");
        decision.SamlAuthnContextClassRefs.Should().HaveCount(32);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));
}
