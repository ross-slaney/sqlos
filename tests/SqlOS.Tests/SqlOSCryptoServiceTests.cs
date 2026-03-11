using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
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
