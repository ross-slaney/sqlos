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
        await service.UpsertSeededMachineClientsAsync();

        var account = await context.Set<SqlOSFgaServiceAccount>().Include(x => x.Subject).SingleAsync();
        account.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        account.ConfigurationSourceKey.Should().Be("seeded-worker");
        account.Subject!.OrganizationId.Should().Be(organization.Id);
        (await context.Set<SqlOSFgaGrant>().CountAsync(x => x.SubjectId == account.SubjectId)).Should().Be(1);
        (await service.ValidateCredentialAsync("seeded-worker", secret, "https://api.example.test", ["jobs.run"])).Valid.Should().BeTrue();
        await FluentActions.Invoking(() => service.RotateAsync("seeded-worker"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*code-owned*");

        optionsValue.ClientSeeds.Clear();
        await service.UpsertSeededMachineClientsAsync();
        account.ConfigurationOrphanedAt.Should().NotBeNull();
        account.ExpiresAt.Should().BeNull("orphan visibility must not silently revoke a worker");
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
        await admin.UpsertSeededClientsAsync();
        await FluentActions.Invoking(() => service.UpsertSeededMachineClientsAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*43 to 256*");
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
