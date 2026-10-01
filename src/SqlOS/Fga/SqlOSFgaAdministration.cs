using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Processes;
using SqlOS.Pagination;

namespace SqlOS.Fga;

/// <summary>
/// What the FGA dashboard API reads and what an operator changes through it. The middleware parses
/// the request, calls one member and writes its answer; it never sees the database.
/// </summary>
internal sealed class SqlOSFgaAdministration(IServiceProvider services, ISqlOSFgaDbContext context)
{
    private const int DefaultPageSize = 25;
    private const int MaxAncestorTraversalDepth = 50;

    public static SqlOSFgaAdministration For(IServiceProvider services)
        => new(services, services.GetRequiredService<ISqlOSFgaDbContext>());

    // --- Operator writes and the trace ---

    public Task<SqlOSFgaResourceAccessTrace> TraceAsync(string subjectId, string resourceId, string permissionKey)
        => services.GetRequiredService<ISqlOSFgaAuthService>().TraceResourceAccessAsync(subjectId, resourceId, permissionKey);

    /// <summary>Grants as the operator; a granted grant comes back as the dashboard lists it.</summary>
    public async Task<(GrantFgaRoleOutcome Outcome, object? Grant)> GrantAsync(
        GrantFgaRoleCommand command,
        GrantAuthority authority,
        CancellationToken cancellationToken)
    {
        var outcome = await new GrantFgaRole(context).ExecuteAsync(command, authority, cancellationToken);
        if (outcome is not GrantFgaRoleOutcome.Granted granted)
        {
            return (outcome, null);
        }

        var grantId = granted.Grant.Id;
        var created = await context.Set<SqlOSFgaGrant>()
            .Where(g => g.Id == grantId)
            .Select(g => new
            {
                g.Id,
                SubjectName = g.Subject != null ? g.Subject.DisplayName : g.SubjectId,
                g.SubjectId,
                ResourceName = g.Resource != null ? g.Resource.Name : g.ResourceId,
                g.ResourceId,
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                g.RoleId,
                g.EffectiveFrom,
                g.EffectiveTo,
                g.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
        return (outcome, created);
    }

    public Task<bool> RevokeGrantAsync(string grantId, CancellationToken cancellationToken)
        => new RevokeFgaGrant(context).ExecuteAsync(grantId, FgaActor.Operator, cancellationToken);

    public Task<DeleteFgaSubjectOutcome> DeleteSubjectAsync(string subjectId, CancellationToken cancellationToken)
        => new DeleteFgaSubject(context).ExecuteAsync(subjectId, FgaActor.Operator, cancellationToken);

    // --- The authorization model ---

    public async Task<object> GetRolePermissionsAsync(string roleId, CancellationToken cancellationToken)
        => await context.Set<SqlOSFgaRolePermission>()
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => new
            {
                rp.Permission!.Id,
                rp.Permission.Key,
                rp.Permission.Name,
                rp.Permission.Description
            })
            .ToListAsync(cancellationToken);

    public async Task<object?> GetRoleAsync(string roleId, CancellationToken cancellationToken)
        => await context.Set<SqlOSFgaRole>()
            .Where(r => r.Id == roleId)
            .Select(r => new
            {
                r.Id,
                r.Key,
                r.Name,
                r.Description,
                r.IsVirtual,
                PermissionCount = r.RolePermissions.Count
            })
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<object> GetRolesAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var roles = context.Set<SqlOSFgaRole>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            roles = roles.Where(r => r.Name.Contains(search) || r.Key.Contains(search));
        }

        var query = roles.Select(r => new RoleListRow
        {
            Id = r.Id,
            Key = r.Key,
            Name = r.Name,
            Description = r.Description,
            IsVirtual = r.IsVirtual,
            PermissionCount = r.RolePermissions.Count
        });
        return (await PageAsync(query, SqlOSKeyset<RoleListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id), "fga.roles", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetPermissionsAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var permissions = context.Set<SqlOSFgaPermission>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            permissions = permissions.Where(p => p.Key.Contains(search) || p.Name.Contains(search));
        }

