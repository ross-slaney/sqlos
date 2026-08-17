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

    [TestMethod]
    public async Task AuthPageSeed_IsIdempotent_ReadOnly_AndNonDestructiveWhenRemoved()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedAuthPage(page =>
        {
            page.PageTitle = "Owned Sign in";
            page.PageSubtitle = "Owned workspace";
            page.PrimaryColor = "#4f46e5";
            page.Layout = "stacked";
        });
        var crypto = TestCryptoService.Create(context, Options.Create(optionsValue));
        var service = new SqlOSSettingsService(context, Options.Create(optionsValue), new TestAuthEmailSender(), crypto);

        await service.UpsertSeededAuthPageSettingsAsync();
        await service.UpsertSeededAuthPageSettingsAsync();
        var seeded = await context.Set<SqlOSAuthPageSettings>().SingleAsync();
        seeded.AuthPageConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        seeded.AuthPageConfigurationSourceKey.Should().Be(SqlOSSettingsService.AuthPageSourceKey);
        seeded.AuthPageConfigurationFingerprint.Should().HaveLength(64);
        seeded.EmailConfigurationOwner.Should().Be(SqlOSConfigurationOwners.System);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled")).Should().Be(1);

        var mutate = () => service.UpdateAuthPageSettingsAsync(AuthPageRequest(pageTitle: "Dashboard title"));
        await mutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");

        var email = await service.UpdateAuthEmailBrandingSettingsAsync(new SqlOSUpdateAuthEmailBrandingSettingsRequest(
            "Dashboard Mail", null, "#16a34a", "#111827", "#f0fdf4"));
        email.Ownership!.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        email.ApplicationName.Should().Be("Dashboard Mail");

        var noSeedService = new SqlOSSettingsService(context, Options.Create(new SqlOSAuthServerOptions()), new TestAuthEmailSender(), crypto);
        await noSeedService.UpsertSeededAuthPageSettingsAsync();
        seeded.AuthPageConfigurationOrphanedAt.Should().NotBeNull();
        seeded.PageTitle.Should().Be("Owned Sign in");
        (await context.Set<SqlOSAuthPageSettings>().SingleAsync()).EmailApplicationName.Should().Be("Dashboard Mail");
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled")).Should().Be(2);
    }

    [TestMethod]
    public async Task AuthEmailSeed_IsIdempotent_ReadOnly_AndFailsClosedOnDashboardCollision()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedAuthEmails(email =>
        {
            email.ApplicationName = "Owned Mail";
            email.PrimaryColor = "#16a34a";
        });
        var crypto = TestCryptoService.Create(context, Options.Create(optionsValue));
        var service = new SqlOSSettingsService(context, Options.Create(optionsValue), new TestAuthEmailSender(), crypto);

        await service.UpsertSeededAuthEmailSettingsAsync();
        await service.UpsertSeededAuthEmailSettingsAsync();
        var seeded = await context.Set<SqlOSAuthPageSettings>().SingleAsync();
        seeded.EmailConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        seeded.EmailConfigurationSourceKey.Should().Be(SqlOSSettingsService.AuthEmailSourceKey);
        seeded.AuthPageConfigurationOwner.Should().Be(SqlOSConfigurationOwners.System);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled")).Should().Be(1);

        var mutate = () => service.UpdateAuthEmailBrandingSettingsAsync(new SqlOSUpdateAuthEmailBrandingSettingsRequest(
            "Dashboard Mail", null, "#4f46e5", "#111827", "#f5f3ff"));
        await mutate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code'*");

        var page = await service.UpdateAuthPageSettingsAsync(AuthPageRequest());
        page.Ownership!.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        page.PageTitle.Should().Be("Parity Sign in");

        var colliding = new SqlOSAuthServerOptions();
        colliding.SeedAuthPage(seed => seed.PageTitle = "Code collision");
        var collidingService = new SqlOSSettingsService(context, Options.Create(colliding), new TestAuthEmailSender(), crypto);
        var collide = () => collidingService.UpsertSeededAuthPageSettingsAsync();
        await collide.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by 'dashboard'*");
    }

    [TestMethod]
    public async Task DashboardAuthPageUpdate_ClaimsOnlySystemDefault()
    {
        using var context = CreateContext();
        var service = new SqlOSSettingsService(context, Options.Create(new SqlOSAuthServerOptions()), new TestAuthEmailSender());
        var updated = await service.UpdateAuthPageSettingsAsync(AuthPageRequest());
        updated.Ownership!.Owner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        updated.Ownership.IsEditable.Should().BeTrue();
        updated.Ownership.CanEmergencyDisable.Should().BeFalse();
        updated.ManagedByStartupSeed.Should().BeFalse();

        var email = await service.GetAuthEmailBrandingSettingsAsync();
        email.Ownership!.Owner.Should().Be(SqlOSConfigurationOwners.System);
        email.Ownership.IsEditable.Should().BeTrue();
    }

    private static SqlOSUpdateAuthPageSettingsRequest AuthPageRequest(string pageTitle = "Parity Sign in")
        => new(null, "#0f766e", "#111827", "#f5f3ff", "stacked", pageTitle, "Parity workspace", true, ["password"]);

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
