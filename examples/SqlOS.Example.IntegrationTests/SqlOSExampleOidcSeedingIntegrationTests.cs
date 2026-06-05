using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExampleOidcSeedingIntegrationTests
{
    private const string SeededClientId = "seeded-microsoft-client-id";

    [TestMethod]
    public async Task SeededMicrosoftConnection_AppearsAsEnabledProvider()
    {
        using var factory = CreateSeededFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var providersResponse = await client.GetAsync("/api/v1/auth/oidc/providers");
        providersResponse.EnsureSuccessStatusCode();
        var providers = JsonDocument.Parse(await providersResponse.Content.ReadAsStringAsync());

        var microsoft = providers.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("providerType").GetString() == "Microsoft");
        microsoft.GetProperty("displayName").GetString().Should().Be("Microsoft");
        microsoft.GetProperty("logoDataUrl").GetString().Should().StartWith("data:image/svg+xml");
    }

    [TestMethod]
    public async Task SeededMicrosoftConnection_PersistsConfiguredValues()
    {
        using var factory = CreateSeededFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var connectionsResponse = await client.GetAsync("/sqlos/admin/auth/api/oidc-connections");
        connectionsResponse.EnsureSuccessStatusCode();
        var connections = JsonDocument.Parse(await connectionsResponse.Content.ReadAsStringAsync());

        var microsoft = connections.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("providerType").GetString() == "Microsoft");

        microsoft.GetProperty("clientId").GetString().Should().Be(SeededClientId);
        microsoft.GetProperty("microsoftTenant").GetString().Should().Be("common");
        microsoft.GetProperty("useDiscovery").GetBoolean().Should().BeTrue();
        microsoft.GetProperty("isEnabled").GetBoolean().Should().BeTrue();

        // The {connectionId} placeholder is replaced with the generated connection id.
        var connectionId = microsoft.GetProperty("id").GetString()!;
        var callbacks = JsonSerializer.Deserialize<List<string>>(microsoft.GetProperty("allowedCallbackUris").GetString()!)!;
        callbacks.Should().Contain($"http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}");
    }

    private static WebApplicationFactory<Program> CreateSeededFactory()
        => ExampleApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("SqlOS:Issuer", "http://localhost:5062/sqlos/auth");
            builder.UseSetting("SqlOS:Oidc:Microsoft:ClientId", SeededClientId);
            builder.UseSetting("SqlOS:Oidc:Microsoft:ClientSecret", "seeded-microsoft-secret");
            builder.UseSetting("SqlOS:Oidc:Microsoft:Tenant", "common");
        });
}
