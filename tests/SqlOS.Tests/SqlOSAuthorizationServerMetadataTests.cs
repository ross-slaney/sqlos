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

    [TestMethod]
    public async Task GetMetadataAsync_OmitsOpenIdProviderFields_WhenProviderDisabled()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.ConfigureOpenIdProvider(provider => provider.Enabled = false);

        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        var json = JsonSerializer.Serialize(metadata);

        json.Should().NotContain("userinfo_endpoint");
        json.Should().NotContain("subject_types_supported");
        json.Should().NotContain("id_token_signing_alg_values_supported");
        json.Should().NotContain("claims_supported");
    }

    [TestMethod]
    public async Task GetMetadataAsync_OmitsOnlyUserInfoEndpoint_WhenUserInfoDisabled()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.ConfigureOpenIdProvider(provider => provider.EnableUserInfoEndpoint = false);

        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        var json = JsonSerializer.Serialize(metadata);

        json.Should().NotContain("userinfo_endpoint");
        json.Should().Contain("\"subject_types_supported\":[\"public\"]");
        json.Should().Contain("\"id_token_signing_alg_values_supported\":[\"RS256\"]");
        json.Should().Contain("claims_supported");
    }

    [TestMethod]
    public async Task GetMetadataAsync_AdvertisesClientCredentialsOnlyForConfiguredConfidentialClient()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);
        var before = await service.GetMetadataAsync(new DefaultHttpContext());
        before.GrantTypesSupported.Should().NotContain(SqlOS.AuthServer.Contracts.SqlOSOAuthGrantTypes.ClientCredentials);

        context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().Add(new()
        {
            Id = "machine-app",
            ClientId = "machine",
            Name = "Machine",
            ClientType = "confidential",
            TokenEndpointAuthMethod = "client_secret_basic",
            GrantTypesJson = "[\"client_credentials\"]",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var after = await service.GetMetadataAsync(new DefaultHttpContext());
        after.GrantTypesSupported.Should().Contain(SqlOS.AuthServer.Contracts.SqlOSOAuthGrantTypes.ClientCredentials);
        after.TokenEndpointAuthMethodsSupported.Should().Contain("client_secret_basic");
    }

    [TestMethod]
    public async Task GetMetadataAsync_AlwaysAdvertisesRequestObjectParametersAsUnsupported()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        var json = JsonSerializer.Serialize(metadata);

        // OIDC Discovery defaults request_uri_parameter_supported to TRUE when the
        // field is absent, so both flags must be emitted explicitly as false.
        json.Should().Contain("\"request_parameter_supported\":false");
        json.Should().Contain("\"request_uri_parameter_supported\":false");
    }

    [TestMethod]
    public async Task GetMetadataAsync_AdvertisesClientSecretPostOnlyWhenSuchAClientExists()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);
        var before = await service.GetMetadataAsync(new DefaultHttpContext());
        before.TokenEndpointAuthMethodsSupported.Should().NotContain("client_secret_post");

        context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().Add(new()
        {
            Id = "post-app",
            ClientId = "post-client",
            Name = "Post Client",
            ClientType = "confidential",
            TokenEndpointAuthMethod = "client_secret_post",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var after = await service.GetMetadataAsync(new DefaultHttpContext());
        after.TokenEndpointAuthMethodsSupported.Should().Equal("none", "client_secret_post");
    }

    [TestMethod]
    public async Task GetMetadataAsync_ZeroClients_AdvertisesStableOpenIdProviderDocument()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().Should().BeEmpty();

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        metadata.ScopesSupported.Should().Equal("email", "openid", "profile");
        metadata.ScopesSupported.Should().NotContain(scope => scope.StartsWith("auth:", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(metadata);
        json.Should().Be(
            """{"issuer":"https://app.example.com/sqlos/auth","authorization_endpoint":"https://app.example.com/sqlos/auth/authorize","token_endpoint":"https://app.example.com/sqlos/auth/token","device_authorization_endpoint":"https://app.example.com/sqlos/auth/device_authorization","jwks_uri":"https://app.example.com/sqlos/auth/.well-known/jwks.json","response_types_supported":["code"],"response_modes_supported":["query"],"request_parameter_supported":false,"request_uri_parameter_supported":false,"grant_types_supported":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:device_code"],"code_challenge_methods_supported":["S256"],"scopes_supported":["email","openid","profile"],"token_endpoint_auth_methods_supported":["none"],"resource_parameter_supported":true,"userinfo_endpoint":"https://app.example.com/sqlos/auth/userinfo","subject_types_supported":["public"],"id_token_signing_alg_values_supported":["RS256"],"claims_supported":["sub","name","preferred_username","email","email_verified","auth_time","amr","org_id"]}""");
    }

    [TestMethod]
    public async Task GetMetadataAsync_ZeroClients_OpenIdProviderDisabled_IsByteIdenticalToOAuthOnlyDocument()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.ConfigureOpenIdProvider(provider => provider.Enabled = false);
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().Should().BeEmpty();

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        metadata.ScopesSupported.Should().BeEmpty();
        metadata.ScopesSupported.Should().NotContain(scope => scope.StartsWith("auth:", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(metadata);
        json.Should().Be(
            """{"issuer":"https://app.example.com/sqlos/auth","authorization_endpoint":"https://app.example.com/sqlos/auth/authorize","token_endpoint":"https://app.example.com/sqlos/auth/token","device_authorization_endpoint":"https://app.example.com/sqlos/auth/device_authorization","jwks_uri":"https://app.example.com/sqlos/auth/.well-known/jwks.json","response_types_supported":["code"],"response_modes_supported":["query"],"request_parameter_supported":false,"request_uri_parameter_supported":false,"grant_types_supported":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:device_code"],"code_challenge_methods_supported":["S256"],"scopes_supported":[],"token_endpoint_auth_methods_supported":["none"],"resource_parameter_supported":true}""");
    }

    [TestMethod]
    public async Task GetMetadataAsync_DefaultSingleApplication_AdvertisesOidcByDefault_AndKeepsSeededAllowlist()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.UseSingleApplication("Acme", app => app.Origin = "https://app.example.com");
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var client = await context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().SingleAsync();
        JsonSerializer.Deserialize<List<string>>(client.AllowedScopesJson)
            .Should().BeEquivalentTo("openid", "profile", "email", "offline_access");

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        metadata.ScopesSupported.Should().Equal("email", "openid", "profile");
        metadata.ScopesSupported.Should().NotContain("offline_access", "refresh issuance is not gated on offline_access, so advertising it would be dishonest");
        metadata.ScopesSupported.Should().NotContain(scope => scope.StartsWith("auth:", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(metadata);
        json.Should().Contain("\"openid\"");
        json.Should().Contain("\"id_token_signing_alg_values_supported\":[\"RS256\"]");
        json.Should().NotContain("offline_access");
        json.Should().NotContain("\"auth:");
    }

    [TestMethod]
    public async Task GetMetadataAsync_DefaultSingleApplication_OpenIdProviderDisabled_MakesNoOidcClaim()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.UseSingleApplication("Acme", app => app.Origin = "https://app.example.com");
        optionsValue.ConfigureOpenIdProvider(provider => provider.Enabled = false);
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var client = await context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().SingleAsync();
        JsonSerializer.Deserialize<List<string>>(client.AllowedScopesJson)
            .Should().BeEquivalentTo("openid", "profile", "email", "offline_access");

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        metadata.ScopesSupported.Should().BeEmpty();
        metadata.ScopesSupported.Should().NotIntersectWith(["openid", "profile", "email", "offline_access"]);
        metadata.ScopesSupported.Should().NotContain(scope => scope.StartsWith("auth:", StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(metadata);
        json.Should().NotContain("openid");
        json.Should().NotContain("id_token");
        json.Should().NotContain("\"auth:");
    }

    [TestMethod]
    public async Task GetMetadataAsync_AdvertisesOperatorApiScopes_AndExcludesReservedOidcAndAuthPseudoScopes()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        optionsValue.UseSingleApplication("Acme", app =>
        {
            app.Origin = "https://app.example.com";
            app.AllowedScopes = ["openid", "profile", "email", "offline_access", "auth:password", "x.read", "y.write"];
        });
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);

        var metadata = await service.GetMetadataAsync(new DefaultHttpContext());
        metadata.ScopesSupported.Should().Equal(
            "email",
            "openid",
            "profile",
            "x.read",
            "y.write");
        metadata.ScopesSupported.Should().NotContain("offline_access");
        metadata.ScopesSupported.Should().NotContain(scope => scope.StartsWith("auth:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetMetadataAsync_AddingClientWithApiScope_SurfacesThatScope()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.com",
            Issuer = "https://app.example.com/sqlos/auth"
        };
        var service = await CreateAuthorizationServerServiceAsync(context, optionsValue);
        var before = await service.GetMetadataAsync(new DefaultHttpContext());
        before.ScopesSupported.Should().Equal("email", "openid", "profile");

        context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>().Add(new()
        {
            Id = "api-app",
            ClientId = "api",
            Name = "API",
            ClientType = "public_pkce",
            TokenEndpointAuthMethod = "none",
            AllowedScopesJson = "[\"x.read\"]",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var after = await service.GetMetadataAsync(new DefaultHttpContext());
        after.ScopesSupported.Should().Equal("email", "openid", "profile", "x.read");
    }

    private static Task<SqlOSAuthorizationServerService> CreateAuthorizationServerServiceAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue)
        => CreateAuthorizationServerServiceAsync(context, optionsValue, new TestAuthEmailSender());

    private static async Task<SqlOSAuthorizationServerService> CreateAuthorizationServerServiceAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue,
        TestAuthEmailSender emailSender)
    {
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);

        await settings.EnsureDefaultAuthPageSettingsAsync();
        if (optionsValue.AuthPageSeed != null)
        {
            await settings.UpsertSeededAuthPageSettingsAsync();
        }
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
