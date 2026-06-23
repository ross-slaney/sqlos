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
    public async Task GetValidationSigningKeysAsync_ExcludesInactiveKeysWithoutRetiredAt()
    {
        using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Set<SqlOSSigningKey>().AddRange(
            CreateStoredSigningKey("key_active", isActive: true, activatedAt: now.AddDays(-1), retiredAt: null),
            CreateStoredSigningKey("key_recently_retired", isActive: false, activatedAt: now.AddDays(-3), retiredAt: now.AddDays(-1)),
            CreateStoredSigningKey("key_inactive_unretired", isActive: false, activatedAt: now.AddDays(-3), retiredAt: null),
            CreateStoredSigningKey("key_expired_retired", isActive: false, activatedAt: now.AddDays(-20), retiredAt: now.AddDays(-10)));
        await context.SaveChangesAsync();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));

        var keys = await service.GetValidationSigningKeysAsync(TimeSpan.FromDays(7));

        keys.Select(static key => key.Id).Should().BeEquivalentTo("key_active", "key_recently_retired");
    }

    [TestMethod]
    public async Task GetSigningKeyDiagnosticsAsync_FlagsInvalidLifecycleRows()
    {
        using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Set<SqlOSSigningKey>().AddRange(
            CreateStoredSigningKey("key_active_1", isActive: true, activatedAt: now.AddDays(-1), retiredAt: null),
            CreateStoredSigningKey("key_active_2", isActive: true, activatedAt: now, retiredAt: now),
            CreateStoredSigningKey("key_inactive_unretired", isActive: false, activatedAt: now.AddDays(-2), retiredAt: null));
        await context.SaveChangesAsync();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));

        var diagnostics = await service.GetSigningKeyDiagnosticsAsync(TimeSpan.FromDays(7));

        diagnostics.ActiveKeyCount.Should().Be(2);
        diagnostics.InactiveMissingRetiredAtCount.Should().Be(1);
        diagnostics.Issues.Select(static issue => issue.Code).Should().Contain([
            "multiple_active_signing_keys",
            "active_key_has_retired_at",
            "inactive_key_missing_retired_at"
        ]);
    }

    [TestMethod]
    public async Task RotateSigningKeyAsync_LeavesExactlyOneActiveKeyAndRetiredInactiveKeys()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));

        var original = await service.EnsureActiveSigningKeyAsync();
        var rotated = await service.RotateSigningKeyAsync();

        rotated.Id.Should().NotBe(original.Id);
        var keys = await context.Set<SqlOSSigningKey>().ToListAsync();
        keys.Should().ContainSingle(static key => key.IsActive);
        keys.Where(static key => !key.IsActive).Should().OnlyContain(static key => key.RetiredAt != null);
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
        context.Set<SqlOSSigningKey>()
            .Where(static key => !key.IsActive)
            .Should()
            .OnlyContain(static key => key.RetiredAt != null);
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

    private static SqlOSSigningKey CreateStoredSigningKey(
        string id,
        bool isActive,
        DateTime activatedAt,
        DateTime? retiredAt)
        => new()
        {
            Id = id,
            Kid = $"{id}_kid",
            Algorithm = "RS256",
            PublicKeyPem = "public",
            PrivateKeyPem = "private",
            IsActive = isActive,
            ActivatedAt = activatedAt,
            RetiredAt = retiredAt
        };

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
