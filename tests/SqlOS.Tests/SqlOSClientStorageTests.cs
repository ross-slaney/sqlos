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
        var crypto = new SqlOSCryptoService(context, options);
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
    public async Task UpsertSeededClientsAsync_BackfillsSeededRegistrationSource()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("seeded-client", "Seeded Client", "https://client.example.test/callback");
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
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

        await admin.UpsertSeededClientsAsync();

        var client = await context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == "seeded-client");

        client.RegistrationSource.Should().Be("seeded");
        client.TokenEndpointAuthMethod.Should().Be("none");
        client.GrantTypesJson.Should().Contain("authorization_code");
        client.ResponseTypesJson.Should().Contain("code");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
