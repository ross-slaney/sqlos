using System.Data.Common;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class ScimAdminHardeningIntegrationTests
{
    [TestMethod]
    public async Task SeedReconciliation_LocksSeededConnectionsInDeterministicOrder_AndConcurrentRunsComplete()
    {
        await using var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("ScimSeedLock");
        var connectionString = setupContext.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrWhiteSpace();
        var optionsValue = new SqlOSAuthServerOptions();
        var setupOptions = Options.Create(optionsValue);
        var setupAdmin = new SqlOSAdminService(
            setupContext,
            setupOptions,
            new SqlOSCryptoService(setupContext, setupOptions));
        var organization = await setupAdmin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest("Seed lock organization", "seed-lock"));
        optionsValue.SeedScimConnection("seed-lock", seed =>
        {
            seed.OrganizationId = organization.Id;
            seed.DisplayName = "Seed lock directory";
            seed.Enabled = false;
        });
        await setupAdmin.UpsertSeededScimConnectionsAsync();

        var interceptor = new CommandCaptureInterceptor();
        await using (var capturedContext = CreateContext(connectionString!, interceptor))
        {
            var capturedOptions = Options.Create(optionsValue);
            var capturedAdmin = new SqlOSAdminService(
                capturedContext,
                capturedOptions,
                new SqlOSCryptoService(capturedContext, capturedOptions));
            await capturedAdmin.UpsertSeededScimConnectionsAsync();
        }

        interceptor.Commands.Should().Contain(command =>
            command.Contains("WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)
            && command.Contains("WHERE [Source] = @source", StringComparison.Ordinal)
            && command.Contains("ORDER BY [Id]", StringComparison.Ordinal));

        var contexts = Enumerable.Range(0, 4)
            .Select(_ => CreateContext(connectionString!))
            .ToList();
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconciliations = contexts.Select(context => Task.Run(async () =>
            {
                await ready.Task;
                var options = Options.Create(optionsValue);
                var admin = new SqlOSAdminService(context, options, new SqlOSCryptoService(context, options));
                await admin.UpsertSeededScimConnectionsAsync();
            })).ToArray();
            ready.SetResult();

            await Task.WhenAll(reconciliations).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
            await setupContext.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task ConnectionDisable_RevokesLargeGrantSet_WithConstantPersistenceAndBoundedEvidence()
    {
        await using var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("ScimGrantBatch");
        try
        {
            var connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var fgaOptions = Options.Create(new SqlOSFgaOptions());
            await new SqlOSFgaSchemaInitializer(
                    setupContext,
                    fgaOptions,
                    loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>())
                .EnsureSchemaAsync();

            var optionsValue = new SqlOSAuthServerOptions();
            var setupOptions = Options.Create(optionsValue);
            var setupAdmin = new SqlOSAdminService(
                setupContext,
                setupOptions,
                new SqlOSCryptoService(setupContext, setupOptions));
            var organization = await setupAdmin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest("Grant batch organization", "grant-batch"));
            var connection = await setupAdmin.CreateScimConnectionAsync(
                new SqlOSCreateScimConnectionRequest(organization.Id, "Grant batch directory", Enabled: true));
            var mapping = await setupAdmin.CreateScimGroupMappingAsync(
                connection.ConnectionId,
                new SqlOSCreateScimGroupMappingRequest(
                    SqlOSScimGroupMappingMatchTypes.DisplayName,
                    "Grant batch group",
                    GroupExternalId: null,
                    GroupPattern: null,
                    RoleKey: "batch_role",
                    ResourceId: "batch_resource",
                    ResourceIdTemplate: null,
                    Enabled: true));

            var now = DateTime.UtcNow;
            setupContext.Set<SqlOSFgaSubjectType>().Add(new SqlOSFgaSubjectType
            {
                Id = "batch_group_type",
                Name = "Batch group"
            });
            setupContext.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
            {
                Id = "batch_group_subject",
                SubjectTypeId = "batch_group_type",
                OrganizationId = organization.Id,
                DisplayName = "Grant batch group",
                CreatedAt = now,
                UpdatedAt = now
            });
            setupContext.Set<SqlOSFgaUserGroup>().Add(new SqlOSFgaUserGroup
            {
                Id = "batch_group",
                SubjectId = "batch_group_subject",
                Name = "Grant batch group",
                GroupType = "scim",
                CreatedAt = now,
                UpdatedAt = now
            });
            setupContext.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType
            {
                Id = "batch_resource_type",
                Name = "Batch resource"
            });
            setupContext.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = "batch_resource",
                ResourceTypeId = "batch_resource_type",
                Name = "Batch resource",
                CreatedAt = now,
                UpdatedAt = now
            });
            setupContext.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole
            {
                Id = "batch_role",
                Key = "batch_role",
                Name = "Batch role"
            });
            const int grantCount = 129;
            setupContext.Set<SqlOSFgaGrant>().AddRange(Enumerable.Range(0, grantCount).Select(index =>
                new SqlOSFgaGrant
                {
                    Id = $"batch_grant_{index:D3}",
                    SubjectId = "batch_group_subject",
                    ResourceId = "batch_resource",
                    RoleId = "batch_role",
                    CreatedAt = now,
                    UpdatedAt = now
                }));
            setupContext.Set<SqlOSScimManagedGrant>().AddRange(Enumerable.Range(0, grantCount).Select(index =>
                new SqlOSScimManagedGrant
                {
                    Id = $"batch_managed_{index:D3}",
                    ConnectionId = connection.ConnectionId,
                    MappingId = mapping.Id,
                    GroupExternalId = "batch_group_external",
                    FgaGroupId = "batch_group",
                    FgaGroupSubjectId = "batch_group_subject",
                    GrantId = $"batch_grant_{index:D3}",
                    RoleId = "batch_role",
                    ResourceId = "batch_resource",
                    CreatedAt = now
                }));
            await setupContext.SaveChangesAsync();

            var persistence = new SaveChangesCaptureInterceptor();
            await using var revokeContext = CreateContext(connectionString!, persistence);
            var revokeOptions = Options.Create(optionsValue);
            var revokeAdmin = new SqlOSAdminService(
                revokeContext,
                revokeOptions,
                new SqlOSCryptoService(revokeContext, revokeOptions));

            await revokeAdmin.SetScimConnectionEnabledAsync(connection.ConnectionId, enabled: false);

            persistence.SaveChangesCalls.Should().BeLessOrEqualTo(5,
                "revocation persistence must remain constant rather than flushing once per managed grant");
            (await revokeContext.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
            (await revokeContext.Set<SqlOSScimManagedGrant>()
                    .CountAsync(managed => managed.RevokedAt != null))
                .Should().Be(grantCount);
            var syncEvents = await revokeContext.Set<SqlOSScimSyncEvent>()
                .Where(item => item.Action == "scim.grant.revoked")
                .ToListAsync();
            syncEvents.Should().ContainSingle();
            var evidence = JsonNode.Parse(syncEvents.Single().DataJson!)!.AsObject();
            evidence["revokedManagedGrantCount"]!.GetValue<int>().Should().Be(grantCount);
            evidence["deletedGrantCount"]!.GetValue<int>().Should().Be(grantCount);
            evidence["evidenceTruncated"]!.GetValue<bool>().Should().BeTrue();
            evidence["evidence"]!.AsArray().Should().HaveCount(32);
            evidence["evidence"]![0]!["mappingId"]!.GetValue<string>().Should().Be(mapping.Id);
            evidence["evidence"]![0]!["grantId"]!.GetValue<string>().Should().Be("batch_grant_000");
            evidence["evidence"]![0]!["resourceId"]!.GetValue<string>().Should().Be("batch_resource");
            (await revokeContext.Set<SqlOSAuditEvent>()
                    .CountAsync(item => item.Action == "scim.grant.revoked"))
                .Should().Be(1);
        }
        finally
        {
            await setupContext.Database.EnsureDeletedAsync();
        }
    }

    private static TestSqlOSDbContext CreateContext(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new TestSqlOSDbContext(builder.Options);
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveChangesCaptureInterceptor : SaveChangesInterceptor
    {
        private int _saveChangesCalls;

        public int SaveChangesCalls => Volatile.Read(ref _saveChangesCalls);

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _saveChangesCalls);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveChangesCalls);
            return ValueTask.FromResult(result);
        }
    }
}