        var query = permissions.Select(p => new PermissionListRow
        {
            Id = p.Id,
            Key = p.Key,
            Name = p.Name,
            Description = p.Description,
            ResourceType = p.ResourceType != null ? p.ResourceType.Name : null
        });
        return (await PageAsync(query, SqlOSKeyset<PermissionListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id), "fga.permissions", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetResourceTypesAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var types = context.Set<SqlOSFgaResourceType>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            types = types.Where(rt => rt.Name.Contains(search) || rt.Id.Contains(search));
        }

        var query = types.Select(rt => new ResourceTypeListRow
        {
            Id = rt.Id,
            Key = rt.Id,
            Name = rt.Name,
            Description = rt.Description
        });
        return (await PageAsync(query, SqlOSKeyset<ResourceTypeListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id), "fga.resource-types", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetStatsAsync(CancellationToken cancellationToken)
    {
        async Task<int> CountAsync<TEntity>()
            where TEntity : class
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>().Set<TEntity>().CountAsync(cancellationToken);
        }

        var counts = await Task.WhenAll(
            CountAsync<SqlOSFgaResource>(),
            CountAsync<SqlOSFgaSubject>(),
            CountAsync<SqlOSFgaUser>(),
            CountAsync<SqlOSFgaAgent>(),
            CountAsync<SqlOSFgaServiceAccount>(),
            CountAsync<SqlOSFgaUserGroup>(),
            CountAsync<SqlOSFgaGrant>(),
            CountAsync<SqlOSFgaRole>(),
            CountAsync<SqlOSFgaPermission>());

        return new
        {
            Resources = counts[0],
            Subjects = counts[1],
            Users = counts[2],
            Agents = counts[3],
            ServiceAccounts = counts[4],
            UserGroups = counts[5],
            Grants = counts[6],
            Roles = counts[7],
            Permissions = counts[8]
        };
    }

    // --- Resources (top-level roots, then children on expand) ---

    public async Task<object> GetResourceTreeAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var resources = context.Set<SqlOSFgaResource>().Where(r => r.ParentId == null && r.IsActive);
        if (!string.IsNullOrEmpty(search))
        {
            resources = resources.Where(r => r.Name.Contains(search));
        }

