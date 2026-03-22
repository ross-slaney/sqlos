using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthorizationServerMetadataTests
{
    [TestMethod]
    public void EnablePortableMcpClients_SetsExpectedDefaults()
    {
        var options = new SqlOSAuthServerOptions();

        options.EnablePortableMcpClients();

        options.ClientRegistration.Cimd.Enabled.Should().BeTrue();
        options.ClientRegistration.Dcr.Enabled.Should().BeFalse();
        options.ResourceIndicators.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void EnableChatGptCompatibility_SetsExpectedDefaults()
    {
        var options = new SqlOSAuthServerOptions();

        options.EnableChatGptCompatibility();

        options.ClientRegistration.Dcr.Enabled.Should().BeTrue();
        options.ResourceIndicators.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void EnableVsCodeCompatibility_PreservesLoopbackSupport()
    {
        var options = new SqlOSAuthServerOptions();
        options.ClientRegistration.Dcr.AllowLoopbackRedirectUris = false;

        options.EnableVsCodeCompatibility();

        options.ClientRegistration.Dcr.Enabled.Should().BeTrue();
        options.ClientRegistration.Dcr.AllowLoopbackRedirectUris.Should().BeTrue();
        options.ResourceIndicators.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetMetadataAsync_IncludesCapabilityFlags_WhenEnabled()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.EnablePortableMcpClients();
        optionsValue.EnableChatGptCompatibility();

        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        var json = JsonSerializer.Serialize(metadata);

        json.Should().Contain("\"registration_endpoint\":\"https://app.example.com/sqlos/auth/register\"");
        json.Should().Contain("\"client_id_metadata_document_supported\":true");
        json.Should().Contain("\"resource_parameter_supported\":true");
        json.Should().Contain("\"authorization_endpoint\":\"https://app.example.com/sqlos/auth/authorize\"");
        json.Should().Contain("\"token_endpoint\":\"https://app.example.com/sqlos/auth/token\"");
        json.Should().Contain("\"jwks_uri\":\"https://app.example.com/sqlos/auth/.well-known/jwks.json\"");
        json.Should().Contain("\"code_challenge_methods_supported\":[\"S256\"]");
        json.Should().Contain("\"token_endpoint_auth_methods_supported\":[\"none\"]");
    }

    [TestMethod]
    public async Task GetMetadataAsync_OmitsOptionalCapabilityFields_WhenDisabled()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.ClientRegistration.Cimd.Enabled = false;
        optionsValue.ClientRegistration.Dcr.Enabled = false;
        optionsValue.ResourceIndicators.Enabled = false;

        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        var json = JsonSerializer.Serialize(metadata);

        json.Should().NotContain("registration_endpoint");
        json.Should().NotContain("client_id_metadata_document_supported");
        json.Should().NotContain("resource_parameter_supported");
    }

    private static async Task<SqlOSAuthorizationServerService> CreateAuthorizationServerServiceAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings);

        await settings.EnsureDefaultAuthPageSettingsAsync();
        await admin.UpsertSeededClientsAsync();

        return new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
