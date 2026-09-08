using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Extensions;

namespace SqlOS.Tests.Infrastructure;

/// <summary>
/// Builds a real <see cref="WebApplication"/> on the in-memory provider exactly the way an
/// application would: <c>AddSqlOS</c> plus its own endpoints, and nothing else. Tests use it to
/// prove what the SqlOS startup filter maps and protects without a manual mapping call.
/// </summary>
internal sealed class SingleApplicationTestHost : IAsyncDisposable
{
    public const string Origin = "https://todo.example.test";

    private SingleApplicationTestHost(WebApplication app, HttpClient client, RecordingLoggerProvider logs, string databaseName)
    {
        App = app;
        Client = client;
        Logs = logs;
        DatabaseName = databaseName;
    }

    public WebApplication App { get; }
    public HttpClient Client { get; }
    public RecordingLoggerProvider Logs { get; }
    public string DatabaseName { get; }

    public static async Task<SingleApplicationTestHost> StartAsync(
        Action<SqlOSOptions> configure,
        Action<WebApplication>? configureApp = null,
        string environment = "Development",
        Action<IServiceCollection>? configureServices = null)
    {
        var databaseName = $"single-app-host-{Guid.NewGuid():N}";
        var logs = new RecordingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(database => database.UseInMemoryDatabase(databaseName));
        builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(configure);
        // Bootstrap (schema/seed) needs a relational provider; tests seed rows directly instead.
        builder.Services.RemoveAll<IHostedService>();

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        configureApp?.Invoke(app);
        await app.StartAsync();

        var client = app.GetTestClient();
        client.BaseAddress = new Uri(Origin);
        return new SingleApplicationTestHost(app, client, logs, databaseName);
    }

    /// <summary>
    /// Mints a real signed access token through the host's own <see cref="SqlOSCryptoService"/>
    /// for a freshly seeded user, client, and session with the requested audience.
    /// </summary>
    public async Task<string> MintAccessTokenAsync(string audience, string? scope = null)
    {
        await using var scopeServices = App.Services.CreateAsyncScope();
        var context = scopeServices.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
        var crypto = scopeServices.ServiceProvider.GetRequiredService<SqlOSCryptoService>();

        var user = new SqlOSUser
        {
            Id = $"usr_{Guid.NewGuid():N}"[..28],
            DisplayName = "Surface User",
            DefaultEmail = $"surface-{Guid.NewGuid():N}@example.test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var client = new SqlOSClientApplication
        {
            Id = $"cli_{Guid.NewGuid():N}"[..28],
            ClientId = $"surface-client-{Guid.NewGuid():N}"[..30],
            Name = "Surface Client",
            Audience = audience,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var session = new SqlOSSession
        {
            Id = $"ses_{Guid.NewGuid():N}"[..28],
            UserId = user.Id,
            ClientApplicationId = client.Id,
            AuthenticationMethod = "password",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1),
            EffectiveAudience = audience,
            Scope = scope
        };
        context.Set<SqlOSUser>().Add(user);
        context.Set<SqlOSClientApplication>().Add(client);
        context.Set<SqlOSSession>().Add(session);
        await context.SaveChangesAsync();

        return await crypto.CreateAccessTokenAsync(user, session, client, organizationId: null);
    }

    public SqlOSAuthServerOptions AuthOptions => App.Services.GetRequiredService<IOptions<SqlOSAuthServerOptions>>().Value;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }

    public sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Category, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Record(LogLevel level, string category, string message)
        {
            lock (_entries)
            {
                _entries.Add((level, category, message));
            }
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => provider.Record(logLevel, category, formatter(state, exception));
        }
    }
}
