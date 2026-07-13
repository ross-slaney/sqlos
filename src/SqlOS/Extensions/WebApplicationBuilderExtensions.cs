using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Extensions;

/// <summary>
/// Provides host-builder extensions for registering SqlOS in an ASP.NET Core application.
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Registers SqlOS for an application <typeparamref name="TContext"/> that is already
    /// registered with dependency injection.
    /// </summary>
    /// <typeparam name="TContext">
    /// The application EF Core context that contains the SqlOS auth-server and FGA models.
    /// </typeparam>
    /// <param name="builder">The application host builder.</param>
    /// <param name="configure">An optional callback that configures SqlOS.</param>
    /// <returns>The same <paramref name="builder"/> instance so that additional calls can be chained.</returns>
    /// <remarks>
    /// This overload does not register <typeparamref name="TContext"/>. Register the context before
    /// calling this method. After <see cref="WebApplicationBuilder.Build"/>, call
    /// <see cref="WebApplicationExtensions.MapSqlOS"/> once to map the SqlOS endpoints.
    /// </remarks>
    public static WebApplicationBuilder AddSqlOS<TContext>(this WebApplicationBuilder builder, Action<SqlOSOptions>? configure = null)
        where TContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    {
        builder.Services.AddSqlOS<TContext>(configure);
        return builder;
    }

    /// <summary>
    /// Registers the application EF Core context and the complete SqlOS service graph in one host call.
    /// </summary>
    /// <typeparam name="TContext">
    /// The application EF Core context that contains the SqlOS auth-server and FGA models.
    /// </typeparam>
    /// <param name="builder">The application host builder.</param>
    /// <param name="configureDbContext">A callback that configures the application EF Core context.</param>
    /// <param name="configureSqlOS">An optional callback that configures SqlOS.</param>
    /// <returns>The same <paramref name="builder"/> instance so that additional calls can be chained.</returns>
    /// <remarks>
    /// This overload calls <see cref="EntityFrameworkServiceCollectionExtensions.AddDbContext{TContext}(IServiceCollection,Action{DbContextOptionsBuilder},ServiceLifetime,ServiceLifetime)"/>
    /// before registering SqlOS. After <see cref="WebApplicationBuilder.Build"/>, call
    /// <see cref="WebApplicationExtensions.MapSqlOS"/> once to map the SqlOS endpoints.
    /// </remarks>
    public static WebApplicationBuilder AddSqlOS<TContext>(
        this WebApplicationBuilder builder,
        Action<DbContextOptionsBuilder> configureDbContext,
        Action<SqlOSOptions>? configureSqlOS = null)
        where TContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    {
        builder.Services.AddDbContext<TContext>(configureDbContext);
        builder.Services.AddSqlOS<TContext>(configureSqlOS);
        return builder;
    }
}
