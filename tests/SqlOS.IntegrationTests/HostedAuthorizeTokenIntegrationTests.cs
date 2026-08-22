using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class HostedAuthorizeTokenIntegrationTests
{
    private static readonly string[] ExpectedOAuthTokenResponseFields =
    [
        "access_token",
        "refresh_token",
        "token_type",
        "expires_in",
        "scope"
    ];

    private static readonly string[] ExpectedOpenIdTokenResponseFields =
    [
        "access_token",
        "refresh_token",
        "token_type",
        "expires_in",
        "scope",
        "id_token"
    ];

    [TestMethod]
    public async Task HostedAuthorizeToToken_CodeGrant_ReturnsGrantedScope()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile", "email");

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile email");

        tokens.RootElement.GetProperty("scope").GetString().Should().Be("openid profile email");
        tokens.RootElement.GetProperty("token_type").GetString().Should().Be("Bearer");
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        tokens.RootElement.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_PartialAllowedScopes_ReturnsIntersectedScope()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile");

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile email");

        tokens.RootElement.GetProperty("scope").GetString().Should().Be("openid profile");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_EmptyAllowedScopes_GrantsNoRequestedScopes()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync();

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile custom.read");

        tokens.RootElement.GetProperty("scope").GetString().Should().Be("");
        tokens.RootElement.TryGetProperty("id_token", out _)
            .Should().BeFalse("openid was requested but not granted, so no ID token is minted");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_RefreshGrant_EchoesOriginallyGrantedScope()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile", "email");
        using var issued = await fixture.AuthorizeLoginAndExchangeAsync("openid profile email");
        var refreshToken = issued.RootElement.GetProperty("refresh_token").GetString()!;

        using var refreshed = await fixture.RefreshAsync(refreshToken);

        refreshed.RootElement.GetProperty("scope").GetString().Should().Be("openid profile email");
        refreshed.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        refreshed.RootElement.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_RefreshGrant_EmptyGrant_EchoesEmptyScopeNotOmission()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync();
        using var issued = await fixture.AuthorizeLoginAndExchangeAsync("openid profile");
        var refreshToken = issued.RootElement.GetProperty("refresh_token").GetString()!;

        using var refreshed = await fixture.RefreshAsync(refreshToken);

        refreshed.RootElement.GetProperty("scope").GetString().Should().Be("");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_CodeGrant_OpenIdScope_ContainsExactlyExpectedTopLevelFields()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile");

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile");

        AssertExactTokenResponseFields(tokens, expectIdToken: true);
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_RefreshGrant_OpenIdSession_ContainsExactlyExpectedTopLevelFields()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile");
        using var issued = await fixture.AuthorizeLoginAndExchangeAsync("openid profile");

        using var refreshed = await fixture.RefreshAsync(issued.RootElement.GetProperty("refresh_token").GetString()!);

        AssertExactTokenResponseFields(refreshed, expectIdToken: true);
        refreshed.RootElement.GetProperty("scope").GetString().Should().Be("openid profile");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_OAuthOnlyGrant_ContainsExactlyExpectedTopLevelFieldsWithoutIdToken()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("custom.read");

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("custom.read");

        AssertExactTokenResponseFields(tokens, expectIdToken: false);
        tokens.RootElement.GetProperty("scope").GetString().Should().Be("custom.read");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_UserAccessToken_CarriesGrantedScopeClaim()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile", "email");
        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile email");

        // The granted scope rides in the access token (RFC 9068 shape) so resource
        // servers can enforce the client's delegation ceiling via RequiredScopes.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(
            tokens.RootElement.GetProperty("access_token").GetString());
        jwt.Payload["scope"].Should().Be("openid profile email");
        jwt.Payload["scope"].Should().Be(tokens.RootElement.GetProperty("scope").GetString(),
            "the claim must record exactly the granted scope the token response echoes");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_DuplicateScopeEntries_DeduplicatesGrantedScope()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync("openid", "profile", "email");

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid openid profile profile email");

        tokens.RootElement.GetProperty("scope").GetString().Should().Be("openid profile email");
    }

    [TestMethod]
    public async Task HostedAuthorizeToToken_ThousandCharacterScope_PersistsAtColumnLimit()
    {
        var thousandCharacterScope = new string('a', 1000);
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync();
        await fixture.SetClientAllowedScopesAsync(thousandCharacterScope);

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync(thousandCharacterScope);

        tokens.RootElement.GetProperty("scope").GetString().Should().Be(thousandCharacterScope);
        tokens.RootElement.GetProperty("scope").GetString()!.Length.Should().Be(1000);
    }

    private static void AssertExactTokenResponseFields(JsonDocument tokens, bool expectIdToken)
    {
        var names = tokens.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        if (expectIdToken)
        {
            names.Should().Equal(ExpectedOpenIdTokenResponseFields);
            tokens.RootElement.GetProperty("id_token").GetString().Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            names.Should().Equal(ExpectedOAuthTokenResponseFields);
            names.Should().NotContain("id_token");
            tokens.RootElement.TryGetProperty("id_token", out _).Should().BeFalse();
        }
    }
}
