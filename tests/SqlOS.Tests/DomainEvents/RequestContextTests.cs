using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.Domain;
using SqlOS.Hosting;

namespace SqlOS.Tests.DomainEvents;

[TestClass]
public sealed class RequestContextTests
{
    [TestMethod]
    public void A_request_context_is_captured_the_way_7x_audit_code_derives_it()
    {
        var httpContext = CreateHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        httpContext.Request.Headers.UserAgent = "Mozilla/5.0";
        httpContext.Request.Headers["X-Request-ID"] = "req-1";
        httpContext.Request.Headers["X-Correlation-ID"] = "corr-1";

        var requestContext = SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Hosted);

        requestContext.Should().Be(new SqlOSRequestContext(SqlOSRequestSurface.Hosted, "203.0.113.10", "Mozilla/5.0", "req-1", "corr-1"));
        var audit = SqlOSAuditContext.FromHttpContext(httpContext);
        requestContext.IpAddress.Should().Be(audit.IpAddress);
        requestContext.UserAgent.Should().Be(audit.UserAgent);
        requestContext.RequestId.Should().Be(audit.RequestId);
        requestContext.CorrelationId.Should().Be(audit.CorrelationId);
    }

    [TestMethod]
    public void Missing_values_fall_back_exactly_as_in_7x()
    {
        var httpContext = CreateHttpContext();
        httpContext.TraceIdentifier = "trace-1";

        var requestContext = SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless);

        requestContext.IpAddress.Should().BeNull();
        requestContext.UserAgent.Should().BeEmpty("7.x reads the header as an empty string; an audit row stores it as no user agent");
        requestContext.RequestId.Should().Be("trace-1");
        requestContext.CorrelationId.Should().BeNull();
    }

    [TestMethod]
    public void The_alternate_header_spellings_are_read()
    {
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers["X-Request-Id"] = "req-2";
        httpContext.Request.Headers["X-Correlation-Id"] = "corr-2";

        var requestContext = SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.PublicApi);

        requestContext.RequestId.Should().Be("req-2");
        requestContext.CorrelationId.Should().Be("corr-2");
    }

    [TestMethod]
    public void Entering_sets_the_request_scope_accessor()
    {
        var httpContext = CreateHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");

        var entered = SqlOSHttpRequestContext.Enter(httpContext, SqlOSRequestSurface.Protocol);

        httpContext.RequestServices.GetRequiredService<SqlOSRequestContextAccessor>().Current.Should().BeSameAs(entered);
        entered.Surface.Should().Be(SqlOSRequestSurface.Protocol);
        entered.IpAddress.Should().Be("198.51.100.7");
    }

    [TestMethod]
    public void The_accessor_starts_as_the_system_context_and_rejects_null()
    {
        var accessor = new SqlOSRequestContextAccessor();

        accessor.Current.Should().BeSameAs(SqlOSRequestContext.System);
        SqlOSRequestContext.System.Surface.Should().Be(SqlOSRequestSurface.System);
        FluentActions.Invoking(() => accessor.Current = null!).Should().Throw<ArgumentNullException>();
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection().AddScoped<SqlOSRequestContextAccessor>().BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services.CreateScope().ServiceProvider };
    }
}
