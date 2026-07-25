using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSClientCredentialsServiceTests
{
    [TestMethod]
    public async Task ValidServiceAccount_IssuesAudienceBoundAccessTokenWithoutHumanSession()
    {
        await using var harness = await CreateHarnessAsync();

        var result = await harness.Service.ExchangeAsync(
            "ledger-worker", harness.Secret, "https://api.example.test/ledger", "ledger.read",
            new DefaultHttpContext(), default);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        jwt.Subject.Should().Be("service_account::ledger-worker");
        jwt.Audiences.Should().ContainSingle("https://api.example.test/ledger");
        jwt.Claims.Should().ContainSingle(x => x.Type == "client_id" && x.Value == "ledger-worker");
        jwt.Claims.Should().ContainSingle(x => x.Type == "token_kind" && x.Value == "service");
        jwt.Claims.Should().NotContain(x => x.Type == "sid");
        jwt.Claims.Should().NotContain(x => x.Type == JwtRegisteredClaimNames.Email);
        result.Scopes.Should().Equal("ledger.read");

        var validated = await harness.Crypto.ValidateAccessTokenAsync(
            result.AccessToken, "https://api.example.test/ledger");
        validated.Should().NotBeNull();
        validated!.SessionId.Should().BeEmpty();
        validated.UserId.Should().BeNull();
        validated.Principal.FindFirst("sub")!.Value.Should().Be("service_account::ledger-worker");
    }

    [TestMethod]
    public async Task WrongAudienceScopeOrSecret_IsRejectedWithoutTokenIssuance()
    {
        await using var harness = await CreateHarnessAsync();

        await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
            "ledger-worker", "wrong-secret", "https://api.example.test/ledger", "ledger.read",
            new DefaultHttpContext(), default));
        await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
            "ledger-worker", harness.Secret, "https://api.example.test/other", "ledger.read",
            new DefaultHttpContext(), default));
        await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
            "ledger-worker", harness.Secret, "https://api.example.test/ledger", "ledger.write",
            new DefaultHttpContext(), default));
    }

    [TestMethod]
    public async Task PublicDisabledExpiredAndGrantlessClients_AreRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var client = await harness.Context.Set<SqlOSClientApplication>().SingleAsync();
        var account = await harness.Context.Set<SqlOSFgaServiceAccount>().SingleAsync();

        foreach (var mutation in new Action[]
        {
            () => client.ClientType = "public_pkce",
            () => { client.ClientType = "confidential"; client.IsActive = false; },
            () => { client.IsActive = true; client.GrantTypesJson = "[]"; },
            () => { client.GrantTypesJson = "[\"client_credentials\"]"; account.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); }
        })
        {
            mutation();
            await harness.Context.SaveChangesAsync();
            await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
                "ledger-worker", harness.Secret, "https://api.example.test/ledger", "ledger.read",
                new DefaultHttpContext(), default));
        }
    }

    [TestMethod]
    public async Task UnknownClient_UsesProcessLocalDummyHashAndPersistsNoCredential()
    {
        await using var harness = await CreateHarnessAsync();

        harness.Crypto.VerifyPassword(
            SqlOSClientAuthenticationService.DummyCredentialHash,
            "unknown-client-secret-with-sufficient-length-123456789").Should().BeFalse();
        await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
            "unknown-client",
            "unknown-client-secret-with-sufficient-length-123456789",
            "https://api.example.test/ledger",
            "ledger.read",
            new DefaultHttpContext(),
            default));
        (await harness.Context.Set<SqlOSFgaServiceAccount>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task RotatingStoredHash_InvalidatesPreviouslyIssuedTokenAndOldSecret()
    {
        await using var harness = await CreateHarnessAsync();
        var first = await harness.Service.ExchangeAsync(
            "ledger-worker", harness.Secret, "https://api.example.test/ledger", "ledger.read",
            new DefaultHttpContext(), default);
        var credential = await harness.Context.Set<SqlOSClientCredential>().SingleAsync();
        credential.SecretHash = harness.Crypto.HashPassword("replacement-secret-with-enough-entropy-123456789");
        await harness.Context.SaveChangesAsync();

        await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
            "ledger-worker", harness.Secret, "https://api.example.test/ledger", "ledger.read",
            new DefaultHttpContext(), default));
        (await harness.Crypto.ValidateAccessTokenAsync(first.AccessToken, "https://api.example.test/ledger"))
            .Should().NotBeNull("secret rotation is an explicit cutover for issuance, not token revocation");
    }

    [TestMethod]
    public async Task RotationAndRevocation_AreAuditedAndRevokeAtTheDocumentedBoundaries()
    {
        await using var harness = await CreateHarnessAsync();
        const string replacement = "replacement-secret-with-at-least-256-bits-123456789";

        await harness.Service.RotateSecretAsync("ledger-worker", replacement, "admin-1");
        await Assert.ThrowsExceptionAsync<SqlOSClientCredentialsException>(() => harness.Service.ExchangeAsync(
            "ledger-worker", harness.Secret, "https://api.example.test/ledger", "ledger.read",
            new DefaultHttpContext(), default));
        var issued = await harness.Service.ExchangeAsync(
            "ledger-worker", replacement, "https://api.example.test/ledger", "ledger.read",
            new DefaultHttpContext(), default);

        await harness.Service.RevokeAsync("ledger-worker", "admin-1");
        (await harness.Crypto.ValidateAccessTokenAsync(issued.AccessToken, "https://api.example.test/ledger"))
            .Should().BeNull();
        var audits = await harness.Context.Set<SqlOSAuditEvent>().Select(x => x.EventType).ToListAsync();
        audits.Should().Contain("oauth.client_credentials.rotated");
        audits.Should().Contain("oauth.client_credentials.revoked");
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var dbOptions = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var context = new TestSqlOSInMemoryDbContext(dbOptions);
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            Issuer = "https://issuer.example.test/sqlos/auth",
            PublicOrigin = "https://issuer.example.test"
        });
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        const string secret = "test-secret-with-at-least-256-bits-of-randomness-123456789";
        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "app-worker",
            ClientId = "ledger-worker",
            Name = "Ledger Worker",
            ClientType = "confidential",
            TokenEndpointAuthMethod = "client_secret_basic",
            GrantTypesJson = "[\"client_credentials\"]",
            AllowedScopesJson = "[\"ledger.read\"]",
            Audience = "https://api.example.test/ledger",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
        {
            Id = "clcred-worker",
            ClientApplicationId = "app-worker",
            SecretHash = crypto.HashPassword(secret),
            CreatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
        {
            Id = "service_account::ledger-worker",
            SubjectTypeId = "service_account",
            DisplayName = "Ledger Worker"
        });
        context.Set<SqlOSFgaServiceAccount>().Add(new SqlOSFgaServiceAccount
        {
            Id = "sa-worker",
            SubjectId = "service_account::ledger-worker",
            ClientId = "ledger-worker",
            ClientSecretHash = crypto.HashPassword(secret)
        });
        await context.SaveChangesAsync();
        return new Harness(context, crypto, new SqlOSClientCredentialsService(context, crypto, admin, options), secret);
    }

    private sealed record Harness(
        TestSqlOSInMemoryDbContext Context,
        SqlOSCryptoService Crypto,
        SqlOSClientCredentialsService Service,
        string Secret) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
