using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #325: timestamps SqlOS reads back through SQL carry <see cref="DateTimeKind.Utc"/>, so JSON writes
/// them with a <c>Z</c> and browsers no longer read UTC as local time.
/// </summary>
[TestClass]
public sealed class UtcTimestampIntegrationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // The instant from the #325 report: a 15-minute device code created at 7:22 PM PDT.
    private static readonly DateTime ExpiresAt = new(2026, 8, 22, 2, 37, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task Timestamps_read_back_through_sql_are_utc_and_serialize_with_z()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("UtcTimestamps");
        try
        {
            await new SqlOSFgaSchemaInitializer(setup, Options.Create(AspireFixture.FgaOptions), NullLogger<SqlOSFgaSchemaInitializer>.Instance)
                .EnsureSchemaAsync();
            var options = Options.Create(AspireFixture.Options);
            var crypto = new SqlOSCryptoService(setup, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(setup, options, crypto);
            var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
                "utc-device", "UTC Device", "sqlos", [], AllowDeviceAuthorization: true));
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("UTC Org", "utc-org"));
            await admin.RecordAuditAsync("utc.regression", "system", null, organizationId: organization.Id);
            setup.Set<SqlOSDeviceAuthorization>().Add(new SqlOSDeviceAuthorization
            {
                Id = "dev_utc",
                DeviceCodeHash = crypto.HashToken("device-code"),
                UserCodeHash = crypto.HashToken("ABCD-EFGH"),
                UserCode = "ABCD-EFGH",
                ClientApplicationId = client.Id,
                Scope = "openid",
                CreatedAt = ExpiresAt.AddMinutes(-15),
                ExpiresAt = ExpiresAt,
                ApprovedAt = ExpiresAt.AddMinutes(-5)
            });
            setup.Set<SqlOSFgaResourceType>().Add(FgaTestModel.ResourceType("utc_type", "UTC"));
            setup.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = "utc_resource",
                Name = "UTC",
                ResourceTypeId = "utc_type",
                CreatedAt = ExpiresAt,
                UpdatedAt = ExpiresAt
            });
            await setup.SaveChangesAsync();

            await using var fresh = new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseTestProvider(setup.Database.GetConnectionString()!)
                .Options);

            var device = await fresh.Set<SqlOSDeviceAuthorization>().AsNoTracking().SingleAsync(x => x.Id == "dev_utc");
            device.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
            device.ExpiresAt.Should().Be(ExpiresAt);
            device.ApprovedAt!.Value.Kind.Should().Be(DateTimeKind.Utc, "nullable timestamps are converted too");
            device.DeniedAt.Should().BeNull();
            var headless = new SqlOSHeadlessDeviceAuthorizationDto(
                device.UserCode, client.ClientId, client.Name, device.Scope, device.Resource, device.ExpiresAt, device.Status);
            JsonSerializer.Serialize(headless, Web).Should().Contain("\"expiresAt\":\"2026-08-22T02:37:00Z\"");

            var projected = await fresh.Set<SqlOSDeviceAuthorization>()
                .Where(x => x.Id == "dev_utc")
                .Select(x => new { x.CreatedAt, x.ApprovedAt })
                .SingleAsync();
            projected.CreatedAt.Kind.Should().Be(DateTimeKind.Utc, "LINQ projections read through the same mapping");
            projected.ApprovedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

            // The admin and dashboard read the organization through this projection.
            var organizationView = await new SqlOSAdminService(fresh, options, crypto).GetOrganizationAsync(organization.Id);
            JsonSerializer.Serialize(organizationView, Web).Should().MatchRegex("\"createdAt\":\"[0-9T:.-]+Z\"");

            var audit = await new SqlOSAuditLogService(fresh, crypto).ListAsync(new SqlOSAuditLogListRequest(OrganizationId: organization.Id));
            var auditEvent = audit.Data.Should().ContainSingle().Subject;
            auditEvent.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
            auditEvent.IngestedAt.Kind.Should().Be(DateTimeKind.Utc);
            JsonSerializer.Serialize(auditEvent, Web).Should().MatchRegex("\"occurredAt\":\"[0-9T:.-]+Z\"");

            var resource = await fresh.Set<SqlOSFgaResource>().AsNoTracking().SingleAsync(x => x.Id == "utc_resource");
            resource.CreatedAt.Kind.Should().Be(DateTimeKind.Utc, "FGA entities follow the same convention");
            resource.CreatedAt.Should().Be(ExpiresAt);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task Expiry_comparisons_against_utc_now_behave_as_before()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("UtcComparisons");
        try
        {
            var options = Options.Create(AspireFixture.Options);
            var crypto = new SqlOSCryptoService(setup, options, AspireFixture.DataProtectionProvider);
            var live = await crypto.CreateTemporaryTokenAsync("utc_test", null, null, null, null, TimeSpan.FromMinutes(5));
            var expired = await crypto.CreateTemporaryTokenAsync("utc_test", null, null, null, null, TimeSpan.FromMinutes(5));
            var expiredRow = await setup.Set<SqlOSTemporaryToken>().SingleAsync(x => x.TokenHash == crypto.HashToken(expired));
            setup.Entry(expiredRow).Property(x => x.ExpiresAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
            await setup.SaveChangesAsync();

            await using var fresh = new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseTestProvider(setup.Database.GetConnectionString()!)
                .Options);
            var freshCrypto = new SqlOSCryptoService(fresh, options, AspireFixture.DataProtectionProvider);

            (await freshCrypto.FindTemporaryTokenAsync("utc_test", live)).Should().NotBeNull();
            (await freshCrypto.FindTemporaryTokenAsync("utc_test", expired)).Should().BeNull();
            var row = await fresh.Set<SqlOSTemporaryToken>().AsNoTracking().SingleAsync(x => x.TokenHash == crypto.HashToken(live));
            (row.ExpiresAt > DateTime.UtcNow).Should().BeTrue("a UTC value compares with UtcNow by instant, as before");
            (row.ExpiresAt - row.CreatedAt).Should().Be(TimeSpan.FromMinutes(5));
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }
}
