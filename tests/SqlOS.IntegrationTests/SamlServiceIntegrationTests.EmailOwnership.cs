using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Email ownership at the SAML boundary: look-alike assertions never select another account.
/// </summary>
public sealed partial class SamlServiceIntegrationTests
{
    [TestMethod]
    public async Task SamlAssertion_ForLookAlikeDomain_DoesNotLinkTheVictim()
    {
        var (_, admin, saml) = CreateSamlServices();
        var unique = Guid.NewGuid().ToString("N")[..12];
        var victimEmail = $"bob{unique}@business.example";
        var lookAlike = $"bob{unique}@busineß.example";
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", victimEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(victim.Id);
        // The tenant legitimately owns the internationalized look-alike domain.
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Look-alike Org {unique}", null, PrimaryDomain: "busineß.example"));
        await AddVerifiedDomainAsync(organization.Id, "xn--busine-gta.example");
        using var certificate = CreateCertificate("CN=SqlOSLookAlikeIdP");
        var connection = await CreatePolicySamlConnectionAsync(admin, organization.Id, certificate, "lookalike", autoProvisionUsers: true, autoLinkByEmail: true);
        var client = await CreateSamlClientAsync(admin, "lookalike");
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);

        await CaptureAcsAsync(
            saml,
            connection.Id,
            BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, lookAlike, "Look", "Alike", flow, nameId: $"lookalike-{unique}"),
            flow.RelayState);

        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().AnyAsync(x => x.UserId == victim.Id)).Should().BeFalse();
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .AnyAsync(x => x.UserId == victim.Id && x.OrganizationId == organization.Id))
            .Should().BeFalse("a look-alike assertion must never be linked onto {0}", victimEmail);
    }

    private static async Task AddVerifiedDomainAsync(string organizationId, string domain)
    {
        AspireFixture.SharedContext.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
        {
            Id = $"dom_{Guid.NewGuid():N}"[..28],
            OrganizationId = organizationId,
            Domain = domain,
            Status = SqlOSOrganizationDomainStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow
        });
        await AspireFixture.SharedContext.SaveChangesAsync();
    }

    private static X509Certificate2 CreateCertificate(string subjectName)
    {
        // The key stays alive with the certificate for signing later in the test.
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
