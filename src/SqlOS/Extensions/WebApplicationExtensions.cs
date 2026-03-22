using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.Configuration;

namespace SqlOS.Extensions;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps SqlOS auth server endpoints. Call once after <see cref="WebApplicationBuilder.Build"/> when using <see cref="WebApplicationBuilderExtensions.AddSqlOS{TContext}"/>.
    /// </summary>
    public static WebApplication MapSqlOS(this WebApplication app)
    {
        var authOptions = app.Services.GetRequiredService<IOptions<SqlOSAuthServerOptions>>().Value;
        app.MapAuthServer(authOptions.BasePath);

        return app;
    }
}
