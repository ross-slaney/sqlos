using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.Configuration;

namespace SqlOS.Hosting;

/// <summary>
/// Extension point for in-process surfaces that register services during <c>AddSqlOS</c>
/// and map endpoints from the SqlOS startup filter.
/// </summary>
/// <remarks>
/// <c>AddSqlOS</c> calls <see cref="ConfigureServices"/> after the options callback has run, and
/// the SqlOS startup filter calls <see cref="MapEndpoints"/> inside the routing pass it owns.
/// MCP hosting does not use this: set <c>app.Mcp</c> and call <c>AddMcpServer</c> / <c>MapMcp</c>
/// in the host.
/// </remarks>
public interface ISqlOSHostExtension
{
    /// <summary>Registers the services the extension needs.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="options">The fully configured SqlOS options.</param>
    void ConfigureServices(IServiceCollection services, SqlOSOptions options);

    /// <summary>Maps the extension's endpoints. Requests under a declared surface have already been validated.</summary>
    /// <param name="endpoints">The SqlOS-owned endpoint route builder.</param>
    /// <param name="options">The fully configured SqlOS options.</param>
    void MapEndpoints(IEndpointRouteBuilder endpoints, SqlOSOptions options);
}
