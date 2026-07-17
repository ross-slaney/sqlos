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
public sealed class ControlPlaneParityPersistenceIntegrationTests
{
    [TestMethod]
    public async Task CodeOwnedClient_ReconcilesAcrossRealSqlContextsWithoutDuplicateStateOrAudit()
    {
        await using var firstContext = await AspireFixture.CreateIsolatedAuthContextAsync("ParityPersist");
        try
        {
            var connectionString = firstContext.Database.GetConnectionString();
            var optionsValue = new SqlOSAuthServerOptions();
            optionsValue.SeedClient(seed =>
            {
                seed.ClientId = "persisted-parity";
                seed.Name = "Persisted Parity";
                seed.RedirectUris = ["https://parity.example.test/callback"];
                seed.AllowedScopes = ["openid", "profile"];
            });
            var options = Options.Create(optionsValue);
            var firstAdmin = new SqlOSAdminService(firstContext, options, new SqlOSCryptoService(firstContext, options));
            await firstAdmin.UpsertSeededClientsAsync();
            var firstFingerprint = (await firstContext.Set<SqlOSClientApplication>().AsNoTracking().SingleAsync()).ConfigurationFingerprint;

            await using var secondContext = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>().UseSqlServer(connectionString).Options);
            var secondAdmin = new SqlOSAdminService(secondContext, options, new SqlOSCryptoService(secondContext, options));
            await secondAdmin.UpsertSeededClientsAsync();

            var persisted = await secondContext.Set<SqlOSClientApplication>().AsNoTracking().SingleAsync();
            persisted.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
            persisted.ConfigurationSourceKey.Should().Be("persisted-parity");
            persisted.ConfigurationFingerprint.Should().Be(firstFingerprint);
            (await secondContext.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled"))
                .Should().Be(1, "an unchanged reconciliation in another process must remain idempotent");
        }
        finally
        {
            await firstContext.Database.EnsureDeletedAsync();
        }
    }
}
