using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SamlSeedReconciliationIntegrationTests
{
    [TestMethod]
    public async Task ConcurrentRealSqlStartup_CreatesOneSeededSamlConnectionAndOneAudit()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("SamlSeedRace");
        try
        {
            var setupOptions = Options.Create(new SqlOSAuthServerOptions());
            var setupAdmin = new SqlOSAdminService(setup, setupOptions, new SqlOSCryptoService(setup, setupOptions));
            var organization = await setupAdmin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Concurrent SAML", "concurrent-saml"));
            var optionsValue = new SqlOSAuthServerOptions();
            optionsValue.SeedSamlConnection("workforce", seed =>
            {
                seed.OrganizationId = organization.Id;
                seed.DisplayName = "Concurrent workforce";
                seed.IdentityProviderEntityId = "urn:concurrent-workforce:idp";
                seed.SingleSignOnUrl = "https://idp.example.test/sso";
                seed.X509CertificatePem = CreateCertificatePem();
            });
            var connectionString = setup.Database.GetConnectionString()!;
            var contexts = Enumerable.Range(0, 4).Select(_ => new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>().UseSqlServer(connectionString).Options)).ToList();
            try
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var tasks = contexts.Select(context => Task.Run(async () =>
                {
                    var options = Options.Create(optionsValue);
                    var admin = new SqlOSAdminService(context, options, new SqlOSCryptoService(context, options));
                    await ready.Task;
                    await admin.UpsertSeededSamlConnectionsAsync();
                })).ToArray();
                ready.SetResult();
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally
            {
                foreach (var context in contexts) await context.DisposeAsync();
            }

            setup.ChangeTracker.Clear();
            var connection = await setup.Set<SqlOSSsoConnection>().AsNoTracking().SingleAsync();
            connection.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
            connection.ConfigurationSourceKey.Should().Be("workforce");
            (await setup.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled"
                && x.DataJson != null && x.DataJson.Contains("saml_connection"))).Should().Be(1);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    private static string CreateCertificatePem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ConcurrentSamlSeed", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.ExportCertificatePem();
    }
}
