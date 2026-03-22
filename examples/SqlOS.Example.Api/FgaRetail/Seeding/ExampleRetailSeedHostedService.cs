using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SqlOS.Example.Api.FgaRetail.Seeding;

/// <summary>
/// Runs after SqlOS bootstrap hosted service so retail demo data can use SqlOS tables.
/// </summary>
public sealed class ExampleRetailSeedHostedService : IHostedService
{
    private readonly IServiceProvider _services;

    public ExampleRetailSeedHostedService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<RetailSeedService>();
        await seeder.SeedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
