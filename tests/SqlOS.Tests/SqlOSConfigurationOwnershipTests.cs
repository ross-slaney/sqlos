using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSConfigurationOwnershipTests
{
    [TestMethod]
    public async Task MfaSeed_IsIdempotent_ReadOnlyExceptEmergencyState_AndNonDestructiveWhenRemoved()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedMfaPolicy(seed => seed.RequireForOwnersAndAdmins = true);
        var crypto = TestCryptoService.Create(context, Options.Create(optionsValue));
        var service = new SqlOSSettingsService(context, Options.Create(optionsValue), new TestAuthEmailSender(), crypto);

        await service.UpsertSeededMfaSettingsAsync();
        await service.UpsertSeededMfaSettingsAsync();
        var seeded = await context.Set<SqlOSMfaSettings>().SingleAsync();
        seeded.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        seeded.ConfigurationSourceKey.Should().Be("mfa:default");
        seeded.ConfigurationFingerprint.Should().HaveLength(64);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled")).Should().Be(1, "an idempotent rerun must not create duplicate reconciliation outcomes");

        var mutate = () => service.UpdateMfaSettingsAsync(new SqlOSUpdateMfaSettingsRequest(
            false, false, true, true, false, true, ["owner", "admin"], ["totp", "recovery_code"]));
        await mutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");

        var disable = await service.UpdateMfaSettingsAsync(new SqlOSUpdateMfaSettingsRequest(
            false, true, true, true, false, true, ["owner", "admin"], ["totp", "recovery_code"]));
        disable.Enabled.Should().BeFalse();

        var noSeedService = new SqlOSSettingsService(context, Options.Create(new SqlOSAuthServerOptions()), new TestAuthEmailSender(), crypto);
        await noSeedService.UpsertSeededMfaSettingsAsync();
        seeded.ConfigurationOrphanedAt.Should().NotBeNull();
        seeded.RequireForOwnersAndAdmins.Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled")).Should().Be(2);
    }

    [TestMethod]
    public async Task DashboardUpdate_ClaimsOnlySystemDefault()
    {
        using var context = CreateContext();
        var service = new SqlOSSettingsService(context, Options.Create(new SqlOSAuthServerOptions()), new TestAuthEmailSender());
        await service.EnsureDefaultMfaSettingsAsync();

        var updated = await service.UpdateMfaSettingsAsync(new SqlOSUpdateMfaSettingsRequest(
            true, true, true, true, false, false, ["owner"], ["totp"]));

        updated.Ownership.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        updated.Ownership.IsEditable.Should().BeTrue();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
