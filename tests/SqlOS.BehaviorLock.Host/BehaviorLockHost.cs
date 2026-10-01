using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlOS.BehaviorLock.Host.Diagnostics;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Host.Probes;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Hosting;
using SqlOS.Services;

namespace SqlOS.BehaviorLock.Host;

public enum DatabaseProvider
{
    SqlServer,
    PostgreSql
}

public sealed class BehaviorLockHostOptions
{
    /// <summary>The <see cref="HostProfile.Name"/> to host.</summary>
    public required string Profile { get; init; }

    public required DatabaseProvider Provider { get; init; }

    public required string ConnectionString { get; init; }

    /// <summary>Serve in-memory with <see cref="TestServer"/> (the harness) instead of Kestrel.</summary>
    public bool UseTestServer { get; init; } = true;

    /// <summary>
    /// Run SqlOS bootstrap (schema, keys, seeds) at startup. The route-inventory gate turns it off
    /// so it can enumerate endpoints without a database.
    /// </summary>
    public bool RunBootstrap { get; init; } = true;

    /// <summary>
    /// Persist the data-protection key ring here so another process (the upgrade seed) and this
    /// host can read each other's protected data. Null uses an ephemeral per-host key ring.
    /// </summary>
    public string? DataProtectionKeysDirectory { get; init; }

    /// <summary>Scenario-specific SqlOS configuration, applied after the profile.</summary>
    public Action<SqlOSOptions>? ConfigureSqlOS { get; init; }

    /// <summary>Scenario-specific service overrides, applied last.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// Answer an exception that escapes the application as Kestrel does (an empty <c>500</c>), naming
    /// it in the <see cref="BehaviorLockHost.UnhandledExceptionHeader"/> response header, instead of
    /// letting TestServer rethrow it into the caller. See <see cref="Diagnostics.UnhandledExceptionStartupFilter"/>.
    /// </summary>
    public bool AnswerUnhandledExceptionsAsServerErrors { get; init; }

    public string[] Args { get; init; } = [];

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Warning;
}

/// <summary>
/// Builds the behavior-lock host: a minimal ASP.NET application that hosts SqlOS exactly as a
/// documented deployment would (one <see cref="HostProfile"/> per deployment model), with every
/// outbound seam replaced by <see cref="BehaviorLockFakes"/> and the <c>/__probe</c> library surface.
/// </summary>
public static class BehaviorLockHost
{
    /// <summary>Default client address when the server did not supply one (TestServer does not).</summary>
    public static readonly IPAddress DefaultClientAddress = IPAddress.Parse("203.0.113.10");

    /// <summary>Lets a scenario present a different client address, for per-IP throttling cases.</summary>
    public const string ClientAddressHeader = "X-BehaviorLock-Client-IP";

    /// <summary>
    /// Harness-only response header that names an exception the application did not handle, when
    /// <see cref="BehaviorLockHostOptions.AnswerUnhandledExceptionsAsServerErrors"/> is on.
    /// </summary>
    public const string UnhandledExceptionHeader = "X-BehaviorLock-Unhandled-Exception";

    /// <summary>Schema for the host's own tables, kept apart from SqlOS's so the schema gate ignores them.</summary>
    public const string ApplicationSchema = "behaviorlock";

    public static WebApplication Build(BehaviorLockHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profile = HostProfiles.Get(options.Profile);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = options.Args,
            EnvironmentName = profile.Environment,
            ApplicationName = typeof(BehaviorLockHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });
        if (options.UseTestServer)
        {
            builder.WebHost.UseTestServer();
        }

        builder.Logging.SetMinimumLevel(options.MinimumLogLevel);

        var httpContextAccessor = new HttpContextAccessor();
        builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
        var fakes = new BehaviorLockFakes(httpContextAccessor);
        var recorder = new RouteHitRecorder();
        builder.Services.AddSingleton(recorder);
        // Registered before AddSqlOS so it is the outermost startup filter and sees every request.
        builder.Services.AddSingleton<IStartupFilter>(new RouteHitRecorderStartupFilter(recorder));
        builder.Services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>();

        WebApplication? app = null;
        Func<HttpMessageHandler>? backchannel = options.UseTestServer
            ? () => (app ?? throw new InvalidOperationException("The host has not been built.")).GetTestServer().CreateHandler()
            : null;
        var context = new HostProfileContext(profile, fakes, backchannel);

        void ConfigureSqlOS(SqlOSOptions sqlos)
        {
            profile.ConfigureSqlOS(sqlos, context);
            ApplyOperatorAccess(profile, sqlos);
            // Scenarios trigger sync explicitly; a timer-driven pass would make effects nondeterministic.
            sqlos.Calendar.SyncScheduler.Enabled = false;
            options.ConfigureSqlOS?.Invoke(sqlos);
        }

        switch (profile.DbContextStyle)
        {
            case DbContextStyle.SqlOSDbContext:
                RegisterSqlOS<BehaviorLockDbContext>(builder, profile, options, ConfigureSqlOS);
                break;
            case DbContextStyle.ManualInterfaces:
                RegisterSqlOS<ManualBehaviorLockDbContext>(builder, profile, options, ConfigureSqlOS);
                break;
            default:
                throw new InvalidOperationException($"Unsupported DbContext style {profile.DbContextStyle}.");
        }

        fakes.Register(builder.Services);
        ConfigureDataProtection(builder.Services, options);
        ConfigureBackgroundWork(builder.Services, profile, options);