        var rows = await PageAsync(ResourceRows(resources), ResourceKeyset, "fga.resource-tree", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken);
        var counts = await ResourcePageCountsAsync(rows.Data.Select(x => x.Id).ToList(), cancellationToken);
        return rows.ToResponse(r => new
        {
            r.Id,
            r.ParentId,
            r.Name,
            r.ResourceType,
            ChildCount = counts.ChildCounts.GetValueOrDefault(r.Id),
            GrantsCount = counts.GrantCounts.GetValueOrDefault(r.Id)
        });
    }

    /// <summary>The flat searchable list for the access tester and grant pickers (any depth).</summary>
    public async Task<object> GetResourcesAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var resources = context.Set<SqlOSFgaResource>().Where(r => r.IsActive);
        if (!string.IsNullOrEmpty(search))
        {
            resources = resources.Where(r => r.Name.Contains(search) || r.Id.Contains(search));
        }

        return (await PageAsync(ResourceRows(resources), ResourceKeyset, "fga.resources", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetResourceChildrenAsync(string parentId, string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var resources = context.Set<SqlOSFgaResource>().Where(r => r.ParentId == parentId && r.IsActive);
        if (!string.IsNullOrEmpty(search))
        {
            resources = resources.Where(r => r.Name.Contains(search));
        }

        var rows = await PageAsync(ResourceRows(resources), ResourceKeyset, "fga.resource-children", SqlOSCursorCodec.Fingerprint(parentId, search), page, cancellationToken);
        var counts = await ResourcePageCountsAsync(rows.Data.Select(x => x.Id).ToList(), cancellationToken);
        return new
        {
            Data = rows.Data.Select(r => new
            {
                r.Id,
                r.ParentId,
                r.Name,
                r.ResourceType,
                ChildCount = counts.ChildCounts.GetValueOrDefault(r.Id),
                GrantsCount = counts.GrantCounts.GetValueOrDefault(r.Id)
            }).ToList(),
            rows.PageSize,
            rows.NextCursor,
            rows.HasNextPage,
            ParentId = parentId
        };
    }

    /// <summary>A resource with its breadcrumb path from the top, or null.</summary>
    public async Task<object?> GetResourceAsync(string resourceId, CancellationToken cancellationToken)
    {
        var resource = await context.Set<SqlOSFgaResource>()
            .Where(r => r.Id == resourceId)
            .Select(r => new
            {
                r.Id,
                r.ParentId,
                r.Name,
                r.Description,
                ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId,
                r.ResourceTypeId,
                r.IsActive,
                r.CreatedAt,
                r.UpdatedAt,
                ChildCount = context.Set<SqlOSFgaResource>().Count(c => c.ParentId == r.Id && c.IsActive),
                GrantsCount = context.Set<SqlOSFgaGrant>().Count(g => g.ResourceId == r.Id)
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (resource == null)
        {
            return null;
        }

        var breadcrumbs = new List<object>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { resource.Id };
        var currentId = resource.ParentId;
        for (var depth = 0; !string.IsNullOrEmpty(currentId) && depth <= MaxAncestorTraversalDepth && visited.Add(currentId); depth++)
        {
            var id = currentId;
            var parent = await context.Set<SqlOSFgaResource>()
                .Where(r => r.Id == id)
                .Select(r => new { r.Id, r.Name, r.ParentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (parent == null)
            {
                break;
            }

            breadcrumbs.Insert(0, new { parent.Id, parent.Name });
            currentId = parent.ParentId;
        }

        return new { Resource = resource, Breadcrumbs = breadcrumbs };
    }

    /// <summary>The grants on a resource and on each ancestor, which it inherits, or null.</summary>
    public async Task<object?> GetResourceAccessAsync(string resourceId, CancellationToken cancellationToken)
    {
        var resource = await context.Set<SqlOSFgaResource>().Where(r => r.Id == resourceId).Select(r => new { r.Id, r.ParentId }).FirstOrDefaultAsync(cancellationToken);
        if (resource == null)
        {
            return null;
        }

        var ancestorIds = new List<string> { resourceId };
        var visited = new HashSet<string>(StringComparer.Ordinal) { resourceId };
        var currentId = resource.ParentId;
        for (var depth = 0; !string.IsNullOrEmpty(currentId) && depth <= MaxAncestorTraversalDepth && visited.Add(currentId); depth++)
        {
            ancestorIds.Add(currentId);
            var id = currentId;
            currentId = await context.Set<SqlOSFgaResource>().Where(r => r.Id == id).Select(r => r.ParentId).FirstOrDefaultAsync(cancellationToken);
        }

        return await context.Set<SqlOSFgaGrant>()
            .Where(g => ancestorIds.Contains(g.ResourceId))
            .Select(g => new
            {
                SubjectId = g.SubjectId,
                SubjectName = g.Subject != null ? g.Subject.DisplayName : g.SubjectId,
                RoleId = g.RoleId,
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                SourceResourceId = g.ResourceId,
                SourceResourceName = g.Resource != null ? g.Resource.Name : g.ResourceId,
                IsInherited = g.ResourceId != resourceId
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>The grants made directly on a resource (the tree's hover popup).</summary>
    public async Task<object> GetResourceGrantsAsync(string resourceId, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var query = context.Set<SqlOSFgaGrant>()
            .Where(g => g.ResourceId == resourceId)
            .Select(g => new ResourceGrantRow
            {
                Id = g.Id,
                SubjectId = g.SubjectId,
                SubjectName = g.Subject != null ? g.Subject.DisplayName : g.SubjectId,
                SubjectType = g.Subject != null && g.Subject.SubjectType != null ? g.Subject.SubjectType.Name : null,
                RoleId = g.RoleId,
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                EffectiveFrom = g.EffectiveFrom,
                EffectiveTo = g.EffectiveTo,
                CreatedAt = g.CreatedAt
            });
        var rows = await PageAsync(query, SqlOSKeyset<ResourceGrantRow>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id), "fga.resource-grants", SqlOSCursorCodec.Fingerprint(resourceId), page, cancellationToken);
        return rows.ToResponse(g => new
        {
            g.Id,
            g.SubjectId,
            g.SubjectName,
            g.SubjectType,
            g.RoleId,
            g.RoleName,
            g.EffectiveFrom,
            g.EffectiveTo
        });
    }

    // --- Subjects and grants ---

    /// <summary>A subject with the groups it belongs to and, for a group, its members; or null.</summary>
    public async Task<object?> GetSubjectAsync(string subjectId, CancellationToken cancellationToken)
    {
        var subject = await context.Set<SqlOSFgaSubject>()
            .Where(s => s.Id == subjectId)
            .Select(s => new
            {
                s.Id,
                s.DisplayName,
                s.SubjectTypeId,
                SubjectType = s.SubjectType != null ? s.SubjectType.Name : s.SubjectTypeId,
                s.OrganizationId,
                s.ExternalRef,
                s.CreatedAt,
                s.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (subject == null)
        {
            return null;
        }

        var groups = await context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Select(m => new
            {
                m.UserGroup!.Id,
                m.UserGroup.Name,
                m.UserGroup.GroupType,
                m.UserGroup.SubjectId,
                m.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var members = await context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.UserGroup != null && m.UserGroup.SubjectId == subjectId)
            .Select(m => new
            {
                m.Subject!.Id,
                m.Subject.DisplayName,
                m.Subject.SubjectTypeId,
                m.CreatedAt
            })
            .ToListAsync(cancellationToken);
        return new { Subject = subject, Groups = groups, Members = members };
    }

    public async Task<object> GetSubjectGrantsAsync(string subjectId, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var query = context.Set<SqlOSFgaGrant>()
            .Where(g => g.SubjectId == subjectId)
            .Select(g => new SubjectGrantRow
            {
                Id = g.Id,
                ResourceName = g.Resource != null ? g.Resource.Name : g.ResourceId,
                ResourceId = g.ResourceId,
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                RoleId = g.RoleId,
                EffectiveFrom = g.EffectiveFrom,
                EffectiveTo = g.EffectiveTo,
                CreatedAt = g.CreatedAt
            });
        return (await PageAsync(query, SqlOSKeyset<SubjectGrantRow>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id), "fga.subject-grants", SqlOSCursorCodec.Fingerprint(subjectId), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetSubjectsAsync(string? type, string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var subjects = context.Set<SqlOSFgaSubject>().AsQueryable();
        if (!string.IsNullOrEmpty(type))
        {
            subjects = subjects.Where(s => s.SubjectTypeId == type);
        }

        if (!string.IsNullOrEmpty(search))
        {
            subjects = subjects.Where(s => s.DisplayName.Contains(search) || s.Id.Contains(search));
        }

        var query = subjects.Select(s => new SubjectListRow
        {
            Id = s.Id,
            DisplayName = s.DisplayName,
            SubjectTypeId = s.SubjectTypeId,
            SubjectType = s.SubjectType != null ? s.SubjectType.Name : s.SubjectTypeId,
            OrganizationId = s.OrganizationId,
            ExternalRef = s.ExternalRef,
            CreatedAt = s.CreatedAt
        });
        return (await PageAsync(query, SqlOSKeyset<SubjectListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id), "fga.subjects", SqlOSCursorCodec.Fingerprint(type, search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetUsersAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var users = context.Set<SqlOSFgaUser>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            users = users.Where(u =>
                (u.Subject != null && (u.Subject.DisplayName.Contains(search) || u.Subject.Id.Contains(search))) ||
                (u.Email != null && u.Email.Contains(search)));
        }

        var query = users.Select(u => new UserListRow
        {
            Id = u.Id,
            SubjectId = u.SubjectId,
            DisplayName = u.Subject != null ? u.Subject.DisplayName : u.Id,
            Email = u.Email,
            IsActive = u.IsActive,
            LastLoginAt = u.LastLoginAt,
            CreatedAt = u.CreatedAt
        });
        return (await PageAsync(query, SqlOSKeyset<UserListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id), "fga.users", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetAgentsAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var agents = context.Set<SqlOSFgaAgent>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            agents = agents.Where(a =>
                (a.Subject != null && (a.Subject.DisplayName.Contains(search) || a.Subject.Id.Contains(search))) ||
                (a.AgentType != null && a.AgentType.Contains(search)) ||
                (a.Description != null && a.Description.Contains(search)));
        }

        var query = agents.Select(a => new AgentListRow
        {
            Id = a.Id,
            SubjectId = a.SubjectId,
            DisplayName = a.Subject != null ? a.Subject.DisplayName : a.Id,
            AgentType = a.AgentType,
            Description = a.Description,
            LastRunAt = a.LastRunAt,
            CreatedAt = a.CreatedAt
        });
        return (await PageAsync(query, SqlOSKeyset<AgentListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id), "fga.agents", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetServiceAccountsAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var accounts = context.Set<SqlOSFgaServiceAccount>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            accounts = accounts.Where(s =>
                (s.Subject != null && (s.Subject.DisplayName.Contains(search) || s.Subject.Id.Contains(search))) ||
                s.ClientId.Contains(search) ||
                (s.Description != null && s.Description.Contains(search)));
        }

        var query = accounts.Select(s => new ServiceAccountListRow
        {
            Id = s.Id,
            SubjectId = s.SubjectId,
            DisplayName = s.Subject != null ? s.Subject.DisplayName : s.Id,
            ClientId = s.ClientId,
            Description = s.Description,
            LastUsedAt = s.LastUsedAt,
            ExpiresAt = s.ExpiresAt,
            ConfigurationOwner = s.ConfigurationOwner,
            ConfigurationSourceKey = s.ConfigurationSourceKey,
            ConfigurationOrphanedAt = s.ConfigurationOrphanedAt,
            CreatedAt = s.CreatedAt
        });
        return (await PageAsync(query, SqlOSKeyset<ServiceAccountListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id), "fga.service-accounts", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    public async Task<object> GetUserGroupsAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var groups = context.Set<SqlOSFgaUserGroup>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            groups = groups.Where(g =>
                g.Name.Contains(search) ||
                (g.Subject != null && g.Subject.DisplayName.Contains(search)) ||
                (g.Description != null && g.Description.Contains(search)));
        }

        var query = groups.Select(g => new UserGroupListRow
        {
            Id = g.Id,
            SubjectId = g.SubjectId,
            Name = g.Name,
            Description = g.Description,
            GroupType = g.GroupType,
            CreatedAt = g.CreatedAt
        });
        var rows = await PageAsync(query, SqlOSKeyset<UserGroupListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id), "fga.user-groups", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken);
        var groupIds = rows.Data.Select(x => x.Id).ToList();
        var memberCounts = groupIds.Count == 0
            ? new Dictionary<string, int>()
            : await context.Set<SqlOSFgaUserGroupMembership>()
                .Where(m => groupIds.Contains(m.UserGroupId))
                .GroupBy(m => m.UserGroupId)
                .Select(g => new { UserGroupId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserGroupId, x => x.Count, cancellationToken);
        return rows.ToResponse(g => new
        {
            g.Id,
            g.SubjectId,
            g.Name,
            g.Description,
            g.GroupType,
            MemberCount = memberCounts.GetValueOrDefault(g.Id, 0),
            g.CreatedAt
        });
    }

    public async Task<object> GetGrantsAsync(string? search, FgaPageRequest page, CancellationToken cancellationToken)
    {
        var grants = context.Set<SqlOSFgaGrant>().AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            grants = grants.Where(g =>
                (g.Subject != null && g.Subject.DisplayName.Contains(search)) ||
                (g.Resource != null && g.Resource.Name.Contains(search)) ||
                (g.Role != null && g.Role.Name.Contains(search)));
        }

        var query = grants.Select(g => new GrantListRow
        {
            Id = g.Id,
            SubjectName = g.Subject != null ? g.Subject.DisplayName : g.SubjectId,
            SubjectId = g.SubjectId,
            ResourceName = g.Resource != null ? g.Resource.Name : g.ResourceId,
            ResourceId = g.ResourceId,
            RoleName = g.Role != null ? g.Role.Name : g.RoleId,
            RoleId = g.RoleId,
            EffectiveFrom = g.EffectiveFrom,
            EffectiveTo = g.EffectiveTo,
            CreatedAt = g.CreatedAt
        });
        return (await PageAsync(query, SqlOSKeyset<GrantListRow>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id), "fga.grants", SqlOSCursorCodec.Fingerprint(search), page, cancellationToken)).ToResponse();
    }

    // --- Helpers ---

    private static readonly SqlOSKeyset<ResourceTreeRow> ResourceKeyset
        = SqlOSKeyset<ResourceTreeRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id);

    private static IQueryable<ResourceTreeRow> ResourceRows(IQueryable<SqlOSFgaResource> resources)
        => resources.Select(r => new ResourceTreeRow
        {
            Id = r.Id,
            ParentId = r.ParentId,
            Name = r.Name,
            ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId
        });

    private static Task<SqlOSCursorPage<T>> PageAsync<T>(
        IQueryable<T> query,
        SqlOSKeyset<T> keyset,
        string sortKey,
        string filterFingerprint,
        FgaPageRequest page,
        CancellationToken cancellationToken)
        where T : class
    {
        SqlOSCursorPagination.RejectLegacyOffset(page.LegacyPage);
        return SqlOSCursorPagination.ToPageAsync(
            query,
            keyset,
            sortKey,
            filterFingerprint,
            page.Cursor,
            SqlOSCursorPagination.NormalizePageSize(page.PageSize, DefaultPageSize),
            cancellationToken);
    }

    private async Task<(Dictionary<string, int> ChildCounts, Dictionary<string, int> GrantCounts)> ResourcePageCountsAsync(
        IReadOnlyList<string> resourceIds,
        CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0)
        {
            return (new Dictionary<string, int>(), new Dictionary<string, int>());
        }

        var childCounts = await context.Set<SqlOSFgaResource>()
            .Where(c => resourceIds.Contains(c.ParentId!) && c.IsActive)
            .GroupBy(c => c.ParentId!)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var grantCounts = await context.Set<SqlOSFgaGrant>()
            .Where(g => resourceIds.Contains(g.ResourceId))
            .GroupBy(g => g.ResourceId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        return (childCounts, grantCounts);
    }

    private sealed class ResourceTreeRow
    {
        public string Id { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ResourceType { get; set; } = string.Empty;
    }

    private sealed class SubjectListRow
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SubjectTypeId { get; set; } = string.Empty;
        public string SubjectType { get; set; } = string.Empty;
        public string? OrganizationId { get; set; }
        public string? ExternalRef { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class UserListRow
    {
        public string Id { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool IsActive { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class AgentListRow
    {
        public string Id { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? AgentType { get; set; }
        public string? Description { get; set; }
        public DateTime? LastRunAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ServiceAccountListRow
    {
        public string Id { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string ConfigurationOwner { get; set; } = string.Empty;
        public string? ConfigurationSourceKey { get; set; }
        public DateTime? ConfigurationOrphanedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class UserGroupListRow
    {
        public string Id { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? GroupType { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class GrantListRow
    {
        public string Id { get; set; } = string.Empty;
        public string SubjectName { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public string RoleId { get; set; } = string.Empty;
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ResourceGrantRow
    {
        public string Id { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string SubjectName { get; set; } = string.Empty;
        public string? SubjectType { get; set; }
        public string RoleId { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class SubjectGrantRow
    {
        public string Id { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public string ResourceId { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
        public string RoleId { get; set; } = string.Empty;
        public DateTime? EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class RoleListRow
    {
        public string Id { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsVirtual { get; set; }
        public int PermissionCount { get; set; }
    }

    private sealed class PermissionListRow
    {
        public string Id { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? ResourceType { get; set; }
    }

    private sealed class ResourceTypeListRow
    {
        public string Id { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}

/// <summary>A dashboard list's paging, as the query string gives it.</summary>
internal sealed record FgaPageRequest(string? Cursor, int? PageSize, int? LegacyPage);
