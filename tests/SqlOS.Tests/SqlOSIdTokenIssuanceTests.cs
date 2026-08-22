using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSIdTokenIssuanceTests
{
    private const string AccessToken = "access-token-for-at-hash-binding";

    [TestMethod]
    public async Task CreateIdTokenAsync_FullGrant_MintsSpecHonestClaims()
    {
        using var context = CreateContext();
        var crypto = await CreateCryptoAsync(context);
        var user = await SeedUserAsync(context, emailVerified: true);
        var authenticatedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = CreateSession(user, authenticatedAt);
        var client = CreateClient();

        var idToken = await crypto.CreateIdTokenAsync(
            user,
            session,
            client,
            "org_idt",
            ["openid", "profile", "email"],
            AccessToken,
            "nonce-123");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
        jwt.Header.Alg.Should().Be("RS256");
        jwt.Header.Typ.Should().Be("JWT");
        jwt.Header.Kid.Should().NotBeNullOrWhiteSpace();
        jwt.Issuer.Should().Be("https://app.example.com/sqlos/auth");
        jwt.Audiences.Should().Equal(client.ClientId);
        jwt.Audiences.Should().NotContain(
            client.Audience,
            "the ID token audience is the relying party's client_id, never the resource-server audience");
        jwt.Subject.Should().Be(user.Id);
        jwt.Payload["nonce"].Should().Be("nonce-123");
        jwt.Payload["at_hash"].Should().Be(ComputeExpectedAtHash(AccessToken));
        Convert.ToInt64(jwt.Payload["auth_time"]).Should().Be(EpochTime.GetIntDate(authenticatedAt));
        jwt.Payload["sid"].Should().Be(session.Id);
        // OIDC Core §5.4: scope-gated profile/email claims are released from
        // UserInfo in the code flow, never embedded in the ID token.
        jwt.Payload.ContainsKey("name").Should().BeFalse();
        jwt.Payload.ContainsKey("preferred_username").Should().BeFalse();
        jwt.Payload.ContainsKey("email").Should().BeFalse();
        jwt.Payload.ContainsKey("email_verified").Should().BeFalse();
        jwt.Payload["org_id"].Should().Be("org_idt");
        jwt.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).Should().Contain("password");
    }

    [TestMethod]
    public async Task CreateIdTokenAsync_WithoutNonce_OmitsNonceAndFallsBackToCreatedAtForAuthTime()
    {
        using var context = CreateContext();
        var crypto = await CreateCryptoAsync(context);
        var user = await SeedUserAsync(context, emailVerified: false);
        var session = CreateSession(user, authenticatedAt: null);
        var client = CreateClient();

        var idToken = await crypto.CreateIdTokenAsync(
            user,
            session,
            client,
            organizationId: null,
            ["openid"],
            AccessToken,
            nonce: null);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
        jwt.Payload.ContainsKey("nonce").Should().BeFalse();
        Convert.ToInt64(jwt.Payload["auth_time"]).Should().Be(EpochTime.GetIntDate(session.CreatedAt));
    }

    [TestMethod]
    public async Task CreateIdTokenAsync_WhitespaceNonce_IsEchoedVerbatim()
    {
        using var context = CreateContext();
        var crypto = await CreateCryptoAsync(context);
        var user = await SeedUserAsync(context, emailVerified: true);
        var session = CreateSession(user, authenticatedAt: DateTime.UtcNow);
        var client = CreateClient();

        var idToken = await crypto.CreateIdTokenAsync(
            user,
            session,
            client,
            organizationId: null,
            ["openid"],
            AccessToken,
            nonce: " ");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
        jwt.Payload["nonce"].Should().Be(
            " ",
            "OIDC Core requires the request nonce back in the ID token unmodified, whitespace included");
    }

    [TestMethod]
    public void ToUtcEpochSeconds_UnspecifiedKind_IsTreatedAsUtc()
    {
        // Session and user timestamps are stored as UTC, but a SQL round-trip
        // returns DateTimeKind.Unspecified, which EpochTime.GetIntDate would
        // convert as local time and shift auth_time by the host's UTC offset.
        var reloadedFromSql = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var sameInstantAsUtc = DateTime.SpecifyKind(reloadedFromSql, DateTimeKind.Utc);

        SqlOSCryptoService.ToUtcEpochSeconds(reloadedFromSql)
            .Should().Be(EpochTime.GetIntDate(sameInstantAsUtc));
        SqlOSCryptoService.ToUtcEpochSeconds(sameInstantAsUtc)
            .Should().Be(SqlOSCryptoService.ToUtcEpochSeconds(reloadedFromSql),
                "an already-UTC value and its Unspecified-kind round-trip must produce the same epoch seconds");
    }

    [TestMethod]
    public async Task CreateIdTokenAsync_OpenIdOnlyGrant_OmitsProfileEmailAndOrganizationClaims()
    {
        using var context = CreateContext();
        var crypto = await CreateCryptoAsync(context);
        var user = await SeedUserAsync(context, emailVerified: true);
        var session = CreateSession(user, authenticatedAt: DateTime.UtcNow);
        var client = CreateClient();

        var idToken = await crypto.CreateIdTokenAsync(
            user,
            session,
            client,
            organizationId: null,
            ["openid"],
            AccessToken,
            nonce: null);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
        jwt.Subject.Should().Be(user.Id);
        jwt.Payload.ContainsKey("name").Should().BeFalse();
        jwt.Payload.ContainsKey("preferred_username").Should().BeFalse();
        jwt.Payload.ContainsKey("email").Should().BeFalse();
        jwt.Payload.ContainsKey("email_verified").Should().BeFalse();
        jwt.Payload.ContainsKey("org_id").Should().BeFalse();
        jwt.Payload["sid"].Should().Be(session.Id);
        jwt.Payload.ContainsKey("amr").Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateIdTokenAsync_ProfileGrantWithoutEmailScope_OmitsEmailClaims()
    {
        using var context = CreateContext();
        var crypto = await CreateCryptoAsync(context);
        var user = await SeedUserAsync(context, emailVerified: true);
        var session = CreateSession(user, authenticatedAt: DateTime.UtcNow);
        var client = CreateClient();

        var idToken = await crypto.CreateIdTokenAsync(
            user,
            session,
            client,
            organizationId: null,
            ["openid", "profile"],
            AccessToken,
            nonce: null);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(idToken);
        jwt.Payload.ContainsKey("name").Should().BeFalse();
        jwt.Payload.ContainsKey("preferred_username").Should().BeFalse();
        jwt.Payload.ContainsKey("email").Should().BeFalse();
        jwt.Payload.ContainsKey("email_verified").Should().BeFalse();
    }

    private static async Task<SqlOSCryptoService> CreateCryptoAsync(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        });
        var crypto = TestCryptoService.Create(context, options);
        await crypto.EnsureActiveSigningKeyAsync();
        return crypto;
    }

    private static async Task<SqlOSUser> SeedUserAsync(TestSqlOSInMemoryDbContext context, bool emailVerified)
    {
        var user = new SqlOSUser
        {
            Id = "usr_idt",
            DisplayName = "Alice Example",
            DefaultEmail = "alice@example.test",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Set<SqlOSUser>().Add(user);
        context.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
        {
            Id = "eml_idt",
            UserId = user.Id,
            Email = user.DefaultEmail!,
            NormalizedEmail = user.DefaultEmail!,
            IsPrimary = true,
            IsVerified = emailVerified,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return user;
    }

    private static SqlOSSession CreateSession(SqlOSUser user, DateTime? authenticatedAt)
        => new()
        {
            Id = "ses_idt",
            UserId = user.Id,
            ClientApplicationId = "cli_idt",
            AuthenticationMethod = "password",
            AuthenticatedAt = authenticatedAt,
            CreatedAt = new DateTime(2026, 7, 31, 8, 30, 0, DateTimeKind.Utc)
        };

    private static SqlOSClientApplication CreateClient()
        => new()
        {
            Id = "cli_idt",
            ClientId = "idt-client",
            Name = "Id Token Client",
            Audience = "https://api.example.test/resource",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// OIDC Core §3.1.3.6: base64url of the left half (16 bytes) of the SHA-256
    /// hash of the ASCII access token.
    /// </summary>
    private static string ComputeExpectedAtHash(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash[..16]);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
