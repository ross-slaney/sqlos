using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Schema;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
                await SeedIfNotExistsAsync<SqlOSFgaResourceType>(rt.Id, rt, cancellationToken);
            }
        }

        if (data.Roles != null)
        {
            foreach (var role in data.Roles)
            {
                await SeedIfNotExistsAsync<SqlOSFgaRole>(role.Id, role, cancellationToken);
            }
        }

        if (data.Permissions != null)
        {
            foreach (var perm in data.Permissions)
            {
                await SeedIfNotExistsAsync<SqlOSFgaPermission>(perm.Id, perm, cancellationToken);
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

    /// <summary>
    /// Seed authorization schema from YAML content. Idempotent (upsert semantics).
    /// </summary>
    public async Task SeedFromYamlAsync(string yamlContent, CancellationToken cancellationToken = default)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var schema = deserializer.Deserialize<SqlOSFgaSchemaYaml>(yamlContent)
            ?? throw new ArgumentException("Invalid YAML schema");

        _logger.LogInformation("Seeding from YAML schema (version {Version})", schema.Version);

        if (schema.ResourceTypes != null)
        {
            foreach (var rt in schema.ResourceTypes)
            {
                var existing = await _context.Set<SqlOSFgaResourceType>().FindAsync(new object[] { rt.Id }, cancellationToken);
                if (existing != null)
                {
                    existing.Name = rt.Name;
                    existing.Description = rt.Description;
                }
                else
                {
                    _context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType
                    {
                        Id = rt.Id,
                        Name = rt.Name,
                        Description = rt.Description
                    });
                }
            }
        }

        if (schema.Permissions != null)
        {
            foreach (var p in schema.Permissions)
            {
                var existing = await _context.Set<SqlOSFgaPermission>().FindAsync(new object[] { p.Id }, cancellationToken);
                if (existing != null)
                {
                    existing.Key = p.Key;
                    existing.Name = p.Name;
                    existing.Description = p.Description;
                    existing.ResourceTypeId = p.ResourceTypeId;
                }
                else
                {
                    _context.Set<SqlOSFgaPermission>().Add(new SqlOSFgaPermission
                    {
                        Id = p.Id,
                        Key = p.Key,
                        Name = p.Name,
                        Description = p.Description,
                        ResourceTypeId = p.ResourceTypeId
                    });
                }
            }
        }

        if (schema.Roles != null)
        {
            foreach (var r in schema.Roles)
            {
                var existing = await _context.Set<SqlOSFgaRole>().FindAsync(new object[] { r.Id }, cancellationToken);
                if (existing != null)
                {
                    existing.Key = r.Key;
                    existing.Name = r.Name;
                    existing.Description = r.Description;
                    existing.IsVirtual = r.IsVirtual;
                }
                else
                {
                    _context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole
                    {
                        Id = r.Id,
                        Key = r.Key,
                        Name = r.Name,
                        Description = r.Description,
                        IsVirtual = r.IsVirtual
                    });
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (schema.Roles != null)
        {
            foreach (var r in schema.Roles)
            {
                if (r.Permissions == null || r.Permissions.Count == 0) continue;

                var role = await _context.Set<SqlOSFgaRole>().FirstOrDefaultAsync(x => x.Id == r.Id, cancellationToken);
                if (role == null) continue;

                foreach (var permKey in r.Permissions)
                {
                    var perm = await _context.Set<SqlOSFgaPermission>().FirstOrDefaultAsync(p => p.Key == permKey, cancellationToken);
                    if (perm == null)
                    {
                        _logger.LogWarning("Permission key {Key} not found for role {RoleId}", permKey, r.Id);
                        continue;
                    }

                    var exists = await _context.Set<SqlOSFgaRolePermission>()
                        .AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id, cancellationToken);
                    if (!exists)
                    {
                        _context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission
                        {
                            RoleId = role.Id,
                            PermissionId = perm.Id
                        });
                    }
                }
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("YAML schema seeded.");
    }

    /// <summary>
    /// Seed authorization schema from a YAML file.
    /// </summary>
    public async Task SeedFromYamlFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
        await SeedFromYamlAsync(yaml, cancellationToken);
    }

    /// <summary>
    /// Export current authorization schema to YAML string.
    /// </summary>
    public string ExportToYaml()
    {
        var schema = new SqlOSFgaSchemaYaml { Version = 1 };

        var resourceTypes = _context.Set<SqlOSFgaResourceType>()
            .Where(rt => rt.Id != "root")
            .OrderBy(rt => rt.Id)
            .Select(rt => new ResourceTypeEntry
            {
                Id = rt.Id,
                Name = rt.Name,
                Description = rt.Description
            })
            .ToList();
        schema.ResourceTypes = resourceTypes;

        var permissions = _context.Set<SqlOSFgaPermission>()
            .OrderBy(p => p.Key)
            .Select(p => new PermissionEntry
            {
                Id = p.Id,
                Key = p.Key,
                Name = p.Name,
                Description = p.Description,
                ResourceTypeId = p.ResourceTypeId
            })
            .ToList();
        schema.Permissions = permissions;

        var roles = _context.Set<SqlOSFgaRole>()
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .OrderBy(r => r.Name)
            .ToList();

        schema.Roles = roles.Select(r => new RoleEntry
        {
            Id = r.Id,
            Key = r.Key,
            Name = r.Name,
            Description = r.Description,
            IsVirtual = r.IsVirtual,
            Permissions = r.RolePermissions
                .Where(rp => rp.Permission != null)
                .Select(rp => rp.Permission!.Key)
                .OrderBy(k => k)
                .ToList()
        }).ToList();

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        return serializer.Serialize(schema);
    }
}

public class SqlOSFgaSeedData
{
    public List<SqlOSFgaResourceType>? ResourceTypes { get; set; }
    public List<SqlOSFgaRole>? Roles { get; set; }
    public List<SqlOSFgaPermission>? Permissions { get; set; }
    public List<(string RoleKey, string[] PermissionKeys)>? RolePermissions { get; set; }
}
