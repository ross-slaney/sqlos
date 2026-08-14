using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Pagination;
using SqlOS.Security;

namespace SqlOS.Fga.Dashboard;

public class SqlOSFgaDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSDashboardOptions _dashboardOptions;
    private readonly SqlOSDashboardSessionService _sessionService;
    private readonly IFileProvider _fileProvider;
    private readonly SqlOSBrowserSecurityHeaders _securityHeaders;
    private const int DefaultPageSize = 25;
    private const int MaxAncestorTraversalDepth = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SqlOSFgaDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSDashboardOptions dashboardOptions,
        SqlOSDashboardSessionService sessionService,
        IOptions<SqlOSOptions> hostOptions)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _dashboardOptions = dashboardOptions;
        _sessionService = sessionService;
        _securityHeaders = new SqlOSBrowserSecurityHeaders(hostOptions);
        _fileProvider = CreateFileProvider();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (!path.Equals(_pathPrefix, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(_pathPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var relativePath = path[_pathPrefix.Length..].TrimStart('/');
        _securityHeaders.ApplyBaseline(context.Response);
        var isApiRequest = relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase);

        if (!await IsAuthorizedAsync(context))
        {
            await HandleUnauthorizedRequestAsync(context, isApiRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            context.Response.Redirect($"{GetDashboardShellPrefix()}admin/fga/resources", permanent: false);
            return;
        }

        // API endpoints
        if (isApiRequest)
        {
            await HandleApiRequest(context, relativePath[4..]);
            return;
        }

        // Serve static files
        await ServeStaticFile(context, relativePath);
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        if (_sessionService.IsPasswordMode(_dashboardOptions.AuthMode)
            && !_sessionService.IsPasswordConfigured(_dashboardOptions.Password))
        {
            return false;
        }

        return await _sessionService.IsAuthorizedAsync(
            context,
            _isDevelopment,
            _dashboardOptions.AuthMode,
            _dashboardOptions.AuthorizationCallback);
    }

    private async Task HandleUnauthorizedRequestAsync(HttpContext context, bool isApiRequest)
    {
        if (_sessionService.IsPasswordMode(_dashboardOptions.AuthMode))
        {
            if (!_sessionService.IsPasswordConfigured(_dashboardOptions.Password))
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("SqlOS dashboard password mode is enabled but no password was configured.");
                return;
            }

            if (isApiRequest)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.Redirect(BuildLoginRedirectPath(context), permanent: false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static IFileProvider CreateFileProvider()
        => new ManifestEmbeddedFileProvider(typeof(SqlOSFgaDashboardMiddleware).Assembly, "Fga/Dashboard/wwwroot");

    private async Task HandleApiRequest(HttpContext context, string endpoint)
    {
        context.Response.ContentType = "application/json";
        try
        {
            await HandleApiRequestCore(context, endpoint);
        }
        catch (SqlOSCursorException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new { error = ex.Error, message = ex.Message }, JsonOptions));
        }
    }

    private async Task HandleApiRequestCore(HttpContext context, string endpoint)
    {
        using var scope = context.RequestServices.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>();

        // Handle POST trace endpoint
        if (endpoint.Equals("trace", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            var body = await JsonSerializer.DeserializeAsync<TraceRequest>(context.Request.Body, JsonOptions);
            if (body == null || string.IsNullOrEmpty(body.SubjectId) || string.IsNullOrEmpty(body.ResourceId) || string.IsNullOrEmpty(body.PermissionKey))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("{\"error\":\"subjectId, resourceId, and permissionKey are required\"}");
                return;
            }
            var authService = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
            var trace = await authService.TraceResourceAccessAsync(body.SubjectId, body.ResourceId, body.PermissionKey);
            await context.Response.WriteAsync(JsonSerializer.Serialize(trace, JsonOptions));
            return;
        }

        // Handle POST grants
        if (endpoint.Equals("grants", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            await HandleCreateGrant(context, dbContext);
            return;
        }

        // Handle DELETE grants/{id}
        if (endpoint.StartsWith("grants/", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "DELETE")
        {
            var grantId = endpoint[7..]; // extract id after "grants/"
            await HandleDeleteGrant(context, dbContext, grantId);
            return;
        }

        // Handle roles/{id}/permissions (GET list)
        if (endpoint.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/permissions") && !endpoint.Contains("/permissions/"))
        {
            var roleId = endpoint[6..^12]; // extract id between "roles/" and "/permissions"
            if (context.Request.Method == "GET")
            {
                var perms = await dbContext.Set<SqlOSFgaRolePermission>()
                    .Include(rp => rp.Permission)
                    .Where(rp => rp.RoleId == roleId)
                    .Select(rp => new
                    {
                        rp.Permission!.Id,
                        rp.Permission.Key,
                        rp.Permission.Name,
                        rp.Permission.Description
                    })
                    .ToListAsync();
                await context.Response.WriteAsync(JsonSerializer.Serialize(perms, JsonOptions));
                return;
            }
            if (context.Request.Method == "POST")
            {
                await HandleAddRolePermission(context, dbContext, roleId);
                return;
            }
        }

        // Handle roles/{id}/permissions/{permId} (DELETE)
        if (endpoint.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "DELETE")
        {
            var parts = endpoint[6..].Split('/'); // after "roles/"
            if (parts.Length == 3 && parts[1].Equals("permissions", StringComparison.OrdinalIgnoreCase))
            {
                var roleId = parts[0];
                var permId = parts[2];
                await HandleRemoveRolePermission(context, dbContext, roleId, permId);
                return;
            }
        }

        // Handle roles (POST) and roles/{id} (GET, PUT, DELETE)
        if (endpoint.Equals("roles", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            await HandleCreateRole(context, dbContext);
            return;
        }
        if (endpoint.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) && !endpoint[6..].Contains('/'))
        {
            var roleId = endpoint[6..];
            if (context.Request.Method == "GET")
            {
                await HandleGetRoleDetail(context, dbContext, roleId);
                return;
            }
            if (context.Request.Method == "PUT")
            {
                await HandleUpdateRole(context, dbContext, roleId);
                return;
            }
            if (context.Request.Method == "DELETE")
            {
                await HandleDeleteRole(context, dbContext, roleId);
                return;
            }
        }

        // Handle POST permissions
        if (endpoint.Equals("permissions", StringComparison.OrdinalIgnoreCase) && context.Request.Method == "POST")
        {
            await HandleCreatePermission(context, dbContext);
            return;
        }

        // Handle resources/{parentId}/children
        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/children"))
        {
            var parentId = endpoint[10..^9]; // extract id between "resources/" and "/children"
            await HandleResourceChildren(context, dbContext, parentId);
            return;
        }

        // Handle resources/{id}/access
        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/access") && context.Request.Method == "GET")
        {
            var resourceId = endpoint[10..^7]; // extract id between "resources/" and "/access"
            await HandleResourceAccess(context, dbContext, resourceId);
            return;
        }

        // Handle resources/{id}/grants (direct grants on this resource, paginated - for hover popup)
        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/grants") && context.Request.Method == "GET")
        {
            var resourceId = endpoint[10..^7]; // extract id between "resources/" and "/grants"
            await HandleResourceGrants(context, dbContext, resourceId);
            return;
        }

        // Handle resources/{id} (single resource detail) — exclude "tree" which is handled by the switch
        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && 
            !endpoint[10..].Contains('/') && 
            !endpoint.Equals("resources/tree", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Method == "GET")
        {
            var resourceId = endpoint[10..];
            await HandleResourceDetail(context, dbContext, resourceId);
            return;
        }

        // Handle subjects/{id}/grants
        if (endpoint.StartsWith("subjects/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/grants"))
        {
            var subjectId = endpoint[9..^7]; // extract id between "subjects/" and "/grants"
            await HandleSubjectGrants(context, dbContext, subjectId);
            return;
        }

        // Handle subjects/{id} (single subject detail) — must be after /grants check
        if (endpoint.StartsWith("subjects/", StringComparison.OrdinalIgnoreCase) && !endpoint[9..].Contains('/'))
        {
            var subjectId = endpoint[9..];
            await HandleSubjectDetail(context, dbContext, subjectId);
            return;
        }

        object? result = endpoint.ToLowerInvariant() switch
        {
            "resources/tree" => await GetResourceTreeAsync(dbContext, context),
            "resources" => await GetResourcesAsync(dbContext, context),
            "subjects" => await GetSubjectsAsync(dbContext, context),
            "users" => await GetUsersAsync(dbContext, context),
            "agents" => await GetAgentsAsync(dbContext, context),
            "service-accounts" => await GetServiceAccountsAsync(dbContext, context),
            "user-groups" => await GetUserGroupsAsync(dbContext, context),
            "grants" => await GetGrantsAsync(dbContext, context),
            "roles" => await GetRolesAsync(dbContext, context),
            "permissions" => await GetPermissionsAsync(dbContext, context),
            "resource-types" => await GetResourceTypesAsync(dbContext, context),
            "stats" => await GetStatsAsync(context.RequestServices, context.RequestAborted),
            _ => null
        };

        if (result == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Not found\"}");
            return;
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    // --- Resource Tree (bounded roots only; children load on expand) ---

    private static async Task<object> GetResourceTreeAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var resources = dbContext.Set<SqlOSFgaResource>()
            .Where(r => r.ParentId == null && r.IsActive);

        if (!string.IsNullOrEmpty(search))
            resources = resources.Where(r => r.Name.Contains(search));

        var query = resources.Select(r => new ResourceTreeRow
        {
            Id = r.Id,
            ParentId = r.ParentId,
            Name = r.Name,
            ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<ResourceTreeRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.resource-tree",
            SqlOSCursorCodec.Fingerprint(search),
            context);

        var counts = await GetResourcePageCountsAsync(dbContext, page.Data.Select(x => x.Id).ToList(), context.RequestAborted);
        return page.ToResponse(r => new
        {
            r.Id,
            r.ParentId,
            r.Name,
            r.ResourceType,
            ChildCount = counts.ChildCounts.GetValueOrDefault(r.Id),
            GrantsCount = counts.GrantCounts.GetValueOrDefault(r.Id)
        });
    }

    // Flat searchable list for Access Tester and grant pickers (any depth; no child prefetch).
    private static async Task<object> GetResourcesAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var resources = dbContext.Set<SqlOSFgaResource>()
            .Where(r => r.IsActive);

        if (!string.IsNullOrEmpty(search))
        {
            resources = resources.Where(r => r.Name.Contains(search) || r.Id.Contains(search));
        }

        var query = resources.Select(r => new ResourceTreeRow
        {
            Id = r.Id,
            ParentId = r.ParentId,
            Name = r.Name,
            ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<ResourceTreeRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.resources",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task HandleResourceChildren(
        HttpContext context, ISqlOSFgaDbContext dbContext, string parentId)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var resources = dbContext.Set<SqlOSFgaResource>()
            .Where(r => r.ParentId == parentId && r.IsActive);

        if (!string.IsNullOrEmpty(search))
            resources = resources.Where(r => r.Name.Contains(search));

        var query = resources.Select(r => new ResourceTreeRow
        {
            Id = r.Id,
            ParentId = r.ParentId,
            Name = r.Name,
            ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<ResourceTreeRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.resource-children",
            SqlOSCursorCodec.Fingerprint(parentId, search),
            context);

        var counts = await GetResourcePageCountsAsync(dbContext, page.Data.Select(x => x.Id).ToList(), context.RequestAborted);
        var result = new
        {
            Data = page.Data.Select(r => new
            {
                r.Id,
                r.ParentId,
                r.Name,
                r.ResourceType,
                ChildCount = counts.ChildCounts.GetValueOrDefault(r.Id),
                GrantsCount = counts.GrantCounts.GetValueOrDefault(r.Id)
            }).ToList(),
            page.PageSize,
            page.NextCursor,
            page.HasNextPage,
            ParentId = parentId
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static async Task HandleResourceDetail(HttpContext context, ISqlOSFgaDbContext dbContext, string resourceId)
    {
        var resource = await dbContext.Set<SqlOSFgaResource>()
            .Include(r => r.ResourceType)
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
                ChildCount = dbContext.Set<SqlOSFgaResource>().Count(c => c.ParentId == r.Id && c.IsActive),
                GrantsCount = dbContext.Set<SqlOSFgaGrant>().Count(g => g.ResourceId == r.Id)
            })
            .FirstOrDefaultAsync();

        if (resource == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Resource not found\"}");
            return;
        }

        // Build breadcrumb path from root to this resource
        var breadcrumbs = new List<object>();
        var currentId = resource.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal) { resource.Id };
        for (var depth = 0; !string.IsNullOrEmpty(currentId) && depth <= MaxAncestorTraversalDepth; depth++)
        {
            if (!visited.Add(currentId))
            {
                break;
            }

            var parent = await dbContext.Set<SqlOSFgaResource>()
                .Where(r => r.Id == currentId)
                .Select(r => new { r.Id, r.Name, r.ParentId })
                .FirstOrDefaultAsync();
            if (parent == null) break;
            breadcrumbs.Insert(0, new { parent.Id, parent.Name });
            currentId = parent.ParentId;
        }

        var result = new { Resource = resource, Breadcrumbs = breadcrumbs };
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static async Task HandleResourceAccess(HttpContext context, ISqlOSFgaDbContext dbContext, string resourceId)
    {
        var resource = await dbContext.Set<SqlOSFgaResource>().Where(r => r.Id == resourceId).Select(r => new { r.Id, r.ParentId }).FirstOrDefaultAsync();
        if (resource == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Resource not found\"}");
            return;
        }

        var ancestorIds = new List<string> { resourceId };
        var currentId = resource.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal) { resourceId };
        for (var depth = 0; !string.IsNullOrEmpty(currentId) && depth <= MaxAncestorTraversalDepth; depth++)
        {
            if (!visited.Add(currentId))
            {
                break;
            }

            ancestorIds.Add(currentId);
            var parentId = await dbContext.Set<SqlOSFgaResource>().Where(r => r.Id == currentId).Select(r => r.ParentId).FirstOrDefaultAsync();
            currentId = parentId;
        }

        var grants = await dbContext.Set<SqlOSFgaGrant>()
            .Include(g => g.Subject)
            .Include(g => g.Resource)
            .Include(g => g.Role)
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
            .ToListAsync();

        await context.Response.WriteAsync(JsonSerializer.Serialize(grants, JsonOptions));
    }

    // --- Resource grants (direct grants only, paginated - for hover popup) ---

    private static async Task HandleResourceGrants(HttpContext context, ISqlOSFgaDbContext dbContext, string resourceId)
    {
        var query = dbContext.Set<SqlOSFgaGrant>()
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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<ResourceGrantRow>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "fga.resource-grants",
            SqlOSCursorCodec.Fingerprint(resourceId),
            context);

        await context.Response.WriteAsync(JsonSerializer.Serialize(
            page.ToResponse(g => new
            {
                g.Id,
                g.SubjectId,
                g.SubjectName,
                g.SubjectType,
                g.RoleId,
                g.RoleName,
                g.EffectiveFrom,
                g.EffectiveTo
            }),
            JsonOptions));
    }

    // --- Subject detail ---

    private static async Task HandleSubjectDetail(HttpContext context, ISqlOSFgaDbContext dbContext, string subjectId)
    {
        var subject = await dbContext.Set<SqlOSFgaSubject>()
            .Include(s => s.SubjectType)
            .Where(s => s.Id == subjectId)
            .Select(s => new
            {
                s.Id, s.DisplayName, s.SubjectTypeId,
                SubjectType = s.SubjectType != null ? s.SubjectType.Name : s.SubjectTypeId,
                s.OrganizationId, s.ExternalRef, s.CreatedAt, s.UpdatedAt
            })
            .FirstOrDefaultAsync();

        if (subject == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Subject not found\"}");
            return;
        }

        // Get group memberships (groups this subject belongs to)
        var groups = await dbContext.Set<SqlOSFgaUserGroupMembership>()
            .Include(m => m.UserGroup)
            .Where(m => m.SubjectId == subjectId)
            .Select(m => new
            {
                m.UserGroup!.Id,
                m.UserGroup.Name,
                m.UserGroup.GroupType,
                m.UserGroup.SubjectId,
                m.CreatedAt
            })
            .ToListAsync();

        // If this subject IS a group, get its members
        var members = await dbContext.Set<SqlOSFgaUserGroupMembership>()
            .Include(m => m.Subject)
            .Where(m => m.UserGroup != null && m.UserGroup.SubjectId == subjectId)
            .Select(m => new
            {
                m.Subject!.Id,
                m.Subject.DisplayName,
                m.Subject.SubjectTypeId,
                m.CreatedAt
            })
            .ToListAsync();

        var result = new { Subject = subject, Groups = groups, Members = members };
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static async Task HandleSubjectGrants(
        HttpContext context, ISqlOSFgaDbContext dbContext, string subjectId)
    {
        var query = dbContext.Set<SqlOSFgaGrant>()
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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<SubjectGrantRow>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "fga.subject-grants",
            SqlOSCursorCodec.Fingerprint(subjectId),
            context);

        await context.Response.WriteAsync(JsonSerializer.Serialize(page.ToResponse(), JsonOptions));
    }

    // --- Paginated table endpoints ---

    private static async Task<object> GetSubjectsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var type = context.Request.Query["type"].FirstOrDefault();
        var search = context.Request.Query["search"].FirstOrDefault();

        var subjects = dbContext.Set<SqlOSFgaSubject>().AsQueryable();

        if (!string.IsNullOrEmpty(type))
            subjects = subjects.Where(s => s.SubjectTypeId == type);

        if (!string.IsNullOrEmpty(search))
            subjects = subjects.Where(s => s.DisplayName.Contains(search) || s.Id.Contains(search));

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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<SubjectListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "fga.subjects",
            SqlOSCursorCodec.Fingerprint(type, search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetUsersAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var users = dbContext.Set<SqlOSFgaUser>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            users = users.Where(u =>
                (u.Subject != null && (u.Subject.DisplayName.Contains(search) || u.Subject.Id.Contains(search))) ||
                (u.Email != null && u.Email.Contains(search)));

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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<UserListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "fga.users",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetAgentsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var agents = dbContext.Set<SqlOSFgaAgent>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            agents = agents.Where(a =>
                (a.Subject != null && (a.Subject.DisplayName.Contains(search) || a.Subject.Id.Contains(search))) ||
                (a.AgentType != null && a.AgentType.Contains(search)) ||
                (a.Description != null && a.Description.Contains(search)));

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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<AgentListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "fga.agents",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetServiceAccountsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var accounts = dbContext.Set<SqlOSFgaServiceAccount>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            accounts = accounts.Where(s =>
                (s.Subject != null && (s.Subject.DisplayName.Contains(search) || s.Subject.Id.Contains(search))) ||
                s.ClientId.Contains(search) ||
                (s.Description != null && s.Description.Contains(search)));

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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<ServiceAccountListRow>.Create().Ascending(x => x.DisplayName).ThenAscending(x => x.Id),
            "fga.service-accounts",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetUserGroupsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var groups = dbContext.Set<SqlOSFgaUserGroup>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            groups = groups.Where(g =>
                g.Name.Contains(search) ||
                (g.Subject != null && g.Subject.DisplayName.Contains(search)) ||
                (g.Description != null && g.Description.Contains(search)));

        var query = groups.Select(g => new UserGroupListRow
        {
            Id = g.Id,
            SubjectId = g.SubjectId,
            Name = g.Name,
            Description = g.Description,
            GroupType = g.GroupType,
            CreatedAt = g.CreatedAt
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<UserGroupListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.user-groups",
            SqlOSCursorCodec.Fingerprint(search),
            context);

        var groupIds = page.Data.Select(x => x.Id).ToList();
        Dictionary<string, int> memberCountLookup;
        if (groupIds.Count > 0)
        {
            var memberCounts = await dbContext.Set<SqlOSFgaUserGroupMembership>()
                .Where(m => groupIds.Contains(m.UserGroupId))
                .GroupBy(m => m.UserGroupId)
                .Select(g => new { UserGroupId = g.Key, Count = g.Count() })
                .ToListAsync(context.RequestAborted);
            memberCountLookup = memberCounts.ToDictionary(x => x.UserGroupId, x => x.Count);
        }
        else
        {
            memberCountLookup = new Dictionary<string, int>();
        }

        return page.ToResponse(g => new
        {
            g.Id,
            g.SubjectId,
            g.Name,
            g.Description,
            g.GroupType,
            MemberCount = memberCountLookup.GetValueOrDefault(g.Id, 0),
            g.CreatedAt
        });
    }

    private static async Task<object> GetGrantsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var grants = dbContext.Set<SqlOSFgaGrant>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            grants = grants.Where(g =>
                (g.Subject != null && g.Subject.DisplayName.Contains(search)) ||
                (g.Resource != null && g.Resource.Name.Contains(search)) ||
                (g.Role != null && g.Role.Name.Contains(search)));

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

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<GrantListRow>.Create().Descending(x => x.CreatedAt).ThenDescending(x => x.Id),
            "fga.grants",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetRolesAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var roles = dbContext.Set<SqlOSFgaRole>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            roles = roles.Where(r => r.Name.Contains(search) || r.Key.Contains(search));

        var query = roles.Select(r => new RoleListRow
        {
            Id = r.Id,
            Key = r.Key,
            Name = r.Name,
            Description = r.Description,
            IsVirtual = r.IsVirtual,
            PermissionCount = r.RolePermissions.Count
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<RoleListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.roles",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetPermissionsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var permissions = dbContext.Set<SqlOSFgaPermission>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            permissions = permissions.Where(p => p.Key.Contains(search) || p.Name.Contains(search));

        var query = permissions.Select(p => new PermissionListRow
        {
            Id = p.Id,
            Key = p.Key,
            Name = p.Name,
            Description = p.Description,
            ResourceType = p.ResourceType != null ? p.ResourceType.Name : null
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<PermissionListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.permissions",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetResourceTypesAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var search = context.Request.Query["search"].FirstOrDefault();

        var types = dbContext.Set<SqlOSFgaResourceType>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            types = types.Where(rt => rt.Name.Contains(search) || rt.Id.Contains(search));

        var query = types.Select(rt => new ResourceTypeListRow
        {
            Id = rt.Id,
            Key = rt.Id,
            Name = rt.Name,
            Description = rt.Description
        });

        var page = await ToCursorPageAsync(
            query,
            SqlOSKeyset<ResourceTypeListRow>.Create().Ascending(x => x.Name).ThenAscending(x => x.Id),
            "fga.resource-types",
            SqlOSCursorCodec.Fingerprint(search),
            context);
        return page.ToResponse();
    }

    private static async Task<object> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        async Task<int> CountAsync<TEntity>()
            where TEntity : class
        {
            using var scope = services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>();
            return await dbContext.Set<TEntity>().CountAsync(cancellationToken);
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

    // --- Helpers ---

    private static (string? Cursor, int PageSize) GetCursorParams(HttpContext context)
    {
        SqlOSCursorPagination.RejectLegacyOffset(TryGetIntQuery(context, "page"));
        var pageSize = SqlOSCursorPagination.NormalizePageSize(TryGetIntQuery(context, "pageSize"), DefaultPageSize);
        var cursor = context.Request.Query["cursor"].FirstOrDefault();
        return (cursor, pageSize);
    }

    private static int? TryGetIntQuery(HttpContext context, string name)
    {
        var value = context.Request.Query[name].FirstOrDefault();
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static Task<SqlOSCursorPage<T>> ToCursorPageAsync<T>(
        IQueryable<T> query,
        SqlOSKeyset<T> keyset,
        string sortKey,
        string filterFingerprint,
        HttpContext context)
        where T : class
    {
        var (cursor, pageSize) = GetCursorParams(context);
        return SqlOSCursorPagination.ToPageAsync(
            query,
            keyset,
            sortKey,
            filterFingerprint,
            cursor,
            pageSize,
            context.RequestAborted);
    }

    private static async Task<(Dictionary<string, int> ChildCounts, Dictionary<string, int> GrantCounts)> GetResourcePageCountsAsync(
        ISqlOSFgaDbContext dbContext,
        IReadOnlyList<string> resourceIds,
        CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0)
        {
            return (new Dictionary<string, int>(), new Dictionary<string, int>());
        }

        var childCounts = await dbContext.Set<SqlOSFgaResource>()
            .Where(c => resourceIds.Contains(c.ParentId!) && c.IsActive)
            .GroupBy(c => c.ParentId!)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);

        var grantCounts = await dbContext.Set<SqlOSFgaGrant>()
            .Where(g => resourceIds.Contains(g.ResourceId))
            .GroupBy(g => g.ResourceId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);

        return (childCounts, grantCounts);
    }

    private async Task ServeStaticFile(HttpContext context, string relativePath)
    {
        var fileInfo = _fileProvider.GetFileInfo(relativePath);
        if (!fileInfo.Exists)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var contentType = GetContentType(relativePath);
        context.Response.ContentType = contentType;

        await using var stream = fileInfo.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body);
    }

    private string GetDashboardShellPrefix()
    {
        var suffix = "/admin/fga";
        return _pathPrefix.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? _pathPrefix[..^suffix.Length] + "/"
            : $"{_pathPrefix}/";
    }

    private string BuildLoginRedirectPath(HttpContext context)
    {
        var shellPrefix = GetDashboardShellPrefix().TrimEnd('/');
        var requestedPath = $"{context.Request.Path}{context.Request.QueryString}";
        var encodedNext = Uri.EscapeDataString(requestedPath);
        return $"{shellPrefix}/login?next={encodedNext}";
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream"
    };

    private static async Task HandleCreateGrant(HttpContext context, ISqlOSFgaDbContext dbContext)
    {
        var body = await JsonSerializer.DeserializeAsync<CreateGrantRequest>(context.Request.Body, JsonOptions);
        if (body == null || string.IsNullOrEmpty(body.SubjectId) || string.IsNullOrEmpty(body.RoleId) || string.IsNullOrEmpty(body.ResourceId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"subjectId, roleId, and resourceId are required\"}");
            return;
        }

        var subjectExists = await dbContext.Set<SqlOSFgaSubject>().AnyAsync(s => s.Id == body.SubjectId);
        if (!subjectExists)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Subject not found\"}");
            return;
        }

        var roleExists = await dbContext.Set<SqlOSFgaRole>().AnyAsync(r => r.Id == body.RoleId);
        if (!roleExists)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Role not found\"}");
            return;
        }

        var resourceExists = await dbContext.Set<SqlOSFgaResource>().AnyAsync(r => r.Id == body.ResourceId);
        if (!resourceExists)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Resource not found\"}");
            return;
        }

        var grantId = $"grant_{Guid.NewGuid():N}"[..30];
        var grant = new SqlOSFgaGrant
        {
            Id = grantId,
            SubjectId = body.SubjectId,
            RoleId = body.RoleId,
            ResourceId = body.ResourceId,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.Set<SqlOSFgaGrant>().Add(grant);
        await dbContext.SaveChangesAsync();

        var created = await dbContext.Set<SqlOSFgaGrant>()
            .Include(g => g.Subject)
            .Include(g => g.Resource)
            .Include(g => g.Role)
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
                g.EffectiveFrom, g.EffectiveTo, g.CreatedAt
            })
            .FirstOrDefaultAsync();

        context.Response.StatusCode = 201;
        await context.Response.WriteAsync(JsonSerializer.Serialize(created, JsonOptions));
    }

    private static async Task HandleDeleteGrant(HttpContext context, ISqlOSFgaDbContext dbContext, string grantId)
    {
        var grant = await dbContext.Set<SqlOSFgaGrant>().FirstOrDefaultAsync(g => g.Id == grantId);
        if (grant == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Grant not found\"}");
            return;
        }

        dbContext.Set<SqlOSFgaGrant>().Remove(grant);
        await dbContext.SaveChangesAsync();

        context.Response.StatusCode = 204;
    }

    private static async Task HandleAddRolePermission(HttpContext context, ISqlOSFgaDbContext dbContext, string roleId)
    {
        var body = await JsonSerializer.DeserializeAsync<AddRolePermissionRequest>(context.Request.Body, JsonOptions);
        if (body == null || string.IsNullOrEmpty(body.PermissionId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"permissionId is required\"}");
            return;
        }

        var roleExists = await dbContext.Set<SqlOSFgaRole>().AnyAsync(r => r.Id == roleId);
        if (!roleExists)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Role not found\"}");
            return;
        }

        var permExists = await dbContext.Set<SqlOSFgaPermission>().AnyAsync(p => p.Id == body.PermissionId);
        if (!permExists)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Permission not found\"}");
            return;
        }

        var exists = await dbContext.Set<SqlOSFgaRolePermission>()
            .AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == body.PermissionId);
        if (exists)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsync("{\"error\":\"Permission already in role\"}");
            return;
        }

        dbContext.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission
        {
            RoleId = roleId,
            PermissionId = body.PermissionId
        });
        await dbContext.SaveChangesAsync();

        context.Response.StatusCode = 204;
    }

    private static async Task HandleRemoveRolePermission(HttpContext context, ISqlOSFgaDbContext dbContext, string roleId, string permId)
    {
        var rp = await dbContext.Set<SqlOSFgaRolePermission>()
            .FirstOrDefaultAsync(x => x.RoleId == roleId && x.PermissionId == permId);
        if (rp == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Role-permission link not found\"}");
            return;
        }

        dbContext.Set<SqlOSFgaRolePermission>().Remove(rp);
        await dbContext.SaveChangesAsync();

        context.Response.StatusCode = 204;
    }

    private static async Task HandleGetRoleDetail(HttpContext context, ISqlOSFgaDbContext dbContext, string roleId)
    {
        var role = await dbContext.Set<SqlOSFgaRole>()
            .Include(r => r.RolePermissions)
            .Where(r => r.Id == roleId)
            .Select(r => new
            {
                r.Id, r.Key, r.Name, r.Description, r.IsVirtual,
                PermissionCount = r.RolePermissions.Count
            })
            .FirstOrDefaultAsync();

        if (role == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Role not found\"}");
            return;
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(role, JsonOptions));
    }

    private static async Task HandleCreateRole(HttpContext context, ISqlOSFgaDbContext dbContext)
    {
        var body = await JsonSerializer.DeserializeAsync<CreateRoleRequest>(context.Request.Body, JsonOptions);
        if (body == null || string.IsNullOrWhiteSpace(body.Key) || string.IsNullOrWhiteSpace(body.Name))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"key and name are required\"}");
            return;
        }

        var roleId = $"role_{Guid.NewGuid():N}"[..30];
        var role = new SqlOSFgaRole
        {
            Id = roleId,
            Key = body.Key,
            Name = body.Name,
            Description = body.Description,
            IsVirtual = body.IsVirtual ?? false
        };
        dbContext.Set<SqlOSFgaRole>().Add(role);
        await dbContext.SaveChangesAsync();

        var created = await dbContext.Set<SqlOSFgaRole>()
            .Where(r => r.Id == roleId)
            .Select(r => new { r.Id, r.Key, r.Name, r.Description, r.IsVirtual, PermissionCount = 0 })
            .FirstOrDefaultAsync();

        context.Response.StatusCode = 201;
        await context.Response.WriteAsync(JsonSerializer.Serialize(created, JsonOptions));
    }

    private static async Task HandleUpdateRole(HttpContext context, ISqlOSFgaDbContext dbContext, string roleId)
    {
        var role = await dbContext.Set<SqlOSFgaRole>().FirstOrDefaultAsync(r => r.Id == roleId);
        if (role == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Role not found\"}");
            return;
        }

        var body = await JsonSerializer.DeserializeAsync<UpdateRoleRequest>(context.Request.Body, JsonOptions);
        if (body == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Invalid body\"}");
            return;
        }

        if (body.Name != null) role.Name = body.Name;
        if (body.Description != null) role.Description = body.Description;
        if (body.IsVirtual.HasValue) role.IsVirtual = body.IsVirtual.Value;

        await dbContext.SaveChangesAsync();

        var updated = await dbContext.Set<SqlOSFgaRole>()
            .Where(r => r.Id == roleId)
            .Select(r => new { r.Id, r.Key, r.Name, r.Description, r.IsVirtual, PermissionCount = r.RolePermissions.Count })
            .FirstOrDefaultAsync();

        await context.Response.WriteAsync(JsonSerializer.Serialize(updated, JsonOptions));
    }

    private static async Task HandleDeleteRole(HttpContext context, ISqlOSFgaDbContext dbContext, string roleId)
    {
        var role = await dbContext.Set<SqlOSFgaRole>()
            .Include(r => r.Grants)
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId);
        if (role == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("{\"error\":\"Role not found\"}");
            return;
        }

        if (role.Grants.Count > 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"Cannot delete role with existing grants. Revoke grants first.\"}");
            return;
        }

        dbContext.Set<SqlOSFgaRolePermission>().RemoveRange(role.RolePermissions);
        dbContext.Set<SqlOSFgaRole>().Remove(role);
        await dbContext.SaveChangesAsync();

        context.Response.StatusCode = 204;
    }

    private static async Task HandleCreatePermission(HttpContext context, ISqlOSFgaDbContext dbContext)
    {
        var body = await JsonSerializer.DeserializeAsync<CreatePermissionRequest>(context.Request.Body, JsonOptions);
        if (body == null || string.IsNullOrWhiteSpace(body.Key) || string.IsNullOrWhiteSpace(body.Name))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"key and name are required\"}");
            return;
        }

        var permissionKey = body.Key.Trim();
        if (permissionKey.Length > SqlOSFgaPermission.MaxKeyLength)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"{{\"error\":\"Permission keys cannot exceed {SqlOSFgaPermission.MaxKeyLength} characters.\"}}");
            return;
        }

        if (await dbContext.Set<SqlOSFgaPermission>().AnyAsync(p => p.Key == permissionKey))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync("{\"error\":\"A permission with this key already exists. Permission keys must be unique.\"}");
            return;
        }

        var permId = $"perm_{Guid.NewGuid():N}"[..30];
        var perm = new SqlOSFgaPermission
        {
            Id = permId,
            Key = permissionKey,
            Name = body.Name.Trim(),
            Description = body.Description,
            ResourceTypeId = body.ResourceTypeId
        };
        dbContext.Set<SqlOSFgaPermission>().Add(perm);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is SqlException { Number: 2601 or 2627 })
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync("{\"error\":\"A permission with this key already exists. Permission keys must be unique.\"}");
            return;
        }

        var created = await dbContext.Set<SqlOSFgaPermission>()
            .Include(p => p.ResourceType)
            .Where(p => p.Id == permId)
            .Select(p => new { p.Id, p.Key, p.Name, p.Description, ResourceType = p.ResourceType != null ? p.ResourceType.Name : (string?)null })
            .FirstOrDefaultAsync();

        context.Response.StatusCode = 201;
        await context.Response.WriteAsync(JsonSerializer.Serialize(created, JsonOptions));
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

    private record TraceRequest(string SubjectId, string ResourceId, string PermissionKey);
    private record CreateGrantRequest(string SubjectId, string RoleId, string ResourceId, DateTime? EffectiveFrom, DateTime? EffectiveTo);
    private record AddRolePermissionRequest(string PermissionId);
    private record CreateRoleRequest(string Key, string Name, string? Description, bool? IsVirtual);
    private record UpdateRoleRequest(string? Name, string? Description, bool? IsVirtual);
    private record CreatePermissionRequest(string Key, string Name, string? Description, string? ResourceTypeId);
}