        profile.ConfigureHost?.Invoke(builder, context);
        options.ConfigureServices?.Invoke(builder.Services);
        if (options.AnswerUnhandledExceptionsAsServerErrors)
        {
            // Registered last, so it is the innermost startup filter (see the filter's remarks).
            builder.Services.AddSingleton<IStartupFilter, UnhandledExceptionStartupFilter>();
        }

        app = builder.Build();
        profile.MapApplication?.Invoke(app, context);
        ProbeEndpoints.Map(app);
        return app;
    }

    private static void RegisterSqlOS<TContext>(
        WebApplicationBuilder builder,
        HostProfile profile,
        BehaviorLockHostOptions options,
        Action<SqlOSOptions> configure)
        where TContext : DbContext, SqlOS.AuthServer.Interfaces.ISqlOSAuthServerDbContext, SqlOS.Fga.Interfaces.ISqlOSFgaDbContext
    {
        if (profile.OneCallRegistration)
        {
            builder.AddSqlOS<TContext>(db => UseProvider(db, options), configure);
            return;
        }

        builder.Services.AddDbContext<TContext>(db => UseProvider(db, options));
        builder.AddSqlOS<TContext>(configure);
    }

    private static void UseProvider(DbContextOptionsBuilder db, BehaviorLockHostOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProvider.SqlServer:
                db.UseSqlServer(options.ConnectionString);
                break;
            case DatabaseProvider.PostgreSql:
                db.UseNpgsql(options.ConnectionString);
                break;
            default:
                throw new InvalidOperationException($"Unsupported provider {options.Provider}.");
        }
    }

    private static void ApplyOperatorAccess(HostProfile profile, SqlOSOptions sqlos)
    {
        if (profile.OperatorAccess != OperatorAccess.AuthorizationCallback)
        {
            return;
        }

        sqlos.Dashboard.AuthorizationCallback = http => Task.FromResult(string.Equals(
            http.Request.Headers[BehaviorLockConstants.OperatorHeader].ToString(),
            BehaviorLockConstants.OperatorSecret,
            StringComparison.Ordinal));
    }

    private static void ConfigureDataProtection(IServiceCollection services, BehaviorLockHostOptions options)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("sqlos-behavior-lock");
        if (options.DataProtectionKeysDirectory is { } keys)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }
        else
        {
            dataProtection.UseEphemeralDataProtectionProvider();
        }
    }

    private static void ConfigureBackgroundWork(IServiceCollection services, HostProfile profile, BehaviorLockHostOptions options)
    {
        // The hourly signing-key rotation check would fire nondeterministically during long runs;
        // scenarios rotate keys through the admin API instead.
        RemoveHostedService<SqlOSSigningKeyRotationService>(services);
        if (!options.RunBootstrap)
        {
            RemoveHostedService<SqlOSBootstrapHostedService>(services);
            return;
        }

        // Runs after SqlOS bootstrap (hosted services start in registration order).
        services.AddHostedService(provider => new ApplicationSchemaHostedService(provider, options.Provider, profile));
    }

    private static void RemoveHostedService<TService>(IServiceCollection services)
    {
        foreach (var descriptor in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                          && descriptor.ImplementationType == typeof(TService))
                     .ToList())
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>Creates the host application's own table after SqlOS has bootstrapped its schema.</summary>
    private sealed class ApplicationSchemaHostedService(IServiceProvider services, DatabaseProvider provider, HostProfile profile) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            DbContext db = profile.DbContextStyle == DbContextStyle.SqlOSDbContext
                ? scope.ServiceProvider.GetRequiredService<BehaviorLockDbContext>()
                : scope.ServiceProvider.GetRequiredService<ManualBehaviorLockDbContext>();
            var sql = provider == DatabaseProvider.SqlServer
                ? $"""
                   IF SCHEMA_ID(N'{ApplicationSchema}') IS NULL EXEC(N'CREATE SCHEMA [{ApplicationSchema}]');
                   IF OBJECT_ID(N'[{ApplicationSchema}].[{Workspace.TableName}]', N'U') IS NULL
                   CREATE TABLE [{ApplicationSchema}].[{Workspace.TableName}] (
                       [Id] nvarchar(64) NOT NULL CONSTRAINT [PK_{Workspace.TableName}] PRIMARY KEY,
                       [ResourceId] nvarchar(128) NOT NULL,
                       [Name] nvarchar(200) NOT NULL,
                       [ParentResourceId] nvarchar(128) NULL,
                       [IsActive] bit NOT NULL);
                   """
                : $"""
                   CREATE SCHEMA IF NOT EXISTS "{ApplicationSchema}";
                   CREATE TABLE IF NOT EXISTS "{ApplicationSchema}"."{Workspace.TableName}" (
                       "Id" varchar(64) NOT NULL PRIMARY KEY,
                       "ResourceId" varchar(128) NOT NULL,
                       "Name" varchar(200) NOT NULL,
                       "ParentResourceId" varchar(128) NULL,
                       "IsActive" boolean NOT NULL);
                   """;
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Gives every request a client address, as a real server would: the harness header when a
    /// scenario sets one, otherwise <see cref="DefaultClientAddress"/>.
    /// </summary>
    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var requested = context.Request.Headers[ClientAddressHeader].ToString();
                if (!string.IsNullOrEmpty(requested) && IPAddress.TryParse(requested, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }
                else
                {
                    context.Connection.RemoteIpAddress ??= DefaultClientAddress;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
