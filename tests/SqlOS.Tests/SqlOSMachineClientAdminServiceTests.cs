using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
public sealed class SqlOSMachineClientAdminServiceTests
{
    [TestMethod]
    public async Task DashboardCreateRotateValidateAndRevoke_RevealsSecretsOnlyOnce()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        var (organization, resource, role) = await SeedDependenciesAsync(context, admin);

        var created = await service.CreateAsync(new SqlOSCreateMachineClientRequest(
            "nightly-worker", "Nightly worker", "Runs nightly jobs", "https://api.example.test",
            ["jobs.run"], organization.Id, DateTime.UtcNow.AddDays(30), [new(resource.Id, role.Id)]));

        created.ClientSecret.Should().HaveLength(64);
        var listJson = JsonSerializer.Serialize(await service.ListAsync());
        listJson.Should().NotContain(created.ClientSecret);
        listJson.Should().NotContain("ClientSecretHash");
        (await service.ValidateCredentialAsync("nightly-worker", created.ClientSecret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeTrue();
        (await service.ValidateCredentialAsync("nightly-worker", created.ClientSecret, "https://wrong.example.test", ["jobs.run"])).Valid.Should().BeFalse();

        var rotated = await service.RotateAsync("nightly-worker");
        rotated.ClientSecret.Should().NotBe(created.ClientSecret);
        (await service.ValidateCredentialAsync("nightly-worker", created.ClientSecret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeFalse();
        (await service.ValidateCredentialAsync("nightly-worker", rotated.ClientSecret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeTrue();

        await service.RevokeAsync("nightly-worker");
        (await service.ValidateCredentialAsync("nightly-worker", rotated.ClientSecret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeFalse();
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1, "revocation and grant removal have distinct effects");
        JsonSerializer.Serialize(await context.Set<SqlOSAuditEvent>().ToListAsync()).Should().NotContain(created.ClientSecret).And.NotContain(rotated.ClientSecret);
    }

    [TestMethod]
    public async Task ListAsync_SkipsFgaOnlyServiceAccounts_SoTheFirstPageShowsOAuthMachineClients()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        var (organization, resource, role) = await SeedDependenciesAsync(context, admin);

        context.Set<SqlOSFgaSubject>().Add(new()
        {
            Id = "sub_sa_bulk_0001",
            SubjectTypeId = "service_account",
            DisplayName = "Bulk Service Account 0001"
        });
        context.Set<SqlOSFgaServiceAccount>().Add(new()
        {
            Id = "sa_bulk_0001",
            SubjectId = "sub_sa_bulk_0001",
            ClientId = "aaa-bulk-only",
            ClientSecretHash = "bulk-not-a-real-hash",
            Description = "FGA-only account"
        });
        await context.SaveChangesAsync();

        await service.CreateAsync(new SqlOSCreateMachineClientRequest(
            "nightly-worker", "Nightly worker", null, "https://api.example.test",
            ["jobs.run"], organization.Id, null, [new(resource.Id, role.Id)]));

        var list = JsonSerializer.Serialize(await service.ListAsync(pageSize: 25));
        list.Should().Contain("nightly-worker");
        list.Should().NotContain("aaa-bulk-only");
    }

    [TestMethod]
    public async Task CodeSeed_IsIdempotentOwnershipSafeAndOrphanVisible()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        var secret = new string('s', 64);
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var (organization, resource, role) = await SeedDependenciesAsync(context, admin);
        optionsValue.SeedMachineClient("seeded-worker", (client, machine) =>
        {
            client.Name = "Seeded worker";
            client.Audience = "https://api.example.test";
            client.AllowedScopes = ["jobs.run"];
            machine.OrganizationId = organization.Id;
            machine.SecretResolver = () => secret;
            machine.Grant(resource.Id, role.Id, "Run jobs");
        });
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);

        await admin.UpsertSeededClientsAsync();
        await service.UpsertSeededMachineClientsAsync();
        var originalGrantId = await context.Set<SqlOSFgaGrant>().Select(x => x.Id).SingleAsync();
        await service.UpsertSeededMachineClientsAsync();

        var account = await context.Set<SqlOSFgaServiceAccount>().Include(x => x.Subject).SingleAsync();
        account.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        account.ConfigurationSourceKey.Should().Be("seeded-worker");
        account.Subject!.OrganizationId.Should().Be(organization.Id);
        (await context.Set<SqlOSFgaGrant>().CountAsync(x => x.SubjectId == account.SubjectId)).Should().Be(1);
        (await context.Set<SqlOSFgaGrant>().Select(x => x.Id).SingleAsync()).Should().Be(originalGrantId);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled" && x.DataJson != null && x.DataJson.Contains("machine_client"))).Should().Be(1);
        (await service.ValidateCredentialAsync("seeded-worker", secret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeTrue();
        await FluentActions.Invoking(() => service.RotateAsync("seeded-worker"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*code-owned*");
        await FluentActions.Invoking(() => service.RevokeAsync("seeded-worker"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*code-owned*");
        var accountAfterRejectedRevoke = await context.Set<SqlOSFgaServiceAccount>().SingleAsync();
        accountAfterRejectedRevoke.ExpiresAt.Should().BeNull();
        (await context.Set<SqlOSClientApplication>().SingleAsync()).IsActive.Should().BeTrue();
        (await context.Set<SqlOSClientCredential>().SingleAsync(x => x.RevokedAt == null)).RevokedAt.Should().BeNull();

        optionsValue.ClientSeeds.Clear();
        await service.UpsertSeededMachineClientsAsync();
        account.ConfigurationOrphanedAt.Should().NotBeNull();
        account.ExpiresAt.Should().BeNull("orphan visibility must not silently revoke a worker");
    }

    [TestMethod]
    public async Task CodeOwnedEmergencyDisable_IsIdempotentAuditedAndSurvivesReconciliation()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        var secret = new string('s', 64);
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var (organization, resource, role) = await SeedDependenciesAsync(context, admin);
        optionsValue.SeedMachineClient("seeded-worker", (client, machine) =>
        {
            client.Name = "Seeded worker";
            client.Audience = "https://api.example.test";
            client.AllowedScopes = ["jobs.run"];
            machine.OrganizationId = organization.Id;
            machine.SecretResolver = () => secret;
            machine.Grant(resource.Id, role.Id, "Run jobs");
        });
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        await admin.UpsertSeededClientsAsync();
        await service.UpsertSeededMachineClientsAsync();
        var originalExpiry = (await context.Set<SqlOSFgaServiceAccount>().SingleAsync()).ExpiresAt;
        var originalGrantId = await context.Set<SqlOSFgaGrant>().Select(x => x.Id).SingleAsync();
        var originalHash = (await context.Set<SqlOSClientCredential>().SingleAsync()).SecretHash;

        var disabled = await service.EmergencyDisableAsync("seeded-worker");
        var repeated = await service.EmergencyDisableAsync("seeded-worker");
        disabled.EmergencyDisabled.Should().BeTrue();
        disabled.Ready.Should().BeFalse();
        disabled.Ownership.IsEditable.Should().BeFalse();
        disabled.Ownership.CanEmergencyDisable.Should().BeTrue();
        repeated.EmergencyDisabled.Should().BeTrue();
        (await service.ValidateCredentialAsync("seeded-worker", secret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeFalse();

        await admin.UpsertSeededClientsAsync();
        await service.UpsertSeededMachineClientsAsync();
        var client = await context.Set<SqlOSClientApplication>().SingleAsync();
        var account = await context.Set<SqlOSFgaServiceAccount>().SingleAsync();
        client.IsActive.Should().BeFalse();
        client.DisabledAt.Should().NotBeNull();
        client.DisabledReason.Should().Be(SqlOSMachineClientAdminService.EmergencyDisabledReason);
        account.ExpiresAt.Should().Be(originalExpiry);
        account.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        (await context.Set<SqlOSFgaGrant>().Select(x => x.Id).SingleAsync()).Should().Be(originalGrantId);
        (await context.Set<SqlOSClientCredential>().SingleAsync()).SecretHash.Should().Be(originalHash);
        (await context.Set<SqlOSClientCredential>().SingleAsync()).RevokedAt.Should().BeNull();
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "machine_client.emergency_disabled")).Should().Be(1);

        var enabled = await service.EmergencyEnableAsync("seeded-worker");
        enabled.EmergencyDisabled.Should().BeFalse();
        enabled.Ready.Should().BeTrue();
        (await service.ValidateCredentialAsync("seeded-worker", secret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "machine_client.emergency_enabled")).Should().Be(1);
        await service.EmergencyEnableAsync("seeded-worker");
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "machine_client.emergency_enabled")).Should().Be(1);
    }

    [TestMethod]
    public async Task EmergencyEnable_RefusesStructuralRevokeAndSeedDisable()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        var (organization, resource, role) = await SeedDependenciesAsync(context, admin);
        var created = await service.CreateAsync(new SqlOSCreateMachineClientRequest(
            "nightly-worker", "Nightly worker", null, "https://api.example.test",
            ["jobs.run"], organization.Id, null, [new(resource.Id, role.Id)]));

        await service.RevokeAsync("nightly-worker");
        await FluentActions.Invoking(() => service.EmergencyEnableAsync("nightly-worker"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*revoked*");
        (await service.ValidateCredentialAsync("nightly-worker", created.ClientSecret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeFalse();

        var seedOptionsValue = new SqlOSAuthServerOptions();
        var seedOptions = Options.Create(seedOptionsValue);
        var seedCrypto = TestCryptoService.Create(context, seedOptions);
        var seedAdmin = new SqlOSAdminService(context, seedOptions, seedCrypto);
        seedOptionsValue.SeedMachineClient("seed-disabled", (client, machine) =>
        {
            client.Name = "Seed disabled";
            client.Audience = "https://api.example.test";
            client.AllowedScopes = ["jobs.run"];
            client.IsActive = false;
            machine.SecretResolver = () => new string('s', 64);
        });
        var seedService = new SqlOSMachineClientAdminService(context, seedAdmin, seedCrypto, seedOptions);
        await seedAdmin.UpsertSeededClientsAsync();
        await seedService.UpsertSeededMachineClientsAsync();
        await FluentActions.Invoking(() => seedService.EmergencyEnableAsync("seed-disabled"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*seed*");
    }

    [TestMethod]
    public async Task CodeSeed_FailsClosedWithoutSecretAndDoesNotAdoptDashboardAccount()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        var (_, resource, role) = await SeedDependenciesAsync(context, admin);
        optionsValue.SeedMachineClient("missing-secret", (client, machine) =>
        {
            client.Name = "Missing"; client.Audience = "api"; client.AllowedScopes = ["run"];
            machine.SecretResolver = () => null;
            machine.Grant(resource.Id, role.Id);
        });
        await FluentActions.Invoking(() => admin.UpsertSeededClientsAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*43 to 256*");

        optionsValue.ClientSeeds[0].MachineClient!.SecretResolver = null;
        optionsValue.ClientSeeds[0].MachineClient!.SecretHashResolver = () => "not-a-password-hash";
        await FluentActions.Invoking(() => admin.UpsertSeededClientsAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*unsupported PasswordHasher payload*");
    }

    [TestMethod]
    public async Task LegacyMachineClientCredential_IsMigratedOnceToOAuthClientCredentialStore()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var service = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        const string secret = "legacy-secret-with-at-least-256-bits-of-entropy-123456789";
        context.Set<SqlOSClientApplication>().Add(new()
        {
            Id = "legacy-app",
            ClientId = "legacy-worker",
            Name = "Legacy worker",
            ClientType = "confidential",
            TokenEndpointAuthMethod = "client_secret_basic",
            GrantTypesJson = "[\"client_credentials\"]",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSFgaSubject>().Add(new()
        {
            Id = "service_account::legacy-worker",
            SubjectTypeId = "service_account",
            DisplayName = "Legacy worker"
        });
        context.Set<SqlOSFgaServiceAccount>().Add(new()
        {
            Id = "legacy-account",
            SubjectId = "service_account::legacy-worker",
            ClientId = "legacy-worker",
            ClientSecretHash = crypto.HashPassword(secret)
        });
        await context.SaveChangesAsync();

        await service.MigrateLegacyClientCredentialsAsync();
        await service.MigrateLegacyClientCredentialsAsync();

        var credential = await context.Set<SqlOSClientCredential>().SingleAsync();
        crypto.VerifyPassword(credential.SecretHash, secret).Should().BeTrue();
        credential.DisplayName.Should().Be("Migrated machine-client credential");
    }

    private static async Task<(SqlOSOrganization Organization, SqlOSFgaResource Resource, SqlOSFgaRole Role)> SeedDependenciesAsync(TestSqlOSInMemoryDbContext context, SqlOSAdminService admin)
    {
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Machines", $"machines-{Guid.NewGuid():N}"));
        var resource = new SqlOSFgaResource { Id = $"res_{Guid.NewGuid():N}", Name = "Jobs", ResourceTypeId = "workspace", IsActive = true };
        var role = new SqlOSFgaRole { Id = $"role_{Guid.NewGuid():N}", Key = "runner", Name = "Runner" };
        context.Set<SqlOSFgaResource>().Add(resource);
        context.Set<SqlOSFgaRole>().Add(role);
        await context.SaveChangesAsync();
        return (organization, resource, role);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
}
