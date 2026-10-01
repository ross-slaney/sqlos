using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.DomainEvents;

[TestClass]
public sealed class AuditProjectionTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SqlOSAuditProjectionContext Context = new(Now, SqlOSRequestContext.System);

    [TestMethod]
    public void Every_domain_event_in_sqlos_has_a_registered_projection()
    {
        // A new event must be registered as audited (with its 7.2.1 row) or as unaudited, so an
        // audit row can never silently disappear when a call site moves onto events.
        var eventTypes = typeof(ISqlOSDomainEvent).Assembly.GetTypes()
            .Where(type => typeof(ISqlOSDomainEvent).IsAssignableFrom(type) && type is { IsInterface: false, IsAbstract: false })
            .ToList();

        eventTypes.Where(type => !SqlOSAuditProjection.Default.Handles(type))
            .Select(type => type.FullName)
            .Should().BeEmpty();
        SqlOSAuditProjection.Default.EventTypes.Should().BeEquivalentTo(eventTypes, "only SqlOS events are registered");
    }

    [TestMethod]
    public void An_audited_event_projects_the_row_its_builder_returns()
    {
        var projection = new SqlOSAuditProjectionBuilder()
            .Audit<Deposited>((domainEvent, context) => SqlOSAuditRows.Create(
                SqlOSAuditRows.AuthServerRequest("ledger.deposited", "system", null, data: new { domainEvent.Amount }),
                "evt_fixed",
                context.Now))
            .Build();

        var row = projection.Project(new Deposited("a", 3), Context);

        row!.Id.Should().Be("evt_fixed");
        row.EventType.Should().Be("ledger.deposited");
        row.MetadataJson.Should().Be("{\"Amount\":3}", "metadata keeps the member names as declared, as RecordAuditAsync does");
        row.IngestedAt.Should().Be(Now);
        projection.Handles(typeof(Deposited)).Should().BeTrue();
    }

    [TestMethod]
    public void An_unaudited_event_projects_no_row()
    {
        var projection = new SqlOSAuditProjectionBuilder().Unaudited<Silent>().Build();

        projection.Project(new Silent("a"), Context).Should().BeNull();
        projection.Handles(typeof(Silent)).Should().BeTrue();
        projection.EventTypes.Should().Equal(typeof(Silent));
    }

    [TestMethod]
    public void An_unregistered_event_is_an_error()
    {
        var projection = new SqlOSAuditProjectionBuilder().Unaudited<Silent>().Build();

        FluentActions.Invoking(() => projection.Project(new Unregistered("a"), Context))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("No audit projection is registered for domain event 'SqlOS.Tests.DomainEvents.Unregistered'*");
        projection.Handles(typeof(Unregistered)).Should().BeFalse();
    }

    [TestMethod]
    public void An_event_is_registered_once()
    {
        var builder = new SqlOSAuditProjectionBuilder().Unaudited<Silent>();

        FluentActions.Invoking(() => builder.Unaudited<Silent>()).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => builder.Audit<Silent>((_, _) => new SqlOSAuditEvent())).Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void An_audit_builder_must_return_a_row()
    {
        var projection = new SqlOSAuditProjectionBuilder().Audit<Deposited>((_, _) => null!).Build();

        FluentActions.Invoking(() => projection.Project(new Deposited("a", 1), Context))
            .Should().Throw<InvalidOperationException>().WithMessage("*returned no row*");
    }

    [TestMethod]
    public void A_built_projection_does_not_change_when_its_builder_does()
    {
        var builder = new SqlOSAuditProjectionBuilder().Unaudited<Silent>();
        var projection = builder.Build();

        builder.Unaudited<Deposited>();

        projection.Handles(typeof(Deposited)).Should().BeFalse();
    }

    [TestMethod]
    public async Task A_projected_authserver_row_is_the_row_RecordAuditAsync_writes()
    {
        await using var context = AuditRowShapeTests.CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        await new SqlOSAdminService(context, options, TestCryptoService.Create(context, options)).RecordAuditAsync(
            "user.login.failed",
            "user",
            "usr_1",
            organizationId: "org_1",
            sessionId: "ses_1",
            ipAddress: "203.0.113.10",
            data: AuditRowShapeTests.AuthServerData);
        var written = await context.Set<SqlOSAuditEvent>().AsNoTracking().SingleAsync();

        var projected = SqlOSAuditRows.Create(
            SqlOSAuditRows.AuthServerRequest(
                "user.login.failed",
                "user",
                "usr_1",
                organizationId: "org_1",
                sessionId: "ses_1",
                ipAddress: "203.0.113.10",
                data: AuditRowShapeTests.AuthServerData),
            SqlOSIds.New("evt"),
            Now);

        projected.Should().BeEquivalentTo(written, options => options
            .Excluding(row => row.Id)
            .Excluding(row => row.OccurredAt)
            .Excluding(row => row.IngestedAt));
        projected.MetadataJson.Should().Be(AuditRowShapeTests.AuthServerMetadataJson);
        projected.OccurredAt.Should().Be(Now);
        projected.IngestedAt.Should().Be(Now);
    }

    [TestMethod]
    public async Task A_projected_row_is_the_row_RecordAsync_writes_for_the_same_request()
    {
        await using var context = AuditRowShapeTests.CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        await new SqlOSAuditLogService(context, TestCryptoService.Create(context, options)).RecordAsync(AuditRowShapeTests.LogRequest);
        var written = await context.Set<SqlOSAuditEvent>().AsNoTracking().SingleAsync();

        var projected = SqlOSAuditRows.Create(AuditRowShapeTests.LogRequest, SqlOSIds.New("evt"), Now);

        projected.Should().BeEquivalentTo(written, options => options
            .Excluding(row => row.Id)
            .Excluding(row => row.IngestedAt));
        projected.OccurredAt.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "a request's own time is kept");
    }

    [TestMethod]
    public void A_row_needs_an_action_and_an_id()
    {
        FluentActions.Invoking(() => SqlOSAuditRows.Create(new SqlOSAuditLogRecordRequest("  "), "evt_1", Now))
            .Should().Throw<ArgumentException>().WithMessage("Action is required.*");
        FluentActions.Invoking(() => SqlOSAuditRows.Create(new SqlOSAuditLogRecordRequest("a.b"), " ", Now))
            .Should().Throw<ArgumentException>();
    }
}
