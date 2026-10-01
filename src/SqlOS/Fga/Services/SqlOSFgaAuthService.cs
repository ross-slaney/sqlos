using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Services;

public class SqlOSFgaAuthService : ISqlOSFgaAuthService
{
    private readonly ISqlOSFgaDbContext _context;
    private readonly SqlOSFgaOptions _options;
    private readonly ILogger<SqlOSFgaAuthService> _logger;

    public SqlOSFgaAuthService(
        ISqlOSFgaDbContext context,
        IOptions<SqlOSFgaOptions> options,
        ILogger<SqlOSFgaAuthService> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> HasCapabilityAsync(string subjectId, string permissionKey)
    {
        var result = await CheckAccessAsync(subjectId, permissionKey, _options.RootResourceId);
        return result.Allowed;
    }

    public async Task<SqlOSFgaAccessCheckResult> CheckAccessAsync(
        string subjectId,
        string permissionKey,
        string resourceId)
    {
        var trace = new List<SqlOSFgaAccessTrace>();

        var resource = await _context.Set<SqlOSFgaResource>()
            .Include(r => r.ResourceType)
            .FirstOrDefaultAsync(r => r.Id == resourceId);

        if (resource == null)
        {
            return new SqlOSFgaAccessCheckResult { Allowed = false, Trace = trace, Error = $"Resource {resourceId} not found" };
        }

        trace.Add(new SqlOSFgaAccessTrace
        {
            Step = "Target Resource",
            Detail = $"Checking access to {resource.Name} ({resource.ResourceTypeId})",
            ResourceId = resource.Id,
            ResourceName = resource.Name,
        });

        var permission = await _context.Set<SqlOSFgaPermission>().FirstOrDefaultAsync(p => p.Key == permissionKey);
        if (permission == null)
        {
            return new SqlOSFgaAccessCheckResult { Allowed = false, Trace = trace, Error = $"Permission {permissionKey} not found" };
        }

        if (permission.ResourceTypeId != null
            && !string.Equals(permission.ResourceTypeId, resource.ResourceTypeId, StringComparison.Ordinal))
        {
            trace.Add(new SqlOSFgaAccessTrace
            {
                Step = "Permission Scope",
                Detail = $"Permission {permission.Name} applies to resource type {permission.ResourceTypeId}, not {resource.ResourceTypeId}",
                ResourceId = resource.Id,
                ResourceName = resource.Name,
            });
            return new SqlOSFgaAccessCheckResult
            {
                Allowed = false,
                Trace = trace,
                Error = $"Permission {permissionKey} does not apply to resource type {resource.ResourceTypeId}"
            };
        }

        trace.Add(new SqlOSFgaAccessTrace
        {
            Step = "Permission",
            Detail = $"Looking for permission: {permission.Name}",
        });

        var allSubjects = await ResolveSubjectsAsync(subjectId, trace);
        if (allSubjects.Count == 0)
        {
            return new SqlOSFgaAccessCheckResult { Allowed = false, Trace = trace, Error = "No subjects found" };
        }

        var ancestorResources = await GetAncestorResourcesAsync(resourceId);
        trace.Add(new SqlOSFgaAccessTrace
        {
            Step = "Resource Path",
            Detail = $"Checking {ancestorResources.Count} resource(s) in hierarchy",
        });

        foreach (var ancestorResource in ancestorResources)
        {
            var resourceType = await _context.Set<SqlOSFgaResourceType>().FirstOrDefaultAsync(rt => rt.Id == ancestorResource.ResourceTypeId);
            trace.Add(new SqlOSFgaAccessTrace
            {
                Step = "Checking Resource",
                Detail = $"[{resourceType?.Name ?? "Unknown"}] {ancestorResource.Name}",
                ResourceId = ancestorResource.Id,
                ResourceName = ancestorResource.Name,
            });

            foreach (var sid in allSubjects)
            {
                var subjectData = await _context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(s => s.Id == sid);
                var grants = await GetActiveGrantsAsync(sid, ancestorResource.Id);

                foreach (var grant in grants)
                {
                    var role = await _context.Set<SqlOSFgaRole>().FirstOrDefaultAsync(r => r.Id == grant.RoleId);
                    if (role == null) continue;

                    var roleHasPermission = await _context.Set<SqlOSFgaRolePermission>()
                        .AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id);

                    if (roleHasPermission)
                    {
                        trace.Add(new SqlOSFgaAccessTrace
                        {
                            Step = "Access Granted",
                            Detail = $"Role \"{role.Name}\" provides permission \"{permission.Name}\" via grant at {ancestorResource.Name}",
                            ResourceId = ancestorResource.Id,
                            ResourceName = ancestorResource.Name,
                            GrantId = grant.Id,
                            RoleName = role.Name,
                            SubjectName = subjectData?.DisplayName,
                        });
                        return new SqlOSFgaAccessCheckResult { Allowed = true, Trace = trace };
                    }
                    else
                    {
                        trace.Add(new SqlOSFgaAccessTrace
                        {
                            Step = "Role Checked",
                            Detail = $"Role \"{role.Name}\" does not provide permission \"{permission.Name}\"",
                            RoleName = role.Name,
                        });
                    }
                }
            }
        }

        trace.Add(new SqlOSFgaAccessTrace
        {
            Step = "Access Denied",
            Detail = $"No matching grants found that provide permission \"{permission.Name}\"",
        });

        return new SqlOSFgaAccessCheckResult { Allowed = false, Trace = trace };
    }

