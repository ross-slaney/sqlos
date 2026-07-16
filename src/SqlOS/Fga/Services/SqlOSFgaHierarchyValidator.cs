using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Services;

/// <summary>
/// Validates persisted FGA resource hierarchy data against the configured depth and cycle policy.
/// </summary>
public sealed class SqlOSFgaHierarchyValidator
{
    private readonly ISqlOSFgaDbContext _context;
    private readonly int _maxDepth;

    public SqlOSFgaHierarchyValidator(
        ISqlOSFgaDbContext context,
        IOptions<SqlOSFgaOptions> options)
    {
        _context = context;
        _maxDepth = SqlOSFgaHierarchyDepth.Normalize(options.Value.MaxResourceHierarchyDepth);
    }

    public async Task ValidateExistingDataAsync(CancellationToken cancellationToken = default)
    {
        var resources = await _context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .Select(resource => new { resource.Id, resource.ParentId })
            .ToListAsync(cancellationToken);
        var parents = resources.ToDictionary(
            resource => resource.Id,
            resource => resource.ParentId,
            StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            ValidateResource(resource.Id, parents);
        }
    }

    private void ValidateResource(
        string resourceId,
        IReadOnlyDictionary<string, string?> parents)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? currentId = resourceId;
        var depth = 0;

        while (!string.IsNullOrWhiteSpace(currentId))
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException(
                    $"FGA resource hierarchy contains a cycle involving resource '{resourceId}'.");
            }

            if (depth > _maxDepth)
            {
                throw new InvalidOperationException(
                    $"FGA resource '{resourceId}' exceeds the configured maximum hierarchy depth of {_maxDepth}. "
                    + "Increase Fga.MaxResourceHierarchyDepth or repair the persisted resource hierarchy before startup.");
            }

            if (!parents.TryGetValue(currentId, out var parentId))
            {
                throw new InvalidOperationException(
                    $"FGA resource '{resourceId}' references missing parent resource '{currentId}'. "
                    + "Repair the persisted resource hierarchy before startup.");
            }

            currentId = parentId;
            depth++;
        }
    }
}
