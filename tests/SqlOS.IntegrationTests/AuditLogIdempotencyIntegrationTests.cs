using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuditLogIdempotencyIntegrationTests
{
    [TestMethod]
    public async Task ConcurrentRealSqlAuditWrites_InsertExactlyOncePerCanonicalNamespace()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("AuditIdempotency");
        try
        {
            var connectionString = setup.Database.GetConnectionString()!;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<SqlOSAuditLogRecordResult> RecordAsync()
            {
                await using var context = CreateContext(connectionString);
                var service = CreateService(context);
                await ready.Task;
                return await service.RecordAsync(CreateRequest("org_1", "workspace-web", "application"));
            }

            var attempts = Enumerable.Range(0, 8).Select(_ => RecordAsync()).ToArray();
            ready.SetResult();
            var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(45));

            results.Should().ContainSingle(x => x.Created);
            results.Select(x => x.EventId).Distinct().Should().ContainSingle();
            setup.ChangeTracker.Clear();
            (await setup.Set<SqlOSAuditEvent>().AsNoTracking().CountAsync()).Should().Be(1);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task RealSqlAuditIdempotency_AllowsSameKeyAcrossTenantApplicationSourceAndGlobalScopes()
    {
        await using var context = await AspireFixture.CreateIsolatedAuthContextAsync("AuditScopes");
        try
        {
            var service = CreateService(context);

            var results = new[]
            {
                await service.RecordAsync(CreateRequest("org_1", "workspace-web", "application")),
                await service.RecordAsync(CreateRequest("org_2", "workspace-web", "application")),
                await service.RecordAsync(CreateRequest(null, "workspace-web", "application")),
                await service.RecordAsync(CreateRequest("org_1", "admin-web", "application")),
                await service.RecordAsync(CreateRequest("org_1", "workspace-web", "authserver"))
            };

            results.Should().OnlyContain(x => x.Created);
            results.Select(x => x.EventId).Should().OnlyHaveUniqueItems();
            (await context.Set<SqlOSAuditEvent>().AsNoTracking().CountAsync()).Should().Be(5);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task RealSqlAuditIdempotency_HashCollisionReturnsTypedConflictWithoutOtherScopeEvent()
    {
        await using var context = await AspireFixture.CreateIsolatedAuthContextAsync("AuditConflict");
        try
        {
            const string key = "business-operation-42";
            context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
            {
                Id = "evt_other_scope",
                OrganizationId = "org_other",
                ApplicationKey = "workspace-web",
                Source = "application",
                Action = "document.shared",
                EventType = "document.shared",
                ActorType = "system",
                TargetsJson = "[]",
                OccurredAt = DateTime.UtcNow,
                IngestedAt = DateTime.UtcNow,
                IdempotencyScopeHash = SqlOSAuditLogService.HashIdempotencyScope(
                    "org_requested", null, "workspace-web", "application", "document.shared", key)
            });
            await context.SaveChangesAsync();
            var service = CreateService(context);

            var act = async () => await service.RecordAsync(CreateRequest(
                "org_requested", "workspace-web", "application", key));

            var exception = await act.Should().ThrowAsync<SqlOSAuditLogIdempotencyConflictException>();
            exception.Which.Error.Should().Be(SqlOSAuditLogIdempotencyConflictException.ErrorCode);
            exception.Which.Message.Should().NotContain("evt_other_scope").And.NotContain(key);
            (await context.Set<SqlOSAuditEvent>().AsNoTracking().CountAsync()).Should().Be(1);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static SqlOSAuditLogRecordRequest CreateRequest(
        string? organizationId,
        string applicationKey,
        string source,
        string idempotencyKey = "business-operation-42")
        => new(
            Action: "document.shared",
            OrganizationId: organizationId,
            ApplicationKey: applicationKey,
            Source: source,
            Actor: new SqlOSAuditActor("user", "usr_1"),
            Targets: [new SqlOSAuditTarget("document", "doc_1")],
            IdempotencyKey: idempotencyKey);

    private static TestSqlOSDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseTestProvider(connectionString)
            .Options);

    private static SqlOSAuditLogService CreateService(TestSqlOSDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        return new SqlOSAuditLogService(context, new SqlOSCryptoService(context, options));
    }
}