    public async Task<SqlOSFgaResourceAccessTrace> TraceResourceAccessAsync(
        string subjectId,
        string resourceId,
        string permissionKey)
    {
        var trace = new SqlOSFgaResourceAccessTrace
        {
            SubjectId = subjectId,
            PermissionKey = permissionKey
        };

        var resource = await _context.Set<SqlOSFgaResource>()
            .Include(r => r.ResourceType)
            .FirstOrDefaultAsync(r => r.Id == resourceId);

        if (resource == null)
        {
            trace.AccessGranted = false;
            trace.DenialReason = $"Resource '{resourceId}' not found";
            trace.DecisionSummary = $"Access denied because the resource '{resourceId}' does not exist.";
            return trace;
        }

        trace.TargetResourceId = resource.Id;
        trace.TargetResourceName = resource.Name;
        trace.TargetResourceType = resource.ResourceType?.Name ?? resource.ResourceTypeId;

        var subject = await _context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(s => s.Id == subjectId);
        if (subject == null)
        {
            trace.AccessGranted = false;
            trace.DenialReason = $"Subject '{subjectId}' not found";
            trace.DecisionSummary = $"Access denied because the subject '{subjectId}' does not exist.";
            return trace;
        }

        trace.SubjectDisplayName = subject.DisplayName;

        var permission = await _context.Set<SqlOSFgaPermission>().FirstOrDefaultAsync(p => p.Key == permissionKey);
        if (permission == null)
        {
            trace.AccessGranted = false;
            trace.DenialReason = $"Permission '{permissionKey}' not found";
            trace.DecisionSummary = $"Access denied because the permission '{permissionKey}' does not exist.";
            return trace;
        }

        trace.PermissionName = permission.Name;

        if (permission.ResourceTypeId != null
            && !string.Equals(permission.ResourceTypeId, resource.ResourceTypeId, StringComparison.Ordinal))
        {
            trace.AccessGranted = false;
            trace.DenialReason = $"Permission '{permissionKey}' applies to resource type '{permission.ResourceTypeId}', not '{resource.ResourceTypeId}'.";
            trace.DecisionSummary = $"Access denied because permission '{permissionKey}' is not valid for this resource type.";
            return trace;
        }

        var allSubjects = await ResolveSubjectsWithInfoAsync(subjectId);
        trace.SubjectsChecked = allSubjects;

        var subjectIds = allSubjects.Select(s => s.SubjectId).ToList();
        var ancestorResources = await GetAncestorResourcesAsync(resourceId);
        var pathNodes = new List<SqlOSFgaResourcePathNodeTrace>();
        var allGrantsUsed = new List<SqlOSFgaGrantTrace>();
        var allRolesUsed = new Dictionary<string, SqlOSFgaRoleTrace>();
        var now = DateTime.UtcNow;

        bool accessGranted = false;
        string? grantingNodeName = null;
        string? grantingRoleName = null;
        string? grantingPrincipalName = null;
        bool grantedViaGroup = false;
        string? grantingGroupName = null;

        int depth = 0;
        foreach (var ancestorResource in ancestorResources)
        {
            var resourceType = await _context.Set<SqlOSFgaResourceType>().FirstOrDefaultAsync(rt => rt.Id == ancestorResource.ResourceTypeId);

            var pathNode = new SqlOSFgaResourcePathNodeTrace
            {
                ResourceId = ancestorResource.Id,
                Name = ancestorResource.Name,
                ResourceType = resourceType?.Name ?? ancestorResource.ResourceTypeId,
                Depth = depth,
                IsTarget = ancestorResource.Id == resourceId
            };

            var grantsOnNode = await _context.Set<SqlOSFgaGrant>()
                .Include(g => g.Role)
                .Include(g => g.Subject)
                .Where(g => g.ResourceId == ancestorResource.Id &&
                           subjectIds.Contains(g.SubjectId) &&
                           (g.EffectiveFrom == null || g.EffectiveFrom <= now) &&
                           (g.EffectiveTo == null || g.EffectiveTo >= now))
                .ToListAsync();

            foreach (var grant in grantsOnNode)
            {
                var role = grant.Role;
                if (role == null) continue;

                var rolePermissions = await _context.Set<SqlOSFgaRolePermission>()
                    .Include(rp => rp.Permission)
                    .Where(rp => rp.RoleId == role.Id)
                    .ToListAsync();

                var hasRequestedPermission = rolePermissions.Any(rp => rp.PermissionId == permission.Id);

                var subjectInfo = allSubjects.FirstOrDefault(s => s.SubjectId == grant.SubjectId);
                var isDirect = subjectInfo?.IsDirect ?? false;
                var viaGroupName = !isDirect ? subjectInfo?.DisplayName : null;

                var grantTrace = new SqlOSFgaGrantTrace
                {
                    GrantId = grant.Id,
                    ResourceId = ancestorResource.Id,
                    ResourceName = ancestorResource.Name,
                    ResourceType = pathNode.ResourceType,
                    RoleKey = role.Key,
                    RoleName = role.Name,
                    SubjectId = grant.SubjectId,
                    SubjectDisplayName = grant.Subject?.DisplayName ?? grant.SubjectId,
                    AppliesToSubject = true,
                    IsDirectGrant = isDirect,
                    ViaGroupName = viaGroupName,
                    ContributedToDecision = hasRequestedPermission && !accessGranted
                };

                pathNode.GrantsOnThisNode.Add(grantTrace);
                allGrantsUsed.Add(grantTrace);

                if (!allRolesUsed.ContainsKey(role.Id))
                {
                    var roleTrace = new SqlOSFgaRoleTrace
                    {
                        RoleKey = role.Key,
                        RoleName = role.Name,
                        IsFromGrant = true,
                        IsVirtualRole = role.IsVirtual,
                        SourceResourceId = ancestorResource.Id,
                        SourceResourceName = ancestorResource.Name,
                        SourceResourceType = pathNode.ResourceType,
                        ContributedToDecision = hasRequestedPermission && !accessGranted,
                        Permissions = rolePermissions.Select(rp => new SqlOSFgaPermissionAssignmentTrace
                        {
                            PermissionKey = rp.Permission?.Key ?? "",
                            PermissionName = rp.Permission?.Name ?? "",
                            UsedForDecision = rp.PermissionId == permission.Id
                        }).ToList()
                    };
                    allRolesUsed[role.Id] = roleTrace;
                }

                foreach (var rp in rolePermissions)
                {
                    if (rp.Permission != null && !pathNode.EffectivePermissions.Contains(rp.Permission.Key))
                    {
                        pathNode.EffectivePermissions.Add(rp.Permission.Key);
                    }
                }

                if (hasRequestedPermission && !accessGranted)
                {
                    accessGranted = true;
                    pathNode.PermissionFoundHere = true;
                    grantingNodeName = ancestorResource.Name;
                    grantingRoleName = role.Name;
                    grantingPrincipalName = grant.Subject?.DisplayName ?? grant.SubjectId;
                    grantedViaGroup = !isDirect;
                    grantingGroupName = viaGroupName;
                }
            }

            pathNodes.Add(pathNode);
            depth++;
        }

        pathNodes.Reverse();
        for (int i = 0; i < pathNodes.Count; i++)
        {
            pathNodes[i].Depth = i;
        }

        trace.PathNodes = pathNodes;
        trace.AllRolesUsed = allRolesUsed.Values.ToList();
        trace.GrantsUsed = allGrantsUsed;
        trace.AccessGranted = accessGranted;

        if (accessGranted)
        {
            if (grantedViaGroup && grantingGroupName != null)
            {
                trace.DecisionSummary = $"Access granted via group '{grantingGroupName}' which has role '{grantingRoleName}' on '{grantingNodeName}'. " +
                                       $"The role '{grantingRoleName}' includes permission '{permissionKey}' which is inherited by child resources.";
            }
            else
            {
                var targetNode = pathNodes.FirstOrDefault(n => n.IsTarget);
                if (targetNode != null && targetNode.PermissionFoundHere)
                {
                    trace.DecisionSummary = $"Access granted because {trace.SubjectDisplayName} has role '{grantingRoleName}' " +
                                           $"directly on this resource, and '{grantingRoleName}' includes permission '{permissionKey}'.";
                }
                else
                {
                    trace.DecisionSummary = $"Access granted because {trace.SubjectDisplayName} has role '{grantingRoleName}' " +
                                           $"on parent resource '{grantingNodeName}', and '{grantingRoleName}' includes permission '{permissionKey}' " +
                                           $"which is inherited by child resources.";
                }
            }
        }
        else
        {
            if (allGrantsUsed.Count == 0)
            {
                trace.DenialReason = $"No grants found for {trace.SubjectDisplayName} (or their groups) on this resource or any ancestor resources.";
                trace.DecisionSummary = $"Access denied. No roles are assigned to {trace.SubjectDisplayName} on '{trace.TargetResourceName}' " +
                                       $"or any of its parent resources.";
                trace.Suggestion = $"To grant access, assign a role that includes '{permissionKey}' on this resource or on a parent.";
            }
            else
            {
                var roleNames = allRolesUsed.Values.Select(r => r.RoleName).Distinct().ToList();
                trace.DenialReason = $"Grants were found, but none of the roles ({string.Join(", ", roleNames)}) include permission '{permissionKey}'.";
                trace.DecisionSummary = $"Access denied. {trace.SubjectDisplayName} has grants on ancestor resources, " +
                                       $"but none of the assigned roles include permission '{permissionKey}'.";
                trace.Suggestion = $"Either assign a different role that includes '{permissionKey}', or add '{permissionKey}' " +
                                  $"to one of the existing roles ({string.Join(", ", roleNames)}).";
            }
        }

        return trace;
    }

