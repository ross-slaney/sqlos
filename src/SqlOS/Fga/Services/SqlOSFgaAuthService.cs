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
        EnsureRelational();
        var trace = new List<SqlOSFgaAccessTrace>();

        var resource = await _context.Set<SqlOSFgaResource>()
            .AsNoTracking()
            .Where(r => r.Id == resourceId)
            .Select(r => new { r.Id, r.Name, r.ResourceTypeId })
            .FirstOrDefaultAsync();

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

        var permission = await _context.Set<SqlOSFgaPermission>().AsNoTracking().FirstOrDefaultAsync(p => p.Key == permissionKey);
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

        // The decision: one query over the target's lineage (fn_IsResourceAccessible), the same rule the list
        // filter applies to a row, read from the other side.
        var match = await FindDecidingGrantAsync(resourceId, allSubjects, permission.Id);
        if (match == null)
        {
            trace.Add(new SqlOSFgaAccessTrace
            {
                Step = "Access Denied",
                Detail = $"No matching grants found that provide permission \"{permission.Name}\"",
            });
            return new SqlOSFgaAccessCheckResult { Allowed = false, Trace = trace };
        }

        var grant = await _context.Set<SqlOSFgaGrant>()
            .AsNoTracking()
            .Where(g => g.Id == match.GrantId)
            .Select(g => new
            {
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                SubjectName = g.Subject != null ? g.Subject.DisplayName : g.SubjectId,
                ResourceName = g.Resource != null ? g.Resource.Name : g.ResourceId,
            })
            .FirstOrDefaultAsync();
        var roleName = grant?.RoleName ?? match.RoleId;
        var grantedAt = grant?.ResourceName ?? match.Id;
        trace.Add(new SqlOSFgaAccessTrace
        {
            Step = "Access Granted",
            Detail = $"Role \"{roleName}\" provides permission \"{permission.Name}\" via grant at {grantedAt}",
            ResourceId = match.Id,
            ResourceName = grantedAt,
            GrantId = match.GrantId,
            RoleName = roleName,
            SubjectName = grant?.SubjectName ?? match.SubjectId,
        });
        return new SqlOSFgaAccessCheckResult { Allowed = true, Trace = trace };
    }

    public async Task<SqlOSFgaResourceAccessTrace> TraceResourceAccessAsync(
        string subjectId,
        string resourceId,
        string permissionKey)
    {
        EnsureRelational();
        var trace = new SqlOSFgaResourceAccessTrace
        {
            SubjectId = subjectId,
            PermissionKey = permissionKey
        };

        var resource = await _context.Set<SqlOSFgaResource>()
            .AsNoTracking()
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

        var subject = await _context.Set<SqlOSFgaSubject>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == subjectId);
        if (subject == null)
        {
            trace.AccessGranted = false;
            trace.DenialReason = $"Subject '{subjectId}' not found";
            trace.DecisionSummary = $"Access denied because the subject '{subjectId}' does not exist.";
            return trace;
        }

        trace.SubjectDisplayName = subject.DisplayName;

        var permission = await _context.Set<SqlOSFgaPermission>().AsNoTracking().FirstOrDefaultAsync(p => p.Key == permissionKey);
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

        // The decision is the point check's (fn_IsResourceAccessible). The rest explains it: the active path
        // from the target's lineage (the resources a grant can reach the target from), and the caller's grants
        // on that path.
        var match = subjectIds.Count == 0 ? null : await FindDecidingGrantAsync(resourceId, subjectIds, permission.Id);
        var provider = SqlOSDatabase.Resolve(_context.Database);
        var path = await _context.Set<SqlOSFgaPathNode>()
            .FromSqlRaw(provider.BuildResourcePathQuerySql(_options), resourceId)
            .AsNoTracking()
            .ToListAsync();
        var activePath = path.Where(n => n.InReach).OrderBy(n => n.Level).ToList();

        var typeIds = activePath.Select(n => n.ResourceTypeId).Distinct().ToList();
        var typeNames = await _context.Set<SqlOSFgaResourceType>()
            .AsNoTracking()
            .Where(rt => typeIds.Contains(rt.Id))
            .ToDictionaryAsync(rt => rt.Id, rt => rt.Name);

        var nodeIds = activePath.Select(n => n.ResourceId).ToList();
        var now = DateTime.UtcNow;
        var grants = subjectIds.Count == 0 || nodeIds.Count == 0
            ? []
            : await _context.Set<SqlOSFgaGrant>()
                .AsNoTracking()
                .Include(g => g.Role)
                .Include(g => g.Subject)
                .Where(g => nodeIds.Contains(g.ResourceId) &&
                            subjectIds.Contains(g.SubjectId) &&
                            (g.EffectiveFrom == null || g.EffectiveFrom <= now) &&
                            (g.EffectiveTo == null || g.EffectiveTo >= now))
                .ToListAsync();
        var roleIds = grants.Select(g => g.RoleId).Distinct().ToList();
        var rolePermissions = roleIds.Count == 0
            ? []
            : await _context.Set<SqlOSFgaRolePermission>()
                .AsNoTracking()
                .Include(rp => rp.Permission)
                .Where(rp => roleIds.Contains(rp.RoleId))
                .ToListAsync();

        var pathNodes = new List<SqlOSFgaResourcePathNodeTrace>();
        var allGrantsUsed = new List<SqlOSFgaGrantTrace>();
        var allRolesUsed = new Dictionary<string, SqlOSFgaRoleTrace>();
        foreach (var node in activePath)
        {
            var pathNode = new SqlOSFgaResourcePathNodeTrace
            {
                ResourceId = node.ResourceId,
                Name = node.Name,
                ResourceType = typeNames.GetValueOrDefault(node.ResourceTypeId) ?? node.ResourceTypeId,
                Depth = pathNodes.Count,
                IsTarget = node.ResourceId == resourceId,
                PermissionFoundHere = match != null && node.ResourceId == match.Id,
            };

            foreach (var grant in grants.Where(g => g.ResourceId == node.ResourceId))
            {
                var role = grant.Role;
                if (role == null) continue;

                var roleGrants = rolePermissions.Where(rp => rp.RoleId == role.Id).ToList();
                var decided = match != null && grant.Id == match.GrantId;
                var subjectInfo = allSubjects.FirstOrDefault(s => s.SubjectId == grant.SubjectId);
                var isDirect = subjectInfo?.IsDirect ?? false;
                var grantTrace = new SqlOSFgaGrantTrace
                {
                    GrantId = grant.Id,
                    ResourceId = node.ResourceId,
                    ResourceName = node.Name,
                    ResourceType = pathNode.ResourceType,
                    RoleKey = role.Key,
                    RoleName = role.Name,
                    SubjectId = grant.SubjectId,
                    SubjectDisplayName = grant.Subject?.DisplayName ?? grant.SubjectId,
                    AppliesToSubject = true,
                    IsDirectGrant = isDirect,
                    ViaGroupName = !isDirect ? subjectInfo?.DisplayName : null,
                    ContributedToDecision = decided
                };

                pathNode.GrantsOnThisNode.Add(grantTrace);
                allGrantsUsed.Add(grantTrace);

                if (!allRolesUsed.TryGetValue(role.Id, out var roleTrace))
                {
                    roleTrace = new SqlOSFgaRoleTrace
                    {
                        RoleKey = role.Key,
                        RoleName = role.Name,
                        IsFromGrant = true,
                        IsVirtualRole = role.IsVirtual,
                        SourceResourceId = node.ResourceId,
                        SourceResourceName = node.Name,
                        SourceResourceType = pathNode.ResourceType,
                        Permissions = roleGrants.Select(rp => new SqlOSFgaPermissionAssignmentTrace
                        {
                            PermissionKey = rp.Permission?.Key ?? "",
                            PermissionName = rp.Permission?.Name ?? "",
                            UsedForDecision = rp.PermissionId == permission.Id
                        }).ToList()
                    };
                    allRolesUsed[role.Id] = roleTrace;
                }

                roleTrace.ContributedToDecision |= decided;

                foreach (var rp in roleGrants)
                {
                    if (rp.Permission != null && !pathNode.EffectivePermissions.Contains(rp.Permission.Key))
                    {
                        pathNode.EffectivePermissions.Add(rp.Permission.Key);
                    }
                }
            }

            pathNodes.Add(pathNode);
        }

        trace.PathNodes = pathNodes;
        trace.AllRolesUsed = allRolesUsed.Values.ToList();
        trace.GrantsUsed = allGrantsUsed;
        trace.AccessGranted = match != null;

        if (match != null)
        {
            var decidingGrant = allGrantsUsed.FirstOrDefault(g => g.GrantId == match.GrantId);
            var grantingNodeName = decidingGrant?.ResourceName ?? match.Id;
            var grantingRoleName = decidingGrant?.RoleName ?? match.RoleId;
            if (decidingGrant is { IsDirectGrant: false, ViaGroupName: not null })
            {
                trace.DecisionSummary = $"Access granted via group '{decidingGrant.ViaGroupName}' which has role '{grantingRoleName}' on '{grantingNodeName}'. " +
                                       $"The role '{grantingRoleName}' includes permission '{permissionKey}' which is inherited by child resources.";
            }
            else if (match.Id == resourceId)
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
        else if (path.Count == 0)
        {
            trace.DenialReason = "The resource has no place in the hierarchy SqlOS can use: it lies in a cycle, or deeper than the configured maximum depth.";
            trace.DecisionSummary = $"Access denied. '{trace.TargetResourceName}' is denied to everyone until its place in the hierarchy is repaired.";
        }
        else if (activePath.Count == 0)
        {
            trace.DenialReason = $"'{trace.TargetResourceName}' is inactive.";
            trace.DecisionSummary = $"Access denied. '{trace.TargetResourceName}' is inactive, so no grant reaches it.";
        }
        else if (allGrantsUsed.Count == 0)
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

        return trace;
    }

    public async Task<Expression<Func<T, bool>>> BuildFilterAsync<T>(
        string subjectId,
        string permissionKey) where T : IHasResourceId
    {
        EnsureRelational();
        EnsureProtectedEntity<T>();
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
        // with a role that includes the permission. The filter compares each row's scope at a root's level with
        // the root. Grants are read when the filter is built, as group membership is; build the filter per
        // request.
        var provider = SqlOSDatabase.Resolve(_context.Database);
        var subjectIdsJson = JsonSerializer.Serialize(subjectIds);
        var roots = await _context.Set<SqlOSFgaAccessRoot>()
            .FromSqlRaw(provider.BuildAccessRootsQuerySql(_options), subjectIdsJson, permission.Id)
            .AsNoTracking()
            .ToListAsync();
        if (roots.Count == 0)
        {
            return entity => false;
        }

        return SqlOSFgaFilterBuilder.Build<T>(roots, subjectIdsJson, permission.TypeSeq, SqlOSFgaLineage.Levels(_options));
    }

    /// <summary>The grant that decides a point check (fn_IsResourceAccessible), or null when it is denied.</summary>
    private Task<SqlOSFgaAccessMatch?> FindDecidingGrantAsync(string resourceId, IReadOnlyList<string> subjectIds, string permissionId)
        => _context.Set<SqlOSFgaAccessMatch>()
            .FromSqlRaw(
                SqlOSDatabase.Resolve(_context.Database).BuildAccessMatchQuerySql(_options),
                resourceId,
                JsonSerializer.Serialize(subjectIds),
                permissionId)
            .AsNoTracking()
            .FirstOrDefaultAsync();

    /// <summary>Authorization runs in the database; an in-memory provider cannot run it.</summary>
    private void EnsureRelational()
    {
        if (!_context.Database.IsRelational())
        {
            throw new InvalidOperationException(
                "SqlOS authorization checks and list filters run in the database. Use SQL Server or PostgreSQL; "
                + "an in-memory EF Core provider cannot run them.");
        }
    }

    /// <summary>
    /// A filter is only correct on a table SqlOS keeps the scope column of: an entity of the context registered
    /// with SqlOS. Say so instead of failing inside EF Core, or returning rows from a table nobody maintains.
    /// </summary>
    private void EnsureProtectedEntity<T>()
    {
        var model = (_context as DbContext)?.Model;
        var entityType = model?.FindEntityType(typeof(T));
        if (model != null && entityType?.FindProperty(SqlOSFgaLineage.ScopeColumn) == null)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is not a protected entity of {_context.GetType().Name}, the DbContext registered with SqlOS. "
                + "Map it in that context, so SqlOS keeps its FgaScope column current; list filters only work on its entities.");
        }
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
}
