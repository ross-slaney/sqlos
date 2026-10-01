using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuditLogs;
using SqlOS.Domain;

namespace SqlOS.Hosting;

/// <summary>
/// Captures the <see cref="SqlOSRequestContext"/> of an HTTP request for the adapters that serve it.
/// </summary>
internal static class SqlOSHttpRequestContext
{
    /// <summary>
    /// The request context exactly as 7.x audit code derives it
    /// (<see cref="SqlOSAuditContext.FromHttpContext"/>): the connection's remote address, the
    /// <c>User-Agent</c> header, <c>X-Request-ID</c> (or the trace identifier) and
    /// <c>X-Correlation-ID</c>.
    /// </summary>
    public static SqlOSRequestContext From(HttpContext httpContext, SqlOSRequestSurface surface)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var audit = SqlOSAuditContext.FromHttpContext(httpContext);
        return new SqlOSRequestContext(surface, audit.IpAddress, audit.UserAgent, audit.RequestId, audit.CorrelationId);
    }

    /// <summary>Captures the request context and sets it on the request scope for the work that follows.</summary>
    public static SqlOSRequestContext Enter(HttpContext httpContext, SqlOSRequestSurface surface)
    {
        var requestContext = From(httpContext, surface);
        httpContext.RequestServices.GetRequiredService<SqlOSRequestContextAccessor>().Current = requestContext;
        return requestContext;
    }
}
