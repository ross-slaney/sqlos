using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Configuration;

namespace SqlOS.Mcp;

/// <summary>
/// Declares and hosts the MCP surface of a SqlOS host.
/// </summary>
public static class SqlOSMcpSingleApplicationExtensions
{
    /// <summary>
    /// Declares <paramref name="path"/> as the application's MCP surface and hosts a Model Context
    /// Protocol server there.
    /// </summary>
    /// <remarks>
    /// Setting the surface makes SqlOS validate bearer tokens for the audience <c>{Origin}{path}</c>
    /// under the prefix, serve the RFC 9728 protected-resource document, and enable client ID
    /// metadata documents plus resource indicators for portable MCP clients. This package then
    /// registers <c>AddMcpServer().WithHttpTransport(stateless)</c>, applies
    /// <paramref name="configure"/> to the SDK builder as-is (for example
    /// <c>mcp.WithTools&lt;MyTools&gt;()</c>), audits every tool call, and maps the server on the
    /// protected branch during startup. Application code needs no <c>AddMcpServer</c> or
    /// <c>MapMcp</c>.
    /// Tools can inject <see cref="ISqlOSMcpUserContext"/> to act as the connecting user.
    /// </remarks>
    /// <param name="app">The application description.</param>
    /// <param name="path">The absolute MCP path prefix under the application origin, for example <c>/mcp</c>.</param>
    /// <param name="configure">Configures the MCP SDK server builder.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static TApplication Mcp<TApplication>(
        this TApplication app,
        string path,
        Action<IMcpServerBuilder> configure)
        where TApplication : SqlOSApplicationOptions
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configure);

        if (app.HostExtensions.Any(extension => extension is SqlOSMcpHostExtension))
        {
            throw new InvalidOperationException(
                "app.Mcp(path, configure) was called more than once. An application host has one MCP surface.");
        }

        app.Mcp = path;
        app.HostExtensions.Add(new SqlOSMcpHostExtension(configure));
        return app;
    }
}
