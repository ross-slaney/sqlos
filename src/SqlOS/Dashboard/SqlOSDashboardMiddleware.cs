using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.Json;
using SqlOS.Configuration;

namespace SqlOS.Dashboard;

public sealed class SqlOSDashboardMiddleware
{
    private const string DashboardAuthPrefix = "dashboard-auth";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSDashboardOptions _options;
    private readonly SqlOSDashboardSessionService _sessionService;
    private readonly IFileProvider _fileProvider;

    public SqlOSDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSDashboardOptions options,
        SqlOSDashboardSessionService sessionService)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _options = options;
        _sessionService = sessionService;
        _fileProvider = CreateFileProvider();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var relativePath = path[_pathPrefix.Length..].TrimStart('/');
        var embedMode = string.Equals(context.Request.Query["embed"], "1", StringComparison.Ordinal);

        if (string.IsNullOrEmpty(relativePath) && !path.EndsWith('/'))
        {
            context.Response.Redirect($"{_pathPrefix}/", permanent: false);
            return;
        }

        if (ShouldPassThrough(relativePath, embedMode))
        {
            await _next(context);
            return;
        }

        if (IsDashboardAuthEndpoint(relativePath))
        {
            await HandleDashboardAuthRequestAsync(context, relativePath);
            return;
        }

        if (!await IsAuthorizedAsync(context))
        {
            await HandleUnauthorizedRequestAsync(context, relativePath);
            return;
        }

        if (IsLoginRoute(relativePath))
        {
            context.Response.Redirect($"{_pathPrefix}/", permanent: false);
            return;
        }

        if (ShouldServeDashboardShell(relativePath))
        {
            await ServeDashboardShellAsync(context);
            return;
        }

        var requestedFile = string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath;
        var file = _fileProvider.GetFileInfo(requestedFile);
        if (!file.Exists)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await _next(context);
            return;
        }

        if (string.Equals(requestedFile, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            await ServeDashboardShellAsync(context);
            return;
        }

        await ServeFileAsync(context, file);
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        if (_sessionService.IsPasswordMode(_options.AuthMode) && !_sessionService.IsPasswordConfigured(_options.Password))
        {
            return false;
        }

        return await _sessionService.IsAuthorizedAsync(
            context,
            _isDevelopment,
            _options.AuthMode,
            _options.AuthorizationCallback);
    }

    private async Task HandleDashboardAuthRequestAsync(HttpContext context, string relativePath)
    {
        var endpoint = relativePath.Length == DashboardAuthPrefix.Length
            ? string.Empty
            : relativePath[(DashboardAuthPrefix.Length + 1)..];

        if (endpoint.Equals("session", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsGet(context.Request.Method))
        {
            var authorized = await IsAuthorizedAsync(context);
            var expiresAt = _sessionService.GetSessionExpiry(context);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                authenticated = authorized,
                expiresAt = expiresAt?.UtcDateTime
            }));
            return;
        }

        if (endpoint.Equals("logout", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(context.Request.Method))
        {
            _sessionService.ClearSession(context, _pathPrefix);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!_sessionService.IsPasswordMode(_options.AuthMode))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!_sessionService.IsPasswordConfigured(_options.Password))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("SqlOS dashboard password mode is enabled but no password was configured.");
            return;
        }

        if (endpoint.Equals("login", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(context.Request.Method))
        {
            var payload = await JsonSerializer.DeserializeAsync<DashboardLoginRequest>(context.Request.Body, JsonOptions);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Password))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("{\"error\":\"password is required\"}");
                return;
            }

            if (!_sessionService.VerifyPassword(_options.Password!, payload.Password))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("{\"error\":\"Invalid password\"}");
                return;
            }

            var allowInsecureCookie = _isDevelopment && !context.Request.IsHttps;
            _sessionService.CreateSession(context, _pathPrefix, _options.SessionLifetime, allowInsecureCookie);
            var expiresAt = _sessionService.GetSessionExpiry(context);

            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                authenticated = true,
                expiresAt = expiresAt?.UtcDateTime
            }));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private async Task HandleUnauthorizedRequestAsync(HttpContext context, string relativePath)
    {
        if (!_sessionService.IsPasswordMode(_options.AuthMode))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!_sessionService.IsPasswordConfigured(_options.Password))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("SqlOS dashboard password mode is enabled but no password was configured.");
            return;
        }

        if (IsLoginRoute(relativePath))
        {
            await ServeDashboardShellAsync(context);
            return;
        }

        if (await TryServePublicAssetAsync(context, relativePath))
        {
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Redirect(BuildLoginRedirectPath(context), permanent: false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private static bool IsDashboardAuthEndpoint(string relativePath)
        => relativePath.Equals(DashboardAuthPrefix, StringComparison.OrdinalIgnoreCase)
           || relativePath.StartsWith($"{DashboardAuthPrefix}/", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoginRoute(string relativePath)
        => relativePath.Trim('/').Equals("login", StringComparison.OrdinalIgnoreCase);

    private bool ShouldPassThrough(string relativePath, bool embedMode)
    {
        if (relativePath.StartsWith("admin/fga", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (relativePath.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/auth/api/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/auth/.well-known/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/auth/saml/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Path.HasExtension(relativePath)
            && (relativePath.StartsWith("admin/auth/", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("admin/fga/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (embedMode && (relativePath.StartsWith("admin/auth", StringComparison.OrdinalIgnoreCase)
                          || relativePath.StartsWith("admin/fga", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream"
    };

    private bool ShouldServeDashboardShell(string relativePath)
    {
        if (IsLoginRoute(relativePath))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        if (relativePath.StartsWith("admin/auth", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task<bool> TryServePublicAssetAsync(HttpContext context, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !Path.HasExtension(relativePath))
        {
            return false;
        }

        var file = _fileProvider.GetFileInfo(relativePath);
        if (!file.Exists)
        {
            return false;
        }

        await ServeFileAsync(context, file);
        return true;
    }

    private async Task ServeFileAsync(HttpContext context, IFileInfo file)
    {
        context.Response.ContentType = GetContentType(file.Name);
        await using var stream = file.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body);
    }

    private async Task ServeDashboardShellAsync(HttpContext context)
    {
        var file = _fileProvider.GetFileInfo("index.html");
        if (!file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";

        await using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();
        html = html.Replace("__SQL_OS_DASHBOARD_BASE_PATH_JSON__", JsonSerializer.Serialize(_pathPrefix), StringComparison.Ordinal);
        html = html.Replace("__SQL_OS_BASE_PATH__", _pathPrefix, StringComparison.Ordinal);
        await context.Response.WriteAsync(html);
    }

    private string BuildLoginRedirectPath(HttpContext context)
    {
        var requestedPath = $"{context.Request.Path}{context.Request.QueryString}";
        var encodedNext = Uri.EscapeDataString(requestedPath);
        return $"{_pathPrefix}/login?next={encodedNext}";
    }

    private static IFileProvider CreateFileProvider()
        => new ManifestEmbeddedFileProvider(typeof(SqlOSDashboardMiddleware).Assembly, "Dashboard/wwwroot");

    private sealed record DashboardLoginRequest(string Password);
}
