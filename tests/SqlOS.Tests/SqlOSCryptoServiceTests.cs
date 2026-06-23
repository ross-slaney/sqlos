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
    public async Task ValidateAccessTokenAsync_DebouncesRepeatedLastSeenWrites()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            AccessTokenValidationLastSeenDebounceInterval = TimeSpan.FromMinutes(10)
        });
        var service = new SqlOSCryptoService(context, options);

        await SeedValidationSessionAsync(context, service, DateTime.UtcNow.AddMinutes(-30));
        var user = context.Set<SqlOSUser>().Single();
        var session = context.Set<SqlOSSession>().Single();
        var client = context.Set<SqlOSClientApplication>().Single();
        var token = await service.CreateAccessTokenAsync(user, session, client, "org_test");

        var baselineSaveCount = context.SaveChangesAsyncCallCount;
        var firstValidation = await service.ValidateAccessTokenAsync(token, client.Audience);

        firstValidation.Should().NotBeNull();
        context.SaveChangesAsyncCallCount.Should().Be(baselineSaveCount + 1);
        var updatedSessionLastSeenAt = session.LastSeenAt;
        var updatedClientLastSeenAt = client.LastSeenAt;

        for (var i = 0; i < 3; i++)
        {
            var repeatedValidation = await service.ValidateAccessTokenAsync(token, client.Audience);
            repeatedValidation.Should().NotBeNull();
        }

        context.SaveChangesAsyncCallCount.Should().Be(baselineSaveCount + 1);
        session.LastSeenAt.Should().Be(updatedSessionLastSeenAt);
        client.LastSeenAt.Should().Be(updatedClientLastSeenAt);
    }

    [TestMethod]
    public async Task ValidateAccessTokenAsync_RejectsRevokedSessionWithinLastSeenDebounceWindow()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            AccessTokenValidationLastSeenDebounceInterval = TimeSpan.FromMinutes(10)
        });
        var service = new SqlOSCryptoService(context, options);

        await SeedValidationSessionAsync(context, service, DateTime.UtcNow);
        var user = context.Set<SqlOSUser>().Single();
        var session = context.Set<SqlOSSession>().Single();
        var client = context.Set<SqlOSClientApplication>().Single();
        var token = await service.CreateAccessTokenAsync(user, session, client, "org_test");

        session.RevokedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        var baselineSaveCount = context.SaveChangesAsyncCallCount;

        var validated = await service.ValidateAccessTokenAsync(token, client.Audience);

        validated.Should().BeNull();
        context.SaveChangesAsyncCallCount.Should().Be(baselineSaveCount);
    }

    [TestMethod]
    public async Task ValidateAccessTokenAsync_InvalidatesSigningKeyCacheOnRotationAndKeepsRetiredKeysInGrace()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            AccessTokenValidationSigningKeyCacheTtl = TimeSpan.FromHours(1),
            AccessTokenValidationLastSeenDebounceInterval = TimeSpan.FromMinutes(10)
        });
        var service = new SqlOSCryptoService(context, options);

        await SeedValidationSessionAsync(context, service, DateTime.UtcNow);
        var user = context.Set<SqlOSUser>().Single();
        var session = context.Set<SqlOSSession>().Single();
        var client = context.Set<SqlOSClientApplication>().Single();
        var tokenBeforeRotation = await service.CreateAccessTokenAsync(user, session, client, "org_test");
        (await service.ValidateAccessTokenAsync(tokenBeforeRotation, client.Audience)).Should().NotBeNull();

        var newKey = await service.RotateSigningKeyAsync();
        var tokenAfterRotation = await service.CreateAccessTokenAsync(user, session, client, "org_test");

        newKey.IsActive.Should().BeTrue();
        (await service.ValidateAccessTokenAsync(tokenAfterRotation, client.Audience)).Should().NotBeNull();
        (await service.ValidateAccessTokenAsync(tokenBeforeRotation, client.Audience)).Should().NotBeNull();
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

    private static async Task SeedValidationSessionAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSCryptoService service,
        DateTime lastSeenAt)
    {
        await service.EnsureActiveSigningKeyAsync();

        var user = CreateUser();
        var session = CreateSession();
        var client = CreateClient();
        session.ClientApplicationId = client.Id;
        session.LastSeenAt = lastSeenAt;
        session.IdleExpiresAt = DateTime.UtcNow.AddHours(1);
        session.AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1);
        client.LastSeenAt = lastSeenAt;

        context.Set<SqlOSUser>().Add(user);
        context.Set<SqlOSSession>().Add(session);
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();
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
