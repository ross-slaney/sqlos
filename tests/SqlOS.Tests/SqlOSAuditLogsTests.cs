using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuditLogsTests
{
    [TestMethod]
    public async Task AuditLogs_RecordAsync_PersistsStructuredEvent()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        var result = await service.RecordAsync(new SqlOSAuditLogRecordRequest(
            Action: "document.shared",
            OrganizationId: "org_1",
            ApplicationKey: "workspace-web",
            Source: "application",
            Actor: new SqlOSAuditActor("user", "usr_1", "Jane Doe"),
            Targets: [new SqlOSAuditTarget("document", "doc_1", "Contract.pdf")],
            Context: new SqlOSAuditContext("203.0.113.10", "Unit Test", "ses_1", "req_1", "corr_1"),
            Metadata: new Dictionary<string, object?> { ["result"] = "success", ["role"] = "viewer" }));

        result.Created.Should().BeTrue();
        var entity = await context.Set<SqlOSAuditEvent>().SingleAsync();
        entity.Action.Should().Be("document.shared");
        entity.EventType.Should().Be("document.shared");
        entity.OrganizationId.Should().Be("org_1");
        entity.ApplicationKey.Should().Be("workspace-web");
        entity.Source.Should().Be("application");
        entity.ActorDisplayName.Should().Be("Jane Doe");
        entity.TargetsJson.Should().Contain("Contract.pdf");
        entity.ContextJson.Should().Contain("req_1");
        entity.MetadataJson.Should().Contain("viewer");
        entity.IngestedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [TestMethod]
    public async Task AuditLogs_RecordAsync_WithApplicationKey_CanFilterByApplication()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await service.RecordAsync(CreateRecord("retail.inventory_item.created", applicationKey: "northwind-retail"));
        await service.RecordAsync(CreateRecord("todo.created", applicationKey: "todo-web"));

        var result = await service.ListAsync(new SqlOSAuditLogListRequest(ApplicationKey: "northwind-retail"));

        result.TotalCount.Should().Be(1);
        result.Data[0].ApplicationKey.Should().Be("northwind-retail");
    }

    [TestMethod]
    public async Task AuditLogs_RecordAsync_WithClientApplication_CanFilterByClient()
    {
        using var context = CreateContext();
        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = "cli_retail",
            ClientId = "retail-web",
            Name = "Retail Web",
            Audience = "sqlos",
            RedirectUrisJson = "[]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        await service.RecordAsync(CreateRecord("retail.inventory_item.updated", applicationKey: "retail-web"));

        var byId = await service.ListAsync(new SqlOSAuditLogListRequest(ApplicationId: "cli_retail"));
        var byEither = await service.ListAsync(new SqlOSAuditLogListRequest(Application: "retail-web"));

        byId.TotalCount.Should().Be(1);
        byId.Data[0].ApplicationId.Should().Be("cli_retail");
        byEither.TotalCount.Should().Be(1);
    }

    [TestMethod]
    public async Task AuditLogs_RecordAsync_WithIdempotencyKey_DoesNotDuplicateEvent()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        var first = await service.RecordAsync(CreateRecord("document.shared", idempotencyKey: "doc_1:share:usr_1"));
        var second = await service.RecordAsync(CreateRecord("document.shared", idempotencyKey: "doc_1:share:usr_1"));

        first.Created.Should().BeTrue();
        second.Created.Should().BeFalse();
        second.EventId.Should().Be(first.EventId);
        (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task AuditLogs_RecordAsync_RedactsDisallowedMetadataFields()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await service.RecordAsync(new SqlOSAuditLogRecordRequest(
            Action: "secret.test",
            Metadata: new Dictionary<string, object?>
            {
                ["result"] = "failed",
                ["password"] = "submitted-password",
                ["accessToken"] = "token-value",
                ["nested"] = new Dictionary<string, object?> { ["clientSecret"] = "secret-value" }
            }));

        var metadataJson = (await context.Set<SqlOSAuditEvent>().SingleAsync()).MetadataJson;
        metadataJson.Should().Contain("[redacted]");
        metadataJson.Should().Contain("failed");
        metadataJson.Should().NotContain("submitted-password");
        metadataJson.Should().NotContain("token-value");
        metadataJson.Should().NotContain("secret-value");
    }

    [TestMethod]
    public async Task AuditLogs_List_FiltersByOrganizationSourceActionActorTargetAndDateRange()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var now = DateTime.UtcNow;

        await service.RecordAsync(CreateRecord(
            "retail.inventory_item.updated",
            organizationId: "org_retail",
            source: "application",
            actorType: "user",
            actorId: "usr_1",
            targetType: "inventory_item",
            targetId: "inv_1",
            occurredAt: now));
        await service.RecordAsync(CreateRecord(
            "retail.inventory_item.updated",
            organizationId: "org_other",
            source: "application",
            actorType: "user",
            actorId: "usr_2",
            targetType: "inventory_item",
            targetId: "inv_2",
            occurredAt: now));

        var result = await service.ListAsync(new SqlOSAuditLogListRequest(
            OrganizationId: "org_retail",
            Source: "application",
            Action: "retail.inventory_item.updated",
            ActorType: "user",
            ActorId: "usr_1",
            TargetType: "inventory_item",
            TargetId: "inv_1",
            OccurredAtFrom: now.AddMinutes(-1),
            OccurredAtTo: now.AddMinutes(1)));

        result.TotalCount.Should().Be(1);
        result.Data[0].OrganizationId.Should().Be("org_retail");
        result.Data[0].Targets.Should().ContainSingle(x => x.Id == "inv_1");
    }

    [TestMethod]
    public async Task AuditLogs_List_DefaultSortsByOccurredAtDescending()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var now = DateTime.UtcNow;

        await service.RecordAsync(CreateRecord("old", occurredAt: now.AddMinutes(-10)));
        await service.RecordAsync(CreateRecord("new", occurredAt: now));
        await service.RecordAsync(CreateRecord("middle", occurredAt: now.AddMinutes(-5)));

        var result = await service.ListAsync(new SqlOSAuditLogListRequest(PageSize: 10));

        result.Data.Select(x => x.Action).Should().ContainInOrder("new", "middle", "old");
    }

    [TestMethod]
    public async Task AuditLogs_List_PaginatesResults()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var now = DateTime.UtcNow;

        await service.RecordAsync(CreateRecord("event.1", occurredAt: now));
        await service.RecordAsync(CreateRecord("event.2", occurredAt: now.AddMinutes(-1)));
        await service.RecordAsync(CreateRecord("event.3", occurredAt: now.AddMinutes(-2)));

        var result = await service.ListAsync(new SqlOSAuditLogListRequest(Page: 2, PageSize: 2));

        result.TotalCount.Should().Be(3);
        result.TotalPages.Should().Be(2);
        result.Data.Should().ContainSingle(x => x.Action == "event.3");
    }

    [TestMethod]
    public async Task AuditLogs_ExportCsv_RespectsOrganizationDateRangeAndFilters()
    {
        using var context = CreateContext();
        var service = CreateService(context);
        var now = DateTime.UtcNow;

        await service.RecordAsync(CreateRecord(
            "retail.inventory_item.updated",
            organizationId: "org_retail",
            applicationKey: "northwind-retail",
            source: "application",
            occurredAt: now));
        await service.RecordAsync(CreateRecord(
            "retail.inventory_item.deleted",
            organizationId: "org_other",
            applicationKey: "northwind-retail",
            source: "application",
            occurredAt: now));

        var export = await service.ExportCsvAsync(new SqlOSAuditLogListRequest(
            OrganizationId: "org_retail",
            ApplicationKey: "northwind-retail",
            Source: "application",
            OccurredAtFrom: now.AddMinutes(-1),
            OccurredAtTo: now.AddMinutes(1)));

        export.Content.Should().Contain("id,occurred_at,ingested_at");
        export.Content.Should().Contain("retail.inventory_item.updated");
        export.Content.Should().NotContain("retail.inventory_item.deleted");
    }

    [TestMethod]
    public async Task AuditLogs_AdminApi_RequiresDashboardAuthorization()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddSingleton<SqlOSDashboardSessionService>();
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var options = new SqlOSAuthServerOptions();
        options.Dashboard.AuthMode = SqlOSDashboardAuthMode.Password;
        options.Dashboard.Password = "secret";

        var method = typeof(SqlOSAuditLogEndpointRouteBuilderExtensions).GetMethod(
            "IsAdminAuthorizedAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var task = (Task<bool>)method!.Invoke(null, [context, options, new TestHostEnvironment()])!;
        var authorized = await task;

        authorized.Should().BeFalse();
    }

    [TestMethod]
    public async Task AuditLogs_Compatibility_ExistingAuthRecordAuditWritesCentralEvent()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        await admin.RecordAuditAsync(
            "client.disabled",
            "client",
            "cli_1",
            userId: "usr_1",
            organizationId: "org_1",
            sessionId: "ses_1",
            ipAddress: "203.0.113.20",
            data: new { reason = "manual_review" });

        var entity = await context.Set<SqlOSAuditEvent>().SingleAsync();
        entity.EventType.Should().Be("client.disabled");
        entity.Action.Should().Be("client.disabled");
        entity.Source.Should().Be("authserver");
        entity.UserId.Should().Be("usr_1");
        entity.OrganizationId.Should().Be("org_1");
        entity.SessionId.Should().Be("ses_1");
        entity.IpAddress.Should().Be("203.0.113.20");
        entity.MetadataJson.Should().Contain("manual_review");
    }

    [TestMethod]
    public void AuditLogs_Dashboard_RendersApplicationFilterAndEventDetail()
    {
        var assembly = typeof(SqlOSDashboardMiddleware).Assembly;
        var appJs = ReadEmbeddedResource(assembly, "SqlOS.Dashboard.wwwroot.app.js");
        var indexHtml = ReadEmbeddedResource(assembly, "SqlOS.Dashboard.wwwroot.index.html");

        indexHtml.Should().Contain("data-route=\"audit-logs\"");
        appJs.Should().Contain("Application key or client ID");
        appJs.Should().Contain("Event Detail");
        appJs.Should().Contain("events/export.csv");
        appJs.Should().Contain("audit-filter-form");
    }

    [TestMethod]
    public void AddSqlOS_RegistersCentralAuditLogService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestSqlOSInMemoryDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));

        services.AddSqlOS<TestSqlOSInMemoryDbContext>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISqlOSAuditLogService>()
            .Should()
            .BeOfType<SqlOSAuditLogService>();
    }

    private static SqlOSAuditLogRecordRequest CreateRecord(
        string action,
        string organizationId = "org_1",
        string? applicationKey = null,
        string source = "application",
        string actorType = "user",
        string actorId = "usr_1",
        string targetType = "document",
        string targetId = "doc_1",
        string? idempotencyKey = null,
        DateTime? occurredAt = null)
        => new(
            Action: action,
            OrganizationId: organizationId,
            ApplicationKey: applicationKey,
            Source: source,
            Actor: new SqlOSAuditActor(actorType, actorId, "Test Actor"),
            Targets: [new SqlOSAuditTarget(targetType, targetId, targetId)],
            Metadata: new Dictionary<string, object?> { ["result"] = "success" },
            IdempotencyKey: idempotencyKey,
            OccurredAt: occurredAt);

    private static SqlOSAuditLogService CreateService(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        return new SqlOSAuditLogService(context, crypto);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static string ReadEmbeddedResource(Assembly assembly, string suffix)
    {
        var name = assembly.GetManifestResourceNames()
            .Single(x => x.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "SqlOS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
