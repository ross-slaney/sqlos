using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Dashboard;

public class SqlOSFgaDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSDashboardOptions _dashboardOptions;
    private readonly SqlOSDashboardSessionService _sessionService;
    private readonly IFileProvider _fileProvider;
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
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
        SqlOSDashboardSessionService sessionService)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _dashboardOptions = dashboardOptions;
        _sessionService = sessionService;
        _fileProvider = CreateFileProvider();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (!path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var relativePath = path[_pathPrefix.Length..].TrimStart('/');
        var isApiRequest = relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase);

        if (!await IsAuthorizedAsync(context))
        {
            await HandleUnauthorizedRequestAsync(context, isApiRequest);
            return;
        }

        var embedMode = string.Equals(context.Request.Query["embed"], "1", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(relativePath) && !embedMode)
        {
            context.Response.Redirect($"{GetDashboardShellPrefix()}admin/fga/resources", permanent: false);
            return;
        }

        // Redirect /sqlzibar to /sqlzibar/ so relative paths (style.css, app.js) resolve correctly
        if (string.IsNullOrEmpty(relativePath) && !path.EndsWith('/'))
        {
            context.Response.Redirect($"{_pathPrefix}/", permanent: false);
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
        using var scope = context.RequestServices.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>();

        context.Response.ContentType = "application/json";

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
            "subjects" => await GetSubjectsAsync(dbContext, context),
            "users" => await GetUsersAsync(dbContext, context),
            "agents" => await GetAgentsAsync(dbContext, context),
            "service-accounts" => await GetServiceAccountsAsync(dbContext, context),
            "user-groups" => await GetUserGroupsAsync(dbContext, context),
            "grants" => await GetGrantsAsync(dbContext, context),
            "roles" => await GetRolesAsync(dbContext, context),
            "permissions" => await GetPermissionsAsync(dbContext, context),
            "resource-types" => await GetResourceTypesAsync(dbContext, context),
            "stats" => new
            {
                Resources = await dbContext.Set<SqlOSFgaResource>().CountAsync(),
                Subjects = await dbContext.Set<SqlOSFgaSubject>().CountAsync(),
                Users = await dbContext.Set<SqlOSFgaUser>().CountAsync(),
                Agents = await dbContext.Set<SqlOSFgaAgent>().CountAsync(),
                ServiceAccounts = await dbContext.Set<SqlOSFgaServiceAccount>().CountAsync(),
                UserGroups = await dbContext.Set<SqlOSFgaUserGroup>().CountAsync(),
                Grants = await dbContext.Set<SqlOSFgaGrant>().CountAsync(),
                Roles = await dbContext.Set<SqlOSFgaRole>().CountAsync(),
                Permissions = await dbContext.Set<SqlOSFgaPermission>().CountAsync(),
            },
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

    // --- Resource Tree (breadth-first initial load) ---

    private static async Task<object> GetResourceTreeAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var maxDepth = GetIntParam(context, "maxDepth", 2);
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        var allResources = dbContext.Set<SqlOSFgaResource>()
            .Include(r => r.ResourceType)
            .Where(r => r.IsActive);

        // Find root nodes (no parent)
        var rootIds = await allResources
            .Where(r => r.ParentId == null)
            .OrderBy(r => r.Name)
            .Select(r => r.Id)
            .ToListAsync();

        var nodes = new List<object>();
        var currentLevelIds = rootIds;

        for (int depth = 0; depth <= maxDepth && currentLevelIds.Count > 0; depth++)
        {
            var levelNodes = await allResources
                .Where(r => currentLevelIds.Contains(r.Id))
                .OrderBy(r => r.Name)
                .Select(r => new
                {
                    r.Id,
                    r.ParentId,
                    r.Name,
                    ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId,
                    ChildCount = dbContext.Set<SqlOSFgaResource>().Count(c => c.ParentId == r.Id && c.IsActive),
                    GrantsCount = dbContext.Set<SqlOSFgaGrant>().Count(g => g.ResourceId == r.Id)
                })
                .ToListAsync();

            nodes.AddRange(levelNodes.Cast<object>());

            // Get next level IDs
            if (depth < maxDepth)
            {
                currentLevelIds = await allResources
                    .Where(r => currentLevelIds.Contains(r.ParentId!))
                    .Select(r => r.Id)
                    .ToListAsync();
            }
            else
            {
                currentLevelIds = [];
            }
        }

        return new { Nodes = nodes, RootIds = rootIds, LoadedDepth = maxDepth };
    }

    private static async Task HandleResourceChildren(
        HttpContext context, ISqlOSFgaDbContext dbContext, string parentId)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaResource>()
            .Include(r => r.ResourceType)
            .Where(r => r.ParentId == parentId && r.IsActive);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(r => r.Name.Contains(search));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(r => r.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.ParentId,
                r.Name,
                ResourceType = r.ResourceType != null ? r.ResourceType.Name : r.ResourceTypeId,
                ChildCount = dbContext.Set<SqlOSFgaResource>().Count(c => c.ParentId == r.Id && c.IsActive),
                GrantsCount = dbContext.Set<SqlOSFgaGrant>().Count(g => g.ResourceId == r.Id)
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        var result = new
        {
            Data = data,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasNextPage = page < totalPages,
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
        while (!string.IsNullOrEmpty(currentId))
        {
            var parent = await dbContext.Set<SqlOSFgaResource>()
                .Where(r => r.Id == currentId)
                .Select(r => new { r.Id, r.Name })
                .FirstOrDefaultAsync();
            if (parent == null) break;
            breadcrumbs.Insert(0, parent);
            var res = await dbContext.Set<SqlOSFgaResource>().Where(r => r.Id == currentId).Select(r => r.ParentId).FirstOrDefaultAsync();
            currentId = res;
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
        while (!string.IsNullOrEmpty(currentId))
        {
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
        var page = GetIntParam(context, "page", 1);
        var pageSize = Math.Clamp(GetIntParam(context, "pageSize", 10), 1, 50);

        var query = dbContext.Set<SqlOSFgaGrant>()
            .Include(g => g.Subject).ThenInclude(s => s!.SubjectType)
            .Include(g => g.Role)
            .Where(g => g.ResourceId == resourceId);

        var total = await query.CountAsync();
        var grants = await query
            .OrderBy(g => g.Subject != null ? g.Subject.DisplayName : g.SubjectId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new
            {
                g.Id,
                SubjectId = g.SubjectId,
                SubjectName = g.Subject != null ? g.Subject.DisplayName : g.SubjectId,
                SubjectType = g.Subject != null && g.Subject.SubjectType != null ? g.Subject.SubjectType.Name : null,
                RoleId = g.RoleId,
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                g.EffectiveFrom,
                g.EffectiveTo
            })
            .ToListAsync();

        var result = new
        {
            data = grants,
            page,
            pageSize,
            total,
            totalPages = (int)Math.Ceiling((double)total / pageSize)
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
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
        var (page, pageSize) = GetPaginationParams(context);

        var query = dbContext.Set<SqlOSFgaGrant>()
            .Include(g => g.Resource)
            .Include(g => g.Role)
            .Where(g => g.SubjectId == subjectId);

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderByDescending(g => g.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new
            {
                g.Id,
                ResourceName = g.Resource != null ? g.Resource.Name : g.ResourceId,
                g.ResourceId,
                RoleName = g.Role != null ? g.Role.Name : g.RoleId,
                g.RoleId,
                g.EffectiveFrom, g.EffectiveTo, g.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        var result = new
        {
            Data = data,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    // --- Paginated table endpoints ---

    private static async Task<object> GetSubjectsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var type = context.Request.Query["type"].FirstOrDefault();
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaSubject>()
            .Include(s => s.SubjectType)
            .AsQueryable();

        if (!string.IsNullOrEmpty(type))
            query = query.Where(s => s.SubjectTypeId == type);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(s => s.DisplayName.Contains(search) || s.Id.Contains(search));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(s => s.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id, s.DisplayName, s.SubjectTypeId,
                SubjectType = s.SubjectType != null ? s.SubjectType.Name : s.SubjectTypeId,
                s.OrganizationId, s.ExternalRef, s.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetUsersAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaUser>()
            .Include(u => u.Subject)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u =>
                (u.Subject != null && (u.Subject.DisplayName.Contains(search) || u.Subject.Id.Contains(search))) ||
                (u.Email != null && u.Email.Contains(search)));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(u => u.Subject != null ? u.Subject.DisplayName : u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.SubjectId,
                DisplayName = u.Subject != null ? u.Subject.DisplayName : u.Id,
                u.Email,
                u.IsActive,
                u.LastLoginAt,
                u.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetAgentsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaAgent>()
            .Include(a => a.Subject)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(a =>
                (a.Subject != null && (a.Subject.DisplayName.Contains(search) || a.Subject.Id.Contains(search))) ||
                (a.AgentType != null && a.AgentType.Contains(search)) ||
                (a.Description != null && a.Description.Contains(search)));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(a => a.Subject != null ? a.Subject.DisplayName : a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.SubjectId,
                DisplayName = a.Subject != null ? a.Subject.DisplayName : a.Id,
                a.AgentType,
                a.Description,
                a.LastRunAt,
                a.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetServiceAccountsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaServiceAccount>()
            .Include(s => s.Subject)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(s =>
                (s.Subject != null && (s.Subject.DisplayName.Contains(search) || s.Subject.Id.Contains(search))) ||
                s.ClientId.Contains(search) ||
                (s.Description != null && s.Description.Contains(search)));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(s => s.Subject != null ? s.Subject.DisplayName : s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.SubjectId,
                DisplayName = s.Subject != null ? s.Subject.DisplayName : s.Id,
                s.ClientId,
                s.Description,
                s.LastUsedAt,
                s.ExpiresAt,
                s.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetUserGroupsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaUserGroup>()
            .Include(g => g.Subject)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(g =>
                g.Name.Contains(search) ||
                (g.Subject != null && g.Subject.DisplayName.Contains(search)) ||
                (g.Description != null && g.Description.Contains(search)));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(g => g.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new
            {
                g.Id,
                g.SubjectId,
                g.Name,
                g.Description,
                g.GroupType,
                g.CreatedAt
            })
            .ToListAsync();

        // Fetch member counts in a single query to avoid N+1 from correlated Count in projection
        var groupIds = data.Select(x => x.Id).ToList();
        Dictionary<string, int> memberCountLookup;
        if (groupIds.Count > 0)
        {
            var memberCounts = await dbContext.Set<SqlOSFgaUserGroupMembership>()
                .Where(m => groupIds.Contains(m.UserGroupId))
                .GroupBy(m => m.UserGroupId)
                .Select(g => new { UserGroupId = g.Key, Count = g.Count() })
                .ToListAsync();
            memberCountLookup = memberCounts.ToDictionary(x => x.UserGroupId, x => x.Count);
        }
        else
        {
            memberCountLookup = new Dictionary<string, int>();
        }

        var dataWithCounts = data.Select(g => new
        {
            g.Id,
            g.SubjectId,
            g.Name,
            g.Description,
            g.GroupType,
            MemberCount = memberCountLookup.GetValueOrDefault(g.Id, 0),
            g.CreatedAt
        }).ToList();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = dataWithCounts, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetGrantsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaGrant>()
            .Include(g => g.Subject)
            .Include(g => g.Resource)
            .Include(g => g.Role)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(g =>
                (g.Subject != null && g.Subject.DisplayName.Contains(search)) ||
                (g.Resource != null && g.Resource.Name.Contains(search)) ||
                (g.Role != null && g.Role.Name.Contains(search)));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderByDescending(g => g.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetRolesAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaRole>()
            .Include(r => r.RolePermissions)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(r => r.Name.Contains(search) || r.Key.Contains(search));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(r => r.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id, r.Key, r.Name, r.Description, r.IsVirtual,
                PermissionCount = r.RolePermissions.Count
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetPermissionsAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaPermission>()
            .Include(p => p.ResourceType)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Key.Contains(search) || p.Name.Contains(search));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(p => p.Key)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id, p.Key, p.Name, p.Description,
                ResourceType = p.ResourceType != null ? p.ResourceType.Name : null
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    private static async Task<object> GetResourceTypesAsync(ISqlOSFgaDbContext dbContext, HttpContext context)
    {
        var (page, pageSize) = GetPaginationParams(context);
        var search = context.Request.Query["search"].FirstOrDefault();

        var query = dbContext.Set<SqlOSFgaResourceType>().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(rt => rt.Name.Contains(search) || rt.Id.Contains(search));

        var totalCount = await query.CountAsync();
        var data = await query
            .OrderBy(rt => rt.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(rt => new { rt.Id, Key = rt.Id, rt.Name, rt.Description })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new { Data = data, Page = page, PageSize = pageSize, TotalCount = totalCount, TotalPages = totalPages };
    }

    // --- Helpers ---

    private static (int Page, int PageSize) GetPaginationParams(HttpContext context)
    {
        var page = GetIntParam(context, "page", 1);
        var pageSize = GetIntParam(context, "pageSize", DefaultPageSize);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        return (page, pageSize);
    }

    private static int GetIntParam(HttpContext context, string name, int defaultValue)
    {
        var value = context.Request.Query[name].FirstOrDefault();
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private async Task ServeStaticFile(HttpContext context, string relativePath)
    {
        var serveShell = false;
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            relativePath = "index.html";
            serveShell = true;
        }

        var fileInfo = _fileProvider.GetFileInfo(relativePath);

        if (!fileInfo.Exists)
        {
            // SPA fallback
            fileInfo = _fileProvider.GetFileInfo("index.html");
            serveShell = true;
        }

        if (!fileInfo.Exists)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var contentType = GetContentType(relativePath);
        context.Response.ContentType = contentType;

        await using var stream = fileInfo.CreateReadStream();
        if (serveShell)
        {
            using var reader = new StreamReader(stream);
            var html = await reader.ReadToEndAsync();
            html = html.Replace("__SQL_OS_BASE_PATH__", GetDashboardShellPrefix().TrimEnd('/'), StringComparison.Ordinal);
            await context.Response.WriteAsync(html);
            return;
        }

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
        if (body == null || string.IsNullOrEmpty(body.Key) || string.IsNullOrEmpty(body.Name))
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
        if (body == null || string.IsNullOrEmpty(body.Key) || string.IsNullOrEmpty(body.Name))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("{\"error\":\"key and name are required\"}");
            return;
        }

        var permId = $"perm_{Guid.NewGuid():N}"[..30];
        var perm = new SqlOSFgaPermission
        {
            Id = permId,
            Key = body.Key,
            Name = body.Name,
            Description = body.Description,
            ResourceTypeId = body.ResourceTypeId
        };
        dbContext.Set<SqlOSFgaPermission>().Add(perm);
        await dbContext.SaveChangesAsync();

        var created = await dbContext.Set<SqlOSFgaPermission>()
            .Include(p => p.ResourceType)
            .Where(p => p.Id == permId)
            .Select(p => new { p.Id, p.Key, p.Name, p.Description, ResourceType = p.ResourceType != null ? p.ResourceType.Name : (string?)null })
            .FirstOrDefaultAsync();

        context.Response.StatusCode = 201;
        await context.Response.WriteAsync(JsonSerializer.Serialize(created, JsonOptions));
    }

    private record TraceRequest(string SubjectId, string ResourceId, string PermissionKey);
    private record CreateGrantRequest(string SubjectId, string RoleId, string ResourceId, DateTime? EffectiveFrom, DateTime? EffectiveTo);
    private record AddRolePermissionRequest(string PermissionId);
    private record CreateRoleRequest(string Key, string Name, string? Description, bool? IsVirtual);
    private record UpdateRoleRequest(string? Name, string? Description, bool? IsVirtual);
    private record CreatePermissionRequest(string Key, string Name, string? Description, string? ResourceTypeId);
}
