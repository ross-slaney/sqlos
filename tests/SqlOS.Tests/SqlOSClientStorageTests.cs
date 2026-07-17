using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSClientStorageTests
{
    [TestMethod]
    public async Task CreateClientAsync_StoresManualRegistrationDefaults()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            "manual-client",
            "Manual Client",
            "sqlos",
            ["https://client.example.test/callback"]));

        client.RegistrationSource.Should().Be("manual");
        client.TokenEndpointAuthMethod.Should().Be("none");
        client.GrantTypesJson.Should().Contain("authorization_code");
        client.ResponseTypesJson.Should().Contain("code");
    }

    [TestMethod]
    public async Task UpsertSeededClientsAsync_DoesNotAdoptDashboardOwnedClient()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("seeded-client", "Seeded Client", "https://client.example.test/callback");
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = crypto.GenerateId("cli"),
            ClientId = "seeded-client",
            Name = "Existing Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        await context.SaveChangesAsync();

        var act = () => admin.UpsertSeededClientsAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owned by 'dashboard'*");

        var client = await context.Set<SqlOSClientApplication>().SingleAsync();
        client.Name.Should().Be("Existing Client");
        client.RegistrationSource.Should().Be("manual");
    }

    [TestMethod]
    public async Task UpsertSeededClientsAsync_ConfiguresExplicitConfidentialServiceClientWithoutRedirects()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.ClientSeeds.Add(new SqlOSClientSeedOptions
        {
            ClientId = "worker",
            Name = "Worker",
            Audience = "https://api.example.test/jobs",
            ClientType = "confidential",
            EnableClientCredentials = true,
            RequirePkce = false,
            AllowedScopes = ["jobs.run"]
        });
        var options = Options.Create(optionsValue);
        var admin = new SqlOSAdminService(context, options, TestCryptoService.Create(context, options));

        await admin.UpsertSeededClientsAsync();

        var client = await context.Set<SqlOSClientApplication>().SingleAsync();
        client.TokenEndpointAuthMethod.Should().Be("client_secret_basic");
        client.GrantTypesJson.Should().Be("[\"client_credentials\"]");
        client.RedirectUrisJson.Should().Be("[]");
        client.RequirePkce.Should().BeFalse();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
