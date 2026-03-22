using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.Services;

namespace SqlOS.Hosting;

/// <summary>
/// Runs SqlOS database bootstrap on host startup (schema, keys, seeds).
/// </summary>
public sealed class SqlOSBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<SqlOSOptions> _options;

    public SqlOSBootstrapHostedService(IServiceProvider services, IOptions<SqlOSOptions> options)
    {
        _services = services;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>();
        await bootstrapper.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
