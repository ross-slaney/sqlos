using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;
using SqlOS.Hosting;

namespace SqlOS.Mcp;

/// <summary>
/// Registers the MCP SDK server and maps it on the declared MCP surface. SqlOS core attaches
/// token validation to those mapped endpoints and serves the RFC 9728 document; this extension
/// only adds the server itself, so application code contains no <c>AddMcpServer</c> or <c>MapMcp</c>.
/// </summary>
internal sealed class SqlOSMcpHostExtension : ISqlOSHostExtension
{
    private readonly Action<IMcpServerBuilder> _configure;

    public SqlOSMcpHostExtension(Action<IMcpServerBuilder> configure)
    {
        _configure = configure;
    }

    public void ConfigureServices(IServiceCollection services, SqlOSOptions options)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ISqlOSMcpUserContext, SqlOSMcpUserContext>();

        var builder = services
            .AddMcpServer()
            .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless);

        // The developer's configuration runs against the SDK builder unchanged.
        _configure(builder);

        builder.WithRequestFilters(filters => filters.AddCallToolFilter(SqlOSMcpToolCallAudit.Wrap));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, SqlOSOptions options)
    {
        var path = SqlOSSingleApplicationSurfaces.NormalizePath(options.AuthServer.Application?.Mcp)
            ?? throw new InvalidOperationException(
                "SqlOS.Mcp requires an MCP surface. Call app.Mcp(\"/mcp\", ...) inside UseSingleApplication.");

        endpoints.MapMcp(path);
    }
}
