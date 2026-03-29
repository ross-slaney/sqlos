using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSClientResolutionTests
{
    [TestMethod]
    public async Task ResolveRequiredClientAsync_ReturnsStoredClient_AndUpdatesLastSeen()
    {
        using var context = CreateContext();
        var client = new SqlOSClientApplication
        {
            Id = "cli_test",
            ClientId = "test-client",
            Name = "Test Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();

        var resolver = CreateResolver(context);
        var resolved = await resolver.ResolveRequiredClientAsync("test-client", "https://client.example.test/callback");

        resolved.Client.Id.Should().Be(client.Id);
        resolved.ResolutionKind.Should().Be("stored");
        client.LastSeenAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_PrefersStoredClient_ForMetadataLikeClientIds()
    {
        using var context = CreateContext();
        var client = new SqlOSClientApplication
        {
            Id = "cli_metadata",
            ClientId = "https://client.example.test/oauth/client.json",
            Name = "Metadata Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();

        var resolver = CreateResolver(context);
        var resolved = await resolver.ResolveRequiredClientAsync(
            "https://client.example.test/oauth/client.json",
            "https://client.example.test/callback");

        resolved.Client.Id.Should().Be(client.Id);
        resolved.ResolutionKind.Should().Be("stored");
    }

    [TestMethod]
    public async Task ResolveRequiredClientAsync_Throws_ForInactiveClients()
    {
        using var context = CreateContext();
        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_inactive",
            ClientId = "inactive-client",
            Name = "Inactive Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = false
        });
        await context.SaveChangesAsync();

        var resolver = CreateResolver(context);

        var act = async () => await resolver.ResolveRequiredClientAsync("inactive-client", "https://client.example.test/callback");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inactive*");
    }

    private static SqlOSClientResolutionService CreateResolver(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        return new SqlOSClientResolutionService(context, options);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
