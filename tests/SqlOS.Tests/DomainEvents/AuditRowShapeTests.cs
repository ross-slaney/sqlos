using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.DomainEvents;

/// <summary>
/// Pins the exact rows the 7.x audit paths write, column by column. The audit projection builds
/// its rows with the same code, so these are the rows a projected event must reproduce.
/// </summary>
[TestClass]
public sealed class AuditRowShapeTests
{
    internal static readonly object AuthServerData = new
    {
        reason = "invalid_password",
        attempts = 3,
        nested = new { password = "hunter2", ok = true },
        list = new[] { 1, 2 },
        big = 12345678901234L,
        ratio = 1.5,
        empty = (string?)null,
        longText = new string('x', 3000)
    };

    internal static readonly string AuthServerMetadataJson =
        "{\"reason\":\"invalid_password\",\"attempts\":3,\"nested\":{\"password\":\"[redacted]\",\"ok\":true},"
        + "\"list\":[1,2],\"big\":12345678901234,\"ratio\":1.5,\"empty\":null,\"longText\":\"" + new string('x', 2048) + "\"}";

    internal static SqlOSAuditLogRecordRequest LogRequest => new(
        Action: "  report.exported  ",
        OrganizationId: " org_1 ",
        Source: "  ",
        Actor: new SqlOSAuditActor("  user ", " usr_9 ", "  Ada  "),
        Targets:
        [
            new SqlOSAuditTarget("report", "rpt_1", "Q3"),
            new SqlOSAuditTarget(" ", "ignored"),
            new SqlOSAuditTarget(" doc ", "doc_2")
        ],
        Context: new SqlOSAuditContext("198.51.100.7", "Mozilla/5.0", "ses_9", "req-1", "corr-1"),
        Metadata: new Dictionary<string, object?> { ["client_secret"] = "s3cr3t", ["Access"] = "x", ["count"] = 2 },
        OccurredAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [TestMethod]
    public async Task RecordAuditAsync_writes_the_authserver_row()
    {
        await using var context = CreateContext();
        var admin = new SqlOSAdminService(context, Options.Create(new SqlOSAuthServerOptions()), TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions())));

        await admin.RecordAuditAsync(
            "user.login.failed",
            "user",
            "usr_1",
            organizationId: "org_1",
            sessionId: "ses_1",
            ipAddress: "203.0.113.10",
            data: AuthServerData);

        var row = await context.Set<SqlOSAuditEvent>().AsNoTracking().SingleAsync();
        row.Id.Should().MatchRegex("^evt_[0-9a-f]{24}$");
        row.OrganizationId.Should().Be("org_1");
        row.ApplicationId.Should().BeNull();
        row.ApplicationKey.Should().BeNull();
        row.UserId.Should().Be("usr_1", "a user actor is the row's user when none is given");
        row.SessionId.Should().Be("ses_1");
        row.EventType.Should().Be("user.login.failed");
        row.Action.Should().Be("user.login.failed");
        row.Source.Should().Be("authserver");
        row.ActorType.Should().Be("user");
        row.ActorId.Should().Be("usr_1");
        row.ActorDisplayName.Should().BeNull();
        row.TargetsJson.Should().Be("[]");
        row.ContextJson.Should().Be("{\"ipAddress\":\"203.0.113.10\",\"userAgent\":null,\"sessionId\":\"ses_1\",\"requestId\":null,\"correlationId\":null}");
        row.MetadataJson.Should().Be(AuthServerMetadataJson);
        row.DataJson.Should().Be(AuthServerMetadataJson);
        row.OccurredAt.Should().Be(row.IngestedAt);
        row.IpAddress.Should().Be("203.0.113.10");
        row.UserAgent.Should().BeNull();
        row.RequestId.Should().BeNull();
        row.CorrelationId.Should().BeNull();
        row.IdempotencyKeyHash.Should().BeNull();
        row.IdempotencyScopeHash.Should().BeNull();
    }

    [TestMethod]
    public async Task RecordAuditAsync_without_data_writes_no_metadata()
    {
        await using var context = CreateContext();
        var admin = new SqlOSAdminService(context, Options.Create(new SqlOSAuthServerOptions()), TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions())));

        await admin.RecordAuditAsync("client.enabled", "client", "cli_1");

        var row = await context.Set<SqlOSAuditEvent>().AsNoTracking().SingleAsync();
        row.UserId.Should().BeNull();
        row.SessionId.Should().BeNull();
        row.OrganizationId.Should().BeNull();
        row.MetadataJson.Should().BeNull();
        row.DataJson.Should().BeNull();
        row.ContextJson.Should().Be("{\"ipAddress\":null,\"userAgent\":null,\"sessionId\":null,\"requestId\":null,\"correlationId\":null}");
        row.IpAddress.Should().BeNull();
    }

    [TestMethod]
    public async Task RecordAsync_normalizes_and_redacts_the_request()
    {
        await using var context = CreateContext();
        var service = new SqlOSAuditLogService(context, TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions())));

        await service.RecordAsync(LogRequest);

        var row = await context.Set<SqlOSAuditEvent>().AsNoTracking().SingleAsync();
        row.Action.Should().Be("report.exported");
        row.EventType.Should().Be("report.exported");
        row.Source.Should().Be("application", "a blank source is the application");
        row.OrganizationId.Should().Be("org_1");
        row.ActorType.Should().Be("user");
        row.ActorId.Should().Be("usr_9");
        row.ActorDisplayName.Should().Be("Ada");
        row.UserId.Should().Be("usr_9");
        row.SessionId.Should().Be("ses_9");
        row.TargetsJson.Should().Be("[{\"type\":\"report\",\"id\":\"rpt_1\",\"displayName\":\"Q3\"},{\"type\":\"doc\",\"id\":\"doc_2\",\"displayName\":null}]");
        row.ContextJson.Should().Be("{\"ipAddress\":\"198.51.100.7\",\"userAgent\":\"Mozilla/5.0\",\"sessionId\":\"ses_9\",\"requestId\":\"req-1\",\"correlationId\":\"corr-1\"}");
        row.MetadataJson.Should().Be("{\"client_secret\":\"[redacted]\",\"Access\":\"[redacted]\",\"count\":2}");
        row.DataJson.Should().Be(row.MetadataJson);
        row.OccurredAt.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        row.IngestedAt.Should().BeAfter(row.OccurredAt);
        row.IpAddress.Should().Be("198.51.100.7");
        row.UserAgent.Should().Be("Mozilla/5.0");
        row.RequestId.Should().Be("req-1");
        row.CorrelationId.Should().Be("corr-1");
        row.IdempotencyScopeHash.Should().BeNull();
    }

    internal static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
