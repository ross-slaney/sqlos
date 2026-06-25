using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Extensions;

public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Registers SqlOS (auth server + FGA), runs bootstrap on startup, and wires dashboard middleware.
    /// After <see cref="WebApplicationBuilder.Build"/>, call <see cref="WebApplicationExtensions.MapSqlOS"/> once to map OAuth and admin API routes.
    /// </summary>
    public static WebApplicationBuilder AddSqlOS<TContext>(this WebApplicationBuilder builder, Action<SqlOSOptions>? configure = null)
        where TContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    {
        builder.Services.AddSqlOS<TContext>(configure);
        return builder;
    }

    /// <summary>
    /// Registers the application's EF Core DbContext and SqlOS (auth server + FGA) in one host call.
    /// After <see cref="WebApplicationBuilder.Build"/>, call <see cref="WebApplicationExtensions.MapSqlOS"/> once to map OAuth and admin API routes.
    /// </summary>
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
