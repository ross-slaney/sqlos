using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCryptoServiceTests
{
    [TestMethod]
    public void HashPassword_VerifyPassword_Succeeds()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));

        var hash = service.HashPassword("P@ssword123!");

        service.VerifyPassword(hash, "P@ssword123!").Should().BeTrue();
        service.VerifyPassword(hash, "bad-password").Should().BeFalse();
    }

    [TestMethod]
    public void Pkce_Rfc7636BoundaryVerifiers_ProduceValidS256Challenges()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));
        var minimumVerifier = new string('A', 43);
        var maximumVerifier = new string('~', 128);

        var minimumChallenge = service.CreatePkceCodeChallenge(minimumVerifier);
        var maximumChallenge = service.CreatePkceCodeChallenge(maximumVerifier);

        service.IsValidPkceCodeVerifier(minimumVerifier).Should().BeTrue();
        service.IsValidPkceCodeVerifier(maximumVerifier).Should().BeTrue();
        service.IsValidS256PkceCodeChallenge(minimumChallenge).Should().BeTrue();
        service.IsValidS256PkceCodeChallenge(maximumChallenge).Should().BeTrue();
        minimumChallenge.Should().HaveLength(43);
        maximumChallenge.Should().HaveLength(43);
        service.VerifyPkceCodeVerifier(minimumVerifier, minimumChallenge, "S256").Should().BeTrue();
        service.VerifyPkceCodeVerifier(maximumVerifier, maximumChallenge, "S256").Should().BeTrue();
    }

    [TestMethod]
    public void Pkce_InvalidVerifierShapes_AreRejectedBeforeHashing()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));
        var invalidVerifiers = new[]
        {
            new string('A', 42),
            new string('A', 129),
            new string('A', 42) + "!",
            new string('A', 42) + "é"
        };

        foreach (var verifier in invalidVerifiers)
        {
            service.IsValidPkceCodeVerifier(verifier).Should().BeFalse();
            var act = () => service.CreatePkceCodeChallenge(verifier);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*43 to 128 RFC 7636 unreserved characters*");
        }
    }

    [TestMethod]
    public void Pkce_InvalidS256ChallengeOrVerifier_FailsVerification()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));
        var validVerifier = new string('A', 43);
        var validChallenge = service.CreatePkceCodeChallenge(validVerifier);

        service.IsValidS256PkceCodeChallenge(new string('A', 42)).Should().BeFalse();
        service.IsValidS256PkceCodeChallenge(new string('A', 44)).Should().BeFalse();
        service.IsValidS256PkceCodeChallenge(new string('A', 42) + "~").Should().BeFalse();
        service.VerifyPkceCodeVerifier(new string('A', 42), validChallenge, "S256").Should().BeFalse();
        service.VerifyPkceCodeVerifier(validVerifier, new string('A', 42), "S256").Should().BeFalse();
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_CreatesOneKey()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));

        var first = await service.EnsureActiveSigningKeyAsync();
        var second = await service.EnsureActiveSigningKeyAsync();

        second.Id.Should().Be(first.Id);
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_WithDefaultOptions_DoesNotProtectSigningKeyEvenWhenDataProtectionExists()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            new EphemeralDataProtectionProvider());

        var key = await service.EnsureActiveSigningKeyAsync();

        key.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");
        key.PrivateKeyPem.Should().NotStartWith("dp:");
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_WithSigningKeyDataProtection_StoresProtectedPrivateKey()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(
            context,
            SigningKeyProtectedOptions(),
            new EphemeralDataProtectionProvider());

        var key = await service.EnsureActiveSigningKeyAsync();

        key.PrivateKeyPem.Should().StartWith("dp:");
        key.PrivateKeyPem.Should().NotContain("BEGIN PRIVATE KEY");
        service.UnprotectSecret(key.PrivateKeyPem).Should().Contain("BEGIN PRIVATE KEY");
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_WithSigningKeyDataProtection_ProtectsLegacyPlaintextPrivateKey()
    {
        using var context = CreateContext();
        var plaintextService = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));
        var legacyKey = await plaintextService.EnsureActiveSigningKeyAsync();
        legacyKey.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");

        var protectedService = new SqlOSCryptoService(
            context,
            SigningKeyProtectedOptions(),
            new EphemeralDataProtectionProvider());

        var upgradedKey = await protectedService.EnsureActiveSigningKeyAsync();

        upgradedKey.Id.Should().Be(legacyKey.Id);
        upgradedKey.PrivateKeyPem.Should().StartWith("dp:");
        protectedService.UnprotectSecret(upgradedKey.PrivateKeyPem).Should().Contain("BEGIN PRIVATE KEY");
    }

    [TestMethod]
    public async Task CreateAccessToken_WithProtectedSigningKey_Succeeds()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(
            context,
            SigningKeyProtectedOptions(),
            new EphemeralDataProtectionProvider());
        await service.EnsureActiveSigningKeyAsync();

        var user = new SqlOSUser
        {
            Id = "usr_test",
            DisplayName = "Test User",
            DefaultEmail = "test@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var session = new SqlOSSession
        {
            Id = "ses_test",
            UserId = user.Id,
            AuthenticationMethod = "password",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        var client = new SqlOSClientApplication
        {
            Id = "cli_test",
            ClientId = "test-client",
            Name = "Test Client",
            Audience = "test-api",
            CreatedAt = DateTime.UtcNow
        };

        var token = await service.CreateAccessTokenAsync(user, session, client, "org_test");

        token.Should().NotBeNullOrWhiteSpace();
        context.Set<SqlOSSigningKey>().Single().PrivateKeyPem.Should().StartWith("dp:");
    }

    [TestMethod]
    public async Task CreateAccessToken_WithUnreadableProtectedSigningKey_RotatesAndSucceeds()
    {
        using var context = CreateContext();
        var originalProvider = new EphemeralDataProtectionProvider();
        var originalService = new SqlOSCryptoService(context, SigningKeyProtectedOptions(), originalProvider);
        var originalKey = await originalService.EnsureActiveSigningKeyAsync();
        originalKey.PrivateKeyPem.Should().StartWith("dp:");

        var replacementProvider = new EphemeralDataProtectionProvider();
        var recoveryService = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            replacementProvider);

        var token = await recoveryService.CreateAccessTokenAsync(
            CreateUser(),
            CreateSession(),
            CreateClient(),
            "org_test");

        token.Should().NotBeNullOrWhiteSpace();
        originalKey.IsActive.Should().BeFalse();
        originalKey.RetiredAt.Should().NotBeNull();
        var activeKey = context.Set<SqlOSSigningKey>().Single(x => x.IsActive);
        activeKey.Id.Should().NotBe(originalKey.Id);
        activeKey.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");
    }

    [TestMethod]
    public void ProtectSecret_UnprotectSecret_RoundTrips()
    {
        using var context = CreateContext();
        var provider = new EphemeralDataProtectionProvider();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()), provider);

        var protectedSecret = service.ProtectSecret("super-secret-value");

        protectedSecret.Should().NotBe("super-secret-value");
        service.UnprotectSecret(protectedSecret).Should().Be("super-secret-value");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static IOptions<SqlOSAuthServerOptions> SigningKeyProtectedOptions()
        => Options.Create(new SqlOSAuthServerOptions { ProtectSigningKeysWithDataProtection = true });

    private static SqlOSUser CreateUser()
        => new()
        {
            Id = "usr_test",
            DisplayName = "Test User",
            DefaultEmail = "test@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static SqlOSSession CreateSession()
        => new()
        {
            Id = "ses_test",
            UserId = "usr_test",
            AuthenticationMethod = "password",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        };

    private static SqlOSClientApplication CreateClient()
        => new()
        {
            Id = "cli_test",
            ClientId = "test-client",
            Name = "Test Client",
            Audience = "test-api",
            CreatedAt = DateTime.UtcNow
        };
}
