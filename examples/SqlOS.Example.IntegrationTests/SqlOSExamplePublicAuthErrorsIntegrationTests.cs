using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Models;
using SqlOS.Example.Api.Data;
using SqlOS.Example.IntegrationTests.Infrastructure;

namespace SqlOS.Example.IntegrationTests;

[TestClass]
public sealed class SqlOSExamplePublicAuthErrorsIntegrationTests
{
    [TestMethod]
    public async Task TokenEndpoint_InvalidGrant_ReturnsStableOAuthErrorWithoutInternalMessage()
    {
        var marker = $"server=db01;secret={Guid.NewGuid():N}";

        var response = await ExampleApiFixture.Client.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = marker,
            ["client_id"] = "example-web",
            ["redirect_uri"] = "http://localhost:3000/auth/callback",
            ["code_verifier"] = "invalid-verifier"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("server=db01");
        body.Should().NotContain("secret=");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
        document.RootElement.GetProperty("error_description").GetString()
            .Should().Be(SqlOSPublicAuthErrorMapper.DefaultGrantMessage);
    }

    [TestMethod]
    public async Task Authorize_InvalidRedirectUri_RendersSafePublicErrorAndAuditsDetails()
    {
        await using var factory = ExampleApiFixture.CreateFactory(builder =>
        {
            builder.UseSetting("SqlOS:EnableHeadlessAuthPage", "false");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var marker = $"server=db01-{Guid.NewGuid():N};secret=redirect";
        var redirectUri = $"http://evil.example/callback?{marker}";
        var response = await client.GetAsync(QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "example-web",
            ["redirect_uri"] = redirectUri,
            ["state"] = "state-123",
            ["scope"] = "openid profile email",
            ["code_challenge"] = "challenge-123",
            ["code_challenge_method"] = "S256"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(SqlOSPublicAuthErrorMapper.DefaultAuthorizationRequestMessage);
        body.Should().NotContain("evil.example");
        body.Should().NotContain(marker);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ExampleAppDbContext>();
        var audit = await context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .OrderByDescending(x => x.OccurredAt)
            .FirstOrDefaultAsync(x =>
                x.EventType == "auth.public_error.mapped"
                && x.MetadataJson != null
                && x.MetadataJson.Contains(marker));

        audit.Should().NotBeNull();
        audit!.MetadataJson.Should().Contain(marker);
        audit.MetadataJson.Should().Contain("Redirect URI");
        audit.MetadataJson.Should().Contain(SqlOSPublicAuthErrorMapper.DefaultAuthorizationRequestMessage);
    }

    [TestMethod]
    public async Task HeadlessEndpoint_InternalException_ReturnsOpaqueErrorShape()
    {
        var marker = $"server=db01-{Guid.NewGuid():N};secret=headless";

        var response = await ExampleApiFixture.Client.PostAsJsonAsync("/sqlos/auth/headless/start", new
        {
            responseType = "code",
            clientId = "example-web",
            redirectUri = $"http://evil.example/callback?{marker}",
            state = "state-123",
            scope = "openid profile email",
            codeChallenge = "challenge-123",
            codeChallengeMethod = "S256"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("evil.example");
        body.Should().NotContain(marker);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("error").GetString().Should().Be("invalid_request");
        document.RootElement.GetProperty("message").GetString()
            .Should().Be(SqlOSPublicAuthErrorMapper.DefaultRequestMessage);
    }

    [TestMethod]
    public async Task OidcProviderFailure_DoesNotExposeRawProviderErrorToBrowser()
    {
        var marker = $"server=db01;secret={Guid.NewGuid():N}";

        var response = await ExampleApiFixture.Client.GetAsync(
            $"/api/v1/auth/oidc/callback/conn_public_error?error={Uri.EscapeDataString(marker)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.ToString().Should().NotContain("server=db01");
        location.ToString().Should().NotContain("secret=");

        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be(SqlOSPublicAuthErrorMapper.DefaultExternalProviderMessage);
    }
}
