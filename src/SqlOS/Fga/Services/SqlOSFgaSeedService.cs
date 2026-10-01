using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Domain;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Processes;

namespace SqlOS.Fga.Services;

public class SqlOSFgaSeedService
{
    private readonly ISqlOSFgaDbContext _context;
    private readonly SqlOSFgaOptions _options;
    private readonly ILogger<SqlOSFgaSeedService> _logger;

    public SqlOSFgaSeedService(
        ISqlOSFgaDbContext context,
        IOptions<SqlOSFgaOptions> options,
        ILogger<SqlOSFgaSeedService> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedCoreAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding core SqlOSFga data...");
        await new SeedFgaCore(_context, _options).ExecuteAsync(cancellationToken);
        _logger.LogInformation("Core SqlOSFga data seeded.");
    }

    public Task SeedAuthorizationDataAsync(SqlOSFgaSeedData data, CancellationToken cancellationToken = default)
        => SeedAuthorizationDataAsync(data, FgaActor.Host, cancellationToken);

    /// <summary>Reconciles <c>options.Fga.Seed</c> at startup.</summary>
    internal Task SeedStartupDataAsync(SqlOSFgaSeedData data, CancellationToken cancellationToken = default)
        => SeedAuthorizationDataAsync(data, FgaActor.Startup, cancellationToken);

    private async Task SeedAuthorizationDataAsync(SqlOSFgaSeedData data, FgaActor actor, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Seeding authorization data...");
        await new ReconcileFgaModel(_context, _logger).ExecuteAsync(data, actor, cancellationToken);
        _logger.LogInformation("Authorization data seeded.");
    }
}

/// <summary>
/// The authorization model <see cref="SqlOSFgaSeedService.SeedAuthorizationDataAsync"/> reconciles:
/// resource types, permissions, roles, and each role's permission keys.
/// </summary>
public class SqlOSFgaSeedData
{
    public List<SqlOSFgaResourceTypeSeed>? ResourceTypes { get; set; }
    public List<SqlOSFgaRoleSeed>? Roles { get; set; }
    public List<SqlOSFgaPermissionSeed>? Permissions { get; set; }
    public List<(string RoleKey, string[] PermissionKeys)>? RolePermissions { get; set; }
}

/// <summary>A resource type in <see cref="SqlOSFgaSeedData"/>.</summary>
public sealed record SqlOSFgaResourceTypeSeed
{
    /// <summary>The stable resource type identifier referenced by resources and permissions.</summary>
    public required string Id { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>An optional description.</summary>
    public string? Description { get; init; }
}

/// <summary>A role in <see cref="SqlOSFgaSeedData"/>.</summary>
public sealed record SqlOSFgaRoleSeed
{
    /// <summary>The stable role identifier stored on grants.</summary>
    public required string Id { get; init; }

    /// <summary>The application-facing key that grant helpers and role permissions use.</summary>
    public required string Key { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>An optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the role is virtual.</summary>
    public bool IsVirtual { get; init; }
}

/// <summary>A permission in <see cref="SqlOSFgaSeedData"/>.</summary>
public sealed record SqlOSFgaPermissionSeed
{
    /// <summary>The stable permission identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The application-facing key that access checks use.</summary>
    public required string Key { get; init; }

    /// <summary>The display name.</summary>
    public required string Name { get; init; }

    /// <summary>An optional description.</summary>
    public string? Description { get; init; }

    /// <summary>The resource type the permission applies to, or null for every resource type.</summary>
    public string? ResourceTypeId { get; init; }
}