    public async Task<Expression<Func<T, bool>>> BuildFilterAsync<T>(
        string subjectId,
        string permissionKey) where T : IHasResourceId
    {
        var subjectIds = await ResolveSubjectIdsAsync(subjectId);
        if (subjectIds.Count == 0)
        {
            _logger.LogWarning("No subjects found for {SubjectId}", subjectId);
            return entity => false;
        }

        var permission = await _context.Set<SqlOSFgaPermission>()
            .AsNoTracking()
            .Where(p => p.Key == permissionKey)
            .Select(p => new
            {
                p.Id,
                p.ResourceTypeId,
                TypeSeq = p.ResourceTypeId == null ? null : EF.Property<int?>(p.ResourceType!, SqlOSFgaLineage.SeqColumn),
            })
            .FirstOrDefaultAsync();

        if (permission == null)
        {
            _logger.LogWarning("Permission {PermissionKey} not found", permissionKey);
            return entity => false;
        }

        // The caller's access roots, read once: the resources their live subjects hold a current grant on
        // with a role that includes the permission. The filter compares each row's ancestor at a root's
        // level with the root. Grants are read when the filter is built, as group membership is; build the
        // filter per request. A caller with more roots than the filter lists is checked row by row instead.
        var provider = SqlOSDatabase.Resolve(_context.Database);
        var subjectIdsJson = JsonSerializer.Serialize(subjectIds);
        var roots = await _context.Set<SqlOSFgaAccessRoot>()
            .FromSqlRaw(provider.BuildAccessRootsQuerySql(_options), subjectIdsJson, permission.Id)
            .AsNoTracking()
            .Take(SqlOSFgaLineage.MaxListedRoots + 1)
            .ToListAsync();
        if (roots.Count == 0)
        {
            return entity => false;
        }

        var liveQuery = _context.Set<SqlOSFgaActiveSubject>()
            .FromSqlRaw(provider.BuildActiveSubjectsQuerySql(_options), subjectIdsJson)
            .AsNoTracking();

        var model = (_context as DbContext)?.Model;
        var entityType = model?.FindEntityType(typeof(T));
        var scoped = entityType is not null && SqlOSFgaScopeColumns.Has(entityType);
        var levels = scoped
            ? CountLevels(entityType!, SqlOSFgaLineage.ScopeAncestorColumn)
            : CountLevels(model?.FindEntityType(typeof(SqlOSFgaResource)), SqlOSFgaLineage.AncestorColumn);
        return SqlOSFgaFilterBuilder.Build<T>(_context, roots, liveQuery, subjectIdsJson, permission.Id, permission.ResourceTypeId, permission.TypeSeq, scoped, levels);
    }

