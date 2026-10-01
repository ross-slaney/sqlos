using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Diagnostics;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Database;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Infrastructure.Hosting;

/// <summary>
/// One running behavior-lock host on its own fresh database, served in-memory by TestServer.
/// Disposing it stops the host and drops the database.
/// </summary>
public sealed class ScenarioHost : IAsyncDisposable
{
    private readonly bool _ownsDatabase;

    private ScenarioHost(HostProfile profile, WebApplication app, string connectionString, bool ownsDatabase)
    {
        Profile = profile;
        App = app;
        ConnectionString = connectionString;
        _ownsDatabase = ownsDatabase;
        Server = app.GetTestServer();
        Fakes = app.Services.GetRequiredService<BehaviorLockFakes>();
        Routes = app.Services.GetRequiredService<RouteHitRecorder>();
    }

    public HostProfile Profile { get; }

    public WebApplication App { get; }

    public TestServer Server { get; }

    public string ConnectionString { get; }

    public BehaviorLockFakes Fakes { get; }

    public RouteHitRecorder Routes { get; }

    public static async Task<ScenarioHost> StartAsync(
        string profile,
        ScenarioOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScenarioOptions();
        var hostProfile = HostProfiles.Get(profile);
        var connectionString = options.ExistingDatabase
            ?? await BehaviorLockDatabase.CreateDatabaseAsync(profile, cancellationToken);
        WebApplication? app = null;
        try
        {
            app = BehaviorLockHost.Build(new BehaviorLockHostOptions
            {
                Profile = profile,
                Provider = BehaviorLockDatabase.Provider,
                ConnectionString = connectionString,
                ConfigureSqlOS = options.ConfigureSqlOS,
                ConfigureServices = options.ConfigureServices,
                DataProtectionKeysDirectory = options.DataProtectionKeysDirectory,
                AnswerUnhandledExceptionsAsServerErrors = options.AnswerUnhandledExceptionsAsServerErrors
            });
            await app.StartAsync(cancellationToken);
            return new ScenarioHost(hostProfile, app, connectionString, ownsDatabase: options.ExistingDatabase == null);
        }
        catch
        {
            if (app != null)
            {
                await app.DisposeAsync();
            }

            if (options.ExistingDatabase == null)
            {
                await BehaviorLockDatabase.DropDatabaseAsync(connectionString, CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>A client that reaches this host in-process, addressed at the public origin.</summary>
    public HttpClient CreateClient()
        => new(Server.CreateHandler()) { BaseAddress = new Uri(BehaviorLockConstants.PublicOrigin) };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await App.StopAsync();
        }
        finally
        {
            await App.DisposeAsync();
            if (_ownsDatabase)
            {
                await BehaviorLockDatabase.DropDatabaseAsync(ConnectionString, CancellationToken.None);
            }
        }
    }
}
