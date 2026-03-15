using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

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

        // Seed subject types
        await SeedIfNotExistsAsync<SqlOSFgaSubjectType>("user", new SqlOSFgaSubjectType { Id = "user", Name = "User", Description = "A human user" }, cancellationToken);
        await SeedIfNotExistsAsync<SqlOSFgaSubjectType>("group", new SqlOSFgaSubjectType { Id = "group", Name = "Group", Description = "A user group" }, cancellationToken);
        await SeedIfNotExistsAsync<SqlOSFgaSubjectType>("service_account", new SqlOSFgaSubjectType { Id = "service_account", Name = "Service Account", Description = "An automated service account" }, cancellationToken);
        await SeedIfNotExistsAsync<SqlOSFgaSubjectType>("agent", new SqlOSFgaSubjectType { Id = "agent", Name = "Agent", Description = "An automated agent (job, worker, AI)" }, cancellationToken);

        // Seed root resource type
        await SeedIfNotExistsAsync<SqlOSFgaResourceType>("root", new SqlOSFgaResourceType { Id = "root", Name = "Root", Description = "The root resource type" }, cancellationToken);

        // Seed root resource
        await SeedIfNotExistsAsync<SqlOSFgaResource>(_options.RootResourceId, new SqlOSFgaResource
        {
            Id = _options.RootResourceId,
            Name = _options.RootResourceName,
            ResourceTypeId = "root",
            IsActive = true,
        }, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Core SqlOSFga data seeded.");
    }

    public async Task SeedAuthorizationDataAsync(SqlOSFgaSeedData data, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding authorization data...");

        if (data.ResourceTypes != null)
        {
            foreach (var rt in data.ResourceTypes)
            {
                var existing = await _context.Set<SqlOSFgaResourceType>().FindAsync(new object[] { rt.Id }, cancellationToken);
                if (existing == null)
                {
                    _context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType
                    {
                        Id = rt.Id,
                        Name = rt.Name,
                        Description = rt.Description
                    });
                }
                else
                {
                    existing.Name = rt.Name;
                    existing.Description = rt.Description;
                }
            }
        }

        if (data.Roles != null)
        {
            foreach (var role in data.Roles)
            {
                var existing = await _context.Set<SqlOSFgaRole>().FindAsync(new object[] { role.Id }, cancellationToken);
                if (existing == null)
                {
                    _context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole
                    {
                        Id = role.Id,
                        Key = role.Key,
                        Name = role.Name,
                        Description = role.Description,
                        IsVirtual = role.IsVirtual
                    });
                }
                else
                {
                    existing.Key = role.Key;
                    existing.Name = role.Name;
                    existing.Description = role.Description;
                    existing.IsVirtual = role.IsVirtual;
                }
            }
        }

        if (data.Permissions != null)
        {
            foreach (var perm in data.Permissions)
            {
                var existing = await _context.Set<SqlOSFgaPermission>().FindAsync(new object[] { perm.Id }, cancellationToken);
                if (existing == null)
                {
                    _context.Set<SqlOSFgaPermission>().Add(new SqlOSFgaPermission
                    {
                        Id = perm.Id,
                        Key = perm.Key,
                        Name = perm.Name,
                        Description = perm.Description,
                        ResourceTypeId = perm.ResourceTypeId
                    });
                }
                else
                {
                    existing.Key = perm.Key;
                    existing.Name = perm.Name;
                    existing.Description = perm.Description;
                    existing.ResourceTypeId = perm.ResourceTypeId;
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (data.RolePermissions != null)
        {
            foreach (var (roleKey, permissionKeys) in data.RolePermissions)
            {
                var role = await _context.Set<SqlOSFgaRole>().FirstOrDefaultAsync(r => r.Key == roleKey, cancellationToken);
                if (role == null)
                {
                    _logger.LogWarning("Role with key {RoleKey} not found for role-permission mapping", roleKey);
                    continue;
                }

                foreach (var permKey in permissionKeys)
                {
                    var perm = await _context.Set<SqlOSFgaPermission>().FirstOrDefaultAsync(p => p.Key == permKey, cancellationToken);
                    if (perm == null)
                    {
                        _logger.LogWarning("Permission with key {PermKey} not found for role {RoleKey}", permKey, roleKey);
                        continue;
                    }

                    var exists = await _context.Set<SqlOSFgaRolePermission>()
                        .AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id, cancellationToken);

                    if (!exists)
                    {
                        _context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission
                        {
                            RoleId = role.Id,
                            PermissionId = perm.Id,
                        });
                    }
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Authorization data seeded.");
    }

    private async Task SeedIfNotExistsAsync<T>(string id, T entity, CancellationToken cancellationToken) where T : class
    {
        var existing = await _context.Set<T>().FindAsync(new object[] { id }, cancellationToken);
        if (existing == null)
        {
            _context.Set<T>().Add(entity);
        }
    }
}

public class SqlOSFgaSeedData
{
    public List<SqlOSFgaResourceType>? ResourceTypes { get; set; }
    public List<SqlOSFgaRole>? Roles { get; set; }
    public List<SqlOSFgaPermission>? Permissions { get; set; }
    public List<(string RoleKey, string[] PermissionKeys)>? RolePermissions { get; set; }
}
