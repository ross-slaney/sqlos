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
    public async Task EnsureActiveSigningKey_WithDataProtection_StoresProtectedPrivateKey()
    {
        using var context = CreateContext();
        var service = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
            new EphemeralDataProtectionProvider());

        var key = await service.EnsureActiveSigningKeyAsync();

        key.PrivateKeyPem.Should().StartWith("dp:");
        key.PrivateKeyPem.Should().NotContain("BEGIN PRIVATE KEY");
        service.UnprotectSecret(key.PrivateKeyPem).Should().Contain("BEGIN PRIVATE KEY");
    }

    [TestMethod]
    public async Task EnsureActiveSigningKey_WithDataProtection_ProtectsLegacyPlaintextPrivateKey()
    {
        using var context = CreateContext();
        var plaintextService = new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions()));
        var legacyKey = await plaintextService.EnsureActiveSigningKeyAsync();
        legacyKey.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");

        var protectedService = new SqlOSCryptoService(
            context,
            Options.Create(new SqlOSAuthServerOptions()),
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
            Options.Create(new SqlOSAuthServerOptions()),
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
}
