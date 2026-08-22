using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// End-to-end OpenID Provider coverage over real SQL: discovery, ID token issuance
/// with JWKS signature validation, UserInfo challenges, optional state, and the
/// provider-disabled OAuth-only posture.
/// </summary>
[TestClass]
public sealed class OpenIdProviderIntegrationTests
{
    private const string Issuer = "https://auth.example.test/sqlos/auth";

    [TestMethod]
    public async Task Discovery_ServesOpenIdConfiguration_ByteIdenticalToOAuthMetadata()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");

        using var response = await fixture.Client.GetAsync("/sqlos/auth/.well-known/openid-configuration");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("issuer").GetString().Should().Be(Issuer);
        root.GetProperty("userinfo_endpoint").GetString().Should().Be($"{Issuer}/userinfo");
        root.GetProperty("jwks_uri").GetString().Should().Be($"{Issuer}/.well-known/jwks.json");
        root.GetProperty("subject_types_supported").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("public");
        // SqlOS only answers authorization requests in the redirect query string.
        root.GetProperty("response_modes_supported").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("query");
        root.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("RS256");
        root.GetProperty("scopes_supported").EnumerateArray().Select(x => x.GetString())
            .Should().Contain("openid");

        var oauthMetadata = await fixture.Client.GetStringAsync("/sqlos/auth/.well-known/oauth-authorization-server");
        body.Should().Be(oauthMetadata, "both well-known documents are the same authorization-server metadata");
    }

    [TestMethod]
    public async Task FullOidcFlow_MintsIdTokenVerifiableAgainstPublishedJwks()
    {
        var nonce = $"nonce-{Guid.NewGuid():N}";
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid", "profile", "email");

        var started = await fixture.StartAuthorizeAsync("openid profile email", nonce: nonce);
        var code = await fixture.SubmitPasswordLoginAsync(started);
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);

        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        var idToken = tokens.RootElement.GetProperty("id_token").GetString();
        idToken.Should().NotBeNullOrWhiteSpace();

        var jwksJson = await fixture.Client.GetStringAsync("/sqlos/auth/.well-known/jwks.json");
        var jwks = new JsonWebKeySet(jwksJson);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        handler.ValidateToken(
            idToken,
            new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = HostedAuthorizeTokenFixture.ClientId,
                IssuerSigningKeys = jwks.GetSigningKeys(),
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true
            },
            out var validated);

        var jwt = (JwtSecurityToken)validated;
        jwt.Header.Typ.Should().Be("JWT");
        jwt.Header.Kid.Should().NotBeNullOrWhiteSpace();
        jwt.Payload["nonce"].Should().Be(nonce);
        jwt.Payload["at_hash"].Should().Be(ComputeAtHash(accessToken));
        jwt.Payload.ContainsKey("auth_time").Should().BeTrue();
        jwt.Payload.ContainsKey("amr").Should().BeTrue();
        // Profile/email claims live in UserInfo, not the ID token (OIDC Core §5.4).
        jwt.Payload.ContainsKey("email").Should().BeFalse();
        jwt.Subject.Should().Be(fixture.UserId);
    }

    [TestMethod]
    public async Task ScopeWithoutOpenId_OmitsIdTokenFromTokenResponse()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("profile");

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("profile");

        tokens.RootElement.GetProperty("scope").GetString().Should().Be("profile");
        tokens.RootElement.TryGetProperty("id_token", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task RefreshAfterOpenIdGrant_MintsIdTokenWithoutNonce()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid", "profile");

        var started = await fixture.StartAuthorizeAsync("openid profile", nonce: $"nonce-{Guid.NewGuid():N}");
        var code = await fixture.SubmitPasswordLoginAsync(started);
        using var issued = await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
        issued.RootElement.GetProperty("id_token").GetString().Should().NotBeNullOrWhiteSpace();

        using var refreshed = await fixture.RefreshAsync(issued.RootElement.GetProperty("refresh_token").GetString()!);

        var refreshedIdToken = refreshed.RootElement.GetProperty("id_token").GetString();
        refreshedIdToken.Should().NotBeNullOrWhiteSpace();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshedIdToken);
        jwt.Payload.ContainsKey("nonce").Should().BeFalse("OIDC Core binds nonce to the authentication event, not to refreshes");
        jwt.Subject.Should().Be(fixture.UserId);
    }

    [TestMethod]
    public async Task UserInfo_WithBearerAndFormTokens_ReleasesMatchingClaims()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid", "profile", "email");
        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile email");
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        var accessJwt = new JwtSecurityTokenHandler { MapInboundClaims = false }.ReadJwtToken(accessToken);

        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/sqlos/auth/userinfo");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var bearerResponse = await fixture.Client.SendAsync(bearerRequest);
        bearerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertUserInfoResponseIsUncacheable(bearerResponse);
        using var bearerClaims = JsonDocument.Parse(await bearerResponse.Content.ReadAsStringAsync());
        bearerClaims.RootElement.GetProperty("sub").GetString().Should().Be(accessJwt.Subject);
        bearerClaims.RootElement.GetProperty("email").GetString().Should().Be(fixture.Email);

        using var formResponse = await fixture.Client.PostAsync(
            "/sqlos/auth/userinfo",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = accessToken }));
        formResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertUserInfoResponseIsUncacheable(formResponse);
        using var formClaims = JsonDocument.Parse(await formResponse.Content.ReadAsStringAsync());
        formClaims.RootElement.GetProperty("sub").GetString().Should().Be(accessJwt.Subject);
    }

    [TestMethod]
    public async Task UserInfo_BearerHeaderAndFormTokenTogether_IsRejectedAsInvalidRequest()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid");
        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid");
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/userinfo")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = accessToken })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "RFC 6750 §2 forbids using more than one token transport in a request, even when both carry the same valid token");
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_request");
        AssertUserInfoResponseIsUncacheable(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("invalid_request");
    }

    /// <summary>
    /// OIDC Core §5.3.2: UserInfo responses carry identity PII, so success and
    /// challenge responses alike must forbid caching.
    /// </summary>
    private static void AssertUserInfoResponseIsUncacheable(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Be("no-cache");
    }

    [TestMethod]
    public async Task UserInfo_LowercaseBearerScheme_IsAccepted()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid");
        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid");
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
        var accessJwt = new JwtSecurityTokenHandler { MapInboundClaims = false }.ReadJwtToken(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/sqlos/auth/userinfo");
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {accessToken}");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "RFC 9110 §11.1: authentication scheme names are case-insensitive");
        using var claims = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        claims.RootElement.GetProperty("sub").GetString().Should().Be(accessJwt.Subject);
    }

    [TestMethod]
    public async Task UserInfo_MissingOrInvalidToken_ReturnsBearerChallenges()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");

        using var missing = await fixture.Client.GetAsync("/sqlos/auth/userinfo");
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");

        using var garbageRequest = new HttpRequestMessage(HttpMethod.Get, "/sqlos/auth/userinfo");
        garbageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        using var garbage = await fixture.Client.SendAsync(garbageRequest);
        garbage.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        garbage.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        using var garbageBody = JsonDocument.Parse(await garbage.Content.ReadAsStringAsync());
        garbageBody.RootElement.GetProperty("error").GetString().Should().Be("invalid_token");
    }

    [TestMethod]
    public async Task UserInfo_SessionWithoutOpenIdScope_Returns403InsufficientScope()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("profile");
        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("profile");
        var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/sqlos/auth/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("insufficient_scope");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("insufficient_scope");
    }

    [TestMethod]
    public async Task ReplayedAuthorizationCode_CannotDoubleIssueIdToken()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid");

        var started = await fixture.StartAuthorizeAsync("openid");
        var code = await fixture.SubmitPasswordLoginAsync(started);
        using var first = await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
        first.RootElement.GetProperty("id_token").GetString().Should().NotBeNullOrWhiteSpace();

        var replay = async () => await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
        await replay.Should().ThrowAsync<InvalidOperationException>(
            "a consumed authorization code must never issue a second token set or ID token");
    }

    [TestMethod]
    public async Task AuthorizeWithoutState_CompletesFlow_AndOmitsStateFromCodeRedirect()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("OpenIdProvider");
        await fixture.SetClientAllowedScopesAsync("openid", "profile");

        var started = await fixture.StartAuthorizeAsync("openid profile", omitState: true);
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        var redirect = new Uri(login.Location, UriKind.Absolute);
        var query = QueryHelpers.ParseQuery(redirect.Query);
        query.ContainsKey("state").Should().BeFalse("an absent state must not be echoed as an empty parameter");
        query.ContainsKey("code").Should().BeTrue();

        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(login.Code, started.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        tokens.RootElement.GetProperty("id_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task OpenIdProviderDisabled_RemovesOidcSurfaceEntirely()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
            "OpenIdProviderOff",
            options => options.AuthServer.ConfigureOpenIdProvider(provider => provider.Enabled = false));
        await fixture.SetClientAllowedScopesAsync("openid", "profile");

        using var discovery = await fixture.Client.GetAsync("/sqlos/auth/.well-known/openid-configuration");
        discovery.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var userInfo = await fixture.Client.GetAsync("/sqlos/auth/userinfo");
        userInfo.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var tokens = await fixture.AuthorizeLoginAndExchangeAsync("openid profile");
        tokens.RootElement.TryGetProperty("id_token", out _)
            .Should().BeFalse("disabled provider mode behaves exactly like a plain OAuth 2.0 authorization server");

        using var metadata = JsonDocument.Parse(
            await fixture.Client.GetStringAsync("/sqlos/auth/.well-known/oauth-authorization-server"));
        metadata.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(x => x.GetString())
            .Should().NotContain("openid");
    }

    /// <summary>
    /// OIDC Core §3.1.3.6: base64url of the left half (16 bytes) of the SHA-256
    /// hash of the ASCII access token.
    /// </summary>
    private static string ComputeAtHash(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash[..16]);
    }
}