    /// <summary>The ancestor columns the model declares for an entity type, or the configured depth's when the model is unavailable.</summary>
    private int CountLevels(IEntityType? entityType, Func<int, string> ancestorColumn)
    {
        if (entityType is null)
        {
            return SqlOSFgaLineage.Levels(_options);
        }

        var levels = 0;
        while (entityType.FindProperty(ancestorColumn(levels)) is not null)
        {
            levels++;
        }

        return levels > 0 ? levels : SqlOSFgaLineage.Levels(_options);
    }

    private async Task<List<string>> ResolveSubjectsAsync(string subjectId, List<SqlOSFgaAccessTrace> trace)
    {
        var subjects = await ResolveSubjectIdsAsync(subjectId);

        var subjectData = await _context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(s => s.Id == subjectId);
        var groupCount = subjects.Count - 1;
        trace.Add(new SqlOSFgaAccessTrace
        {
            Step = "Subject Resolution",
            Detail = $"Subject \"{subjectData?.DisplayName}\" + {groupCount} group membership(s)",
            SubjectName = subjectData?.DisplayName,
        });

        return subjects;
    }

    private async Task<List<string>> ResolveSubjectIdsAsync(string subjectId)
    {
        if (!await IsSubjectActiveAsync(subjectId))
        {
            return [];
        }

        var subjects = new List<string> { subjectId };

        var groupSubjectIds = await _context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Join(_context.Set<SqlOSFgaUserGroup>(),
                m => m.UserGroupId,
                g => g.Id,
                (m, g) => g)
            .Where(g => g.IsActive)
            .Select(g => g.SubjectId)
            .ToListAsync();

        subjects.AddRange(groupSubjectIds);
        return subjects;
    }

