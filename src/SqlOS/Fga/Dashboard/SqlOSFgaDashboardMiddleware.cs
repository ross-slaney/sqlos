using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Domain;
using SqlOS.Fga.Processes;
using SqlOS.Hosting;
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
            _dashboardOptions.Password,
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
        if (await SqlOSCookieMutationCsrf.RejectIfRequiredAsync(context))
        {
            return;
        }

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
        var fga = SqlOSFgaAdministration.For(scope.ServiceProvider, SqlOSHttpRequestContext.From(context, SqlOSRequestSurface.Admin));
        var method = context.Request.Method;
        var aborted = context.RequestAborted;

        if (endpoint.Equals("trace", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var body = await ReadJsonAsync<TraceRequest>(context);
            if (body is not { Value: var trace } || trace == null || string.IsNullOrEmpty(trace.SubjectId) || string.IsNullOrEmpty(trace.ResourceId) || string.IsNullOrEmpty(trace.PermissionKey))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, body == null ? InvalidJsonError : "subjectId, resourceId, and permissionKey are required");
                return;
            }

            await WriteJsonAsync(context, await fga.TraceAsync(trace.SubjectId, trace.ResourceId, trace.PermissionKey));
            return;
        }

        if (endpoint.Equals("grants", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            await HandleCreateGrant(context, fga);
            return;
        }

        if (endpoint.StartsWith("grants/", StringComparison.OrdinalIgnoreCase) && method == "DELETE")
        {
            if (await fga.RevokeGrantAsync(endpoint[7..], aborted))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Grant not found");
            return;
        }

        // roles/{roleId}/permissions
        if (endpoint.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/permissions") && !endpoint.Contains("/permissions/"))
        {
            if (method == "GET")
            {
                await WriteJsonAsync(context, await fga.GetRolePermissionsAsync(endpoint[6..^12], aborted));
                return;
            }

            if (method == "POST")
            {
                await RejectSchemaWriteAsync(context);
                return;
            }
        }

        // roles/{roleId}/permissions/{permissionId}
        if (endpoint.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) && method == "DELETE")
        {
            var parts = endpoint[6..].Split('/');
            if (parts.Length == 3 && parts[1].Equals("permissions", StringComparison.OrdinalIgnoreCase))
            {
                await RejectSchemaWriteAsync(context);
                return;
            }
        }

        if (endpoint.Equals("roles", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            await RejectSchemaWriteAsync(context);
            return;
        }

        if (endpoint.StartsWith("roles/", StringComparison.OrdinalIgnoreCase) && !endpoint[6..].Contains('/'))
        {
            if (method == "GET")
            {
                await WriteFoundAsync(context, await fga.GetRoleAsync(endpoint[6..], aborted), "Role not found");
                return;
            }

            if (method == "PUT" || method == "DELETE")
            {
                await RejectSchemaWriteAsync(context);
                return;
            }
        }

        if (endpoint.Equals("permissions", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            await RejectSchemaWriteAsync(context);
            return;
        }

        // resources/{parentId}/children answers any method.
        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/children"))
        {
            await WriteJsonAsync(context, await fga.GetResourceChildrenAsync(endpoint[10..^9], Search(context), Page(context), aborted));
            return;
        }

        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/access") && method == "GET")
        {
            await WriteFoundAsync(context, await fga.GetResourceAccessAsync(endpoint[10..^7], aborted), "Resource not found");
            return;
        }

        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/grants") && method == "GET")
        {
            await WriteJsonAsync(context, await fga.GetResourceGrantsAsync(endpoint[10..^7], Page(context), aborted));
            return;
        }

        if (endpoint.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)
            && !endpoint[10..].Contains('/')
            && !endpoint.Equals("resources/tree", StringComparison.OrdinalIgnoreCase)
            && method == "GET")
        {
            await WriteFoundAsync(context, await fga.GetResourceAsync(endpoint[10..], aborted), "Resource not found");
            return;
        }

        // subjects/{subjectId}/grants answers any method.
        if (endpoint.StartsWith("subjects/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/grants"))
        {
            await WriteJsonAsync(context, await fga.GetSubjectGrantsAsync(endpoint[9..^7], Page(context), aborted));
            return;
        }

        if (endpoint.StartsWith("subjects/", StringComparison.OrdinalIgnoreCase) && !endpoint[9..].Contains('/'))
        {
            if (method == "DELETE")
            {
                await HandleDeleteSubject(context, fga, endpoint[9..]);
                return;
            }

            await WriteFoundAsync(context, await fga.GetSubjectAsync(endpoint[9..], aborted), "Subject not found");
            return;
        }

        var search = Search(context);
        object? result = endpoint.ToLowerInvariant() switch
        {
            "resources/tree" => await fga.GetResourceTreeAsync(search, Page(context), aborted),
            "resources" => await fga.GetResourcesAsync(search, Page(context), aborted),
            "subjects" => await fga.GetSubjectsAsync(context.Request.Query["type"].FirstOrDefault(), search, Page(context), aborted),
            "users" => await fga.GetUsersAsync(search, Page(context), aborted),
            "agents" => await fga.GetAgentsAsync(search, Page(context), aborted),
            "service-accounts" => await fga.GetServiceAccountsAsync(search, Page(context), aborted),
            "user-groups" => await fga.GetUserGroupsAsync(search, Page(context), aborted),
            "grants" => await fga.GetGrantsAsync(search, Page(context), aborted),
            "roles" => await fga.GetRolesAsync(search, Page(context), aborted),
            "permissions" => await fga.GetPermissionsAsync(search, Page(context), aborted),
            "resource-types" => await fga.GetResourceTypesAsync(search, Page(context), aborted),
            "stats" => await fga.GetStatsAsync(aborted),
            _ => null
        };
        await WriteFoundAsync(context, result, "Not found");
    }

    private static async Task HandleCreateGrant(HttpContext context, SqlOSFgaAdministration fga)
    {
        var body = await ReadJsonAsync<CreateGrantRequest>(context);
        if (body is not { Value: var request } || request == null || string.IsNullOrEmpty(request.SubjectId) || string.IsNullOrEmpty(request.RoleId) || string.IsNullOrEmpty(request.ResourceId))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, body == null ? InvalidJsonError : "subjectId, roleId, and resourceId are required");
            return;
        }

        var (outcome, grant) = await fga.GrantAsync(
            new GrantFgaRoleCommand(request.SubjectId, request.RoleId, request.ResourceId, request.EffectiveFrom, request.EffectiveTo),
            new GrantAuthority(FgaActor.Operator),
            context.RequestAborted);
        switch (outcome)
        {
            case GrantFgaRoleOutcome.Granted:
                context.Response.StatusCode = StatusCodes.Status201Created;
                await WriteJsonAsync(context, grant);
                break;
            case GrantFgaRoleOutcome.Refused { Reason: GrantRefusal.Duplicate } duplicate:
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await WriteJsonAsync(context, new { error = "An identical grant already exists.", grantId = duplicate.ExistingGrantId });
                break;
            case GrantFgaRoleOutcome.Refused refused:
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, refused.Reason switch
                {
                    GrantRefusal.SubjectNotFound => "Subject not found",
                    GrantRefusal.RoleNotFound => "Role not found",
                    _ => "Resource not found"
                });
                break;
        }
    }

    private static async Task HandleDeleteSubject(HttpContext context, SqlOSFgaAdministration fga, string subjectId)
    {
        switch (await fga.DeleteSubjectAsync(subjectId, context.RequestAborted))
        {
            case DeleteFgaSubjectOutcome.Deleted:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case DeleteFgaSubjectOutcome.Refused refused:
                await WriteErrorAsync(context, StatusCodes.Status409Conflict, refused.Message);
                break;
            default:
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Subject not found");
                break;
        }
    }

    private const string InvalidJsonError = "The request body is not valid JSON.";

    /// <summary>The body as <typeparamref name="T"/>, or null when it is not valid JSON for it.</summary>
    private static async Task<JsonBody<T>?> ReadJsonAsync<T>(HttpContext context)
    {
        try
        {
            return new JsonBody<T>(await JsonSerializer.DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Search(HttpContext context) => context.Request.Query["search"].FirstOrDefault();

    private static FgaPageRequest Page(HttpContext context)
        => new(context.Request.Query["cursor"].FirstOrDefault(), IntQuery(context, "pageSize"), IntQuery(context, "page"));

    private static int? IntQuery(HttpContext context, string name)
        => int.TryParse(context.Request.Query[name].FirstOrDefault(), out var parsed) ? parsed : null;

    private static Task WriteJsonAsync(HttpContext context, object? value)
        => context.Response.WriteAsync(JsonSerializer.Serialize(value, JsonOptions));

    private static Task WriteFoundAsync(HttpContext context, object? value, string notFound)
        => value == null ? WriteErrorAsync(context, StatusCodes.Status404NotFound, notFound) : WriteJsonAsync(context, value);

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        return WriteJsonAsync(context, new { error });
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

    internal const string SchemaWriteError =
        "FGA roles and permissions are defined in startup configuration. Use options.Fga.Seed to change the authorization model.";

    private static async Task RejectSchemaWriteAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "GET";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = SchemaWriteError }, JsonOptions));
    }

    private sealed record JsonBody<T>(T? Value);

    private sealed record TraceRequest(string SubjectId, string ResourceId, string PermissionKey);

    private sealed record CreateGrantRequest(string SubjectId, string RoleId, string ResourceId, DateTime? EffectiveFrom, DateTime? EffectiveTo);
}
