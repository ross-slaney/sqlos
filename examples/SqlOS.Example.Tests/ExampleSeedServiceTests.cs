using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Example.Api.Configuration;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Seeding;
using SqlOS.Fga.Services;

namespace SqlOS.Example.Tests;

[TestClass]
public sealed class ExampleSeedServiceTests
{
    [TestMethod]
    public async Task SeedAsync_CreatesExampleClient()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var fgaSeedService = new SqlOSFgaSeedService(context, Options.Create(new SqlOS.Fga.Configuration.SqlOSFgaOptions()), NullLogger<SqlOSFgaSeedService>.Instance);
        var webOptions = Options.Create(new ExampleWebOptions());
        var seedService = new ExampleSeedService(context, admin, fgaSeedService, webOptions);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.EnsureDefaultClientAsync();
        await seedService.SeedAsync();

        var client = await context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == "example-web");
        client.Audience.Should().Be("sqlos-example");
    }

    private static ExampleAppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ExampleAppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ExampleAppDbContext(options);
    }
}
