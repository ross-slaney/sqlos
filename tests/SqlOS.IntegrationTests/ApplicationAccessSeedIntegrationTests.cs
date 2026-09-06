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
public sealed class ApplicationAccessSeedIntegrationTests
{
    [TestMethod]
    public async Task ApplicationAccessSeeds_ConcurrentRealSqlStartupIsIdempotentAndPreservesDashboardRows()
    {
        await using var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("AppAccessSeed");
        try
        {
            var connectionString = setupContext.Database.GetConnectionString()!;
            var optionsValue = new SqlOSAuthServerOptions();
            var setupOptions = Options.Create(optionsValue);
            var setupAdmin = new SqlOSAdminService(setupContext, setupOptions, new SqlOSCryptoService(setupContext, setupOptions));
            var organization = await setupAdmin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Seeded access", "seeded-access"));
            optionsValue.SeedClient(client =>
            {
                client.ClientId = "real-sql-seeded-app";
                client.Name = "Real SQL seeded app";
                client.RedirectUris = ["https://app.example.test/callback"];
                client.AccessMode = SqlOSApplicationAccessModes.SelectedOrganizations;
                client.AssignOrganization("primary-org", organization.Slug, description: "seeded entitlement");
            });
            await setupAdmin.UpsertSeededClientsAsync();
            var dashboard = await setupAdmin.AssignApplicationAsync(
                "real-sql-seeded-app",
                new SqlOSCreateApplicationAssignmentRequest(
                    SqlOSApplicationAssignmentPrincipalTypes.Organization,
                    OrganizationId: organization.Id,
                    Access: SqlOSApplicationAssignmentAccess.Denied,
                    Reason: "operator exception"));

            var contexts = Enumerable.Range(0, 4)
                .Select(_ => new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(connectionString).Options))
                .ToList();
            try
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var runs = contexts.Select(context => Task.Run(async () =>
                {
                    await ready.Task;
                    var options = Options.Create(optionsValue);
                    var admin = new SqlOSAdminService(context, options, new SqlOSCryptoService(context, options));
                    await admin.UpsertSeededClientsAsync();
                })).ToArray();
                ready.SetResult();
                await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally
            {
                foreach (var context in contexts) await context.DisposeAsync();
            }

            setupContext.ChangeTracker.Clear();
            var client = await setupContext.Set<SqlOSClientApplication>().AsNoTracking().SingleAsync(x => x.ClientId == "real-sql-seeded-app");
            client.AccessMode.Should().Be(SqlOSApplicationAccessModes.SelectedOrganizations);
            var assignments = await setupContext.Set<SqlOSApplicationAssignment>().AsNoTracking().Where(x => x.ClientApplicationId == client.Id).ToListAsync();
            assignments.Should().HaveCount(2);
            assignments.Single(x => x.ConfigurationSourceKey == "primary-org").ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
            assignments.Single(x => x.Id == dashboard.Id).ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Dashboard);
            (await setupContext.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled" && x.DataJson != null && x.DataJson.Contains("application_assignment")))
                .Should().Be(1, "no-op concurrent startup must not duplicate assignment reconciliation evidence");
        }
        finally
        {
            await setupContext.Database.EnsureDeletedAsync();
        }
    }
}