    private async Task<List<SqlOSFgaSubjectInfo>> ResolveSubjectsWithInfoAsync(string subjectId)
    {
        var result = new List<SqlOSFgaSubjectInfo>();

        if (!await IsSubjectActiveAsync(subjectId))
        {
            return result;
        }

        var subject = await _context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(s => s.Id == subjectId);
        result.Add(new SqlOSFgaSubjectInfo
        {
            SubjectId = subjectId,
            DisplayName = subject?.DisplayName ?? subjectId,
            Type = "user",
            IsDirect = true
        });

        var memberships = await _context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .ToListAsync();

        foreach (var membership in memberships)
        {
            var group = await _context.Set<SqlOSFgaUserGroup>()
                .FirstOrDefaultAsync(g => g.Id == membership.UserGroupId && g.IsActive);
            if (group != null)
            {
                result.Add(new SqlOSFgaSubjectInfo
                {
                    SubjectId = group.SubjectId,
                    DisplayName = group.Name,
                    Type = "usergroup",
                    IsDirect = false
                });
            }
        }

        return result;
    }

    private async Task<List<SqlOSFgaResource>> GetAncestorResourcesAsync(string resourceId)
    {
        var ancestors = new List<SqlOSFgaResource>();
        string? currentId = resourceId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        var maxDepth = Math.Max(1, _options.MaxResourceHierarchyDepth);

        while (currentId != null)
        {
            if (!visited.Add(currentId))
            {
                throw new InvalidOperationException("FGA resource hierarchy contains a cycle.");
            }

            if (depth > maxDepth)
            {
                throw new InvalidOperationException($"FGA resource hierarchy exceeds the configured maximum depth of {maxDepth}.");
            }

            var resource = await _context.Set<SqlOSFgaResource>().FirstOrDefaultAsync(r => r.Id == currentId);
            if (resource == null || !resource.IsActive) break;
            ancestors.Add(resource);
            currentId = resource.ParentId;
            depth++;
        }

        return ancestors;
    }

    private async Task<bool> IsSubjectActiveAsync(string subjectId)
    {
        var subjectType = await _context.Set<SqlOSFgaSubject>()
            .AsNoTracking()
            .Where(subject => subject.Id == subjectId)
            .Select(subject => subject.SubjectTypeId)
            .FirstOrDefaultAsync();

        return subjectType switch
        {
            "user" => await _context.Set<SqlOSFgaUser>()
                .AsNoTracking()
                .AnyAsync(user => user.SubjectId == subjectId && user.IsActive),
            "service_account" => await _context.Set<SqlOSFgaServiceAccount>()
                .AsNoTracking()
                .AnyAsync(account => account.SubjectId == subjectId
                    && (account.ExpiresAt == null || account.ExpiresAt > DateTime.UtcNow)),
            "group" => await _context.Set<SqlOSFgaUserGroup>()
                .AsNoTracking()
                .AnyAsync(group => group.SubjectId == subjectId && group.IsActive),
            "agent" => await _context.Set<SqlOSFgaAgent>()
                .AsNoTracking()
                .AnyAsync(agent => agent.SubjectId == subjectId),
            _ => false
        };
    }

    private async Task<List<SqlOSFgaGrant>> GetActiveGrantsAsync(string subjectId, string resourceId)
    {
        var now = DateTime.UtcNow;

        return await _context.Set<SqlOSFgaGrant>()
            .Where(g => g.SubjectId == subjectId &&
                       g.ResourceId == resourceId &&
                       (g.EffectiveFrom == null || g.EffectiveFrom <= now) &&
                       (g.EffectiveTo == null || g.EffectiveTo >= now))
            .ToListAsync();
    }
}
