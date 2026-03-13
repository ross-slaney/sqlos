using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SqlOS.AuthServer.Configuration;
using SqlOS.Dashboard;

namespace SqlOS.AuthServer.Dashboard;

public sealed class SqlOSDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSAuthServerDashboardOptions _options;
    private readonly SqlOSDashboardSessionService _sessionService;
    private readonly IFileProvider _fileProvider;

    public SqlOSDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSAuthServerDashboardOptions options,
        SqlOSDashboardSessionService sessionService)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _options = options;
        _sessionService = sessionService;
        _fileProvider = CreateFileProvider(environment);
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
        var isApiRequest = relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".well-known/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("saml/", StringComparison.OrdinalIgnoreCase);

        if (!await IsAuthorizedAsync(context))
        {
            await HandleUnauthorizedRequestAsync(context, isApiRequest);
            return;
        }

        if (isApiRequest)
        {
            await _next(context);
            return;
        }

        var embedMode = string.Equals(context.Request.Query["embed"], "1", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(relativePath) && !embedMode)
        {
            context.Response.Redirect($"{GetDashboardShellPrefix()}admin/auth/overview", false);
            return;
        }

        if (string.IsNullOrEmpty(relativePath) && !path.EndsWith('/'))
        {
            context.Response.Redirect($"{_pathPrefix}/", false);
            return;
        }

        var file = _fileProvider.GetFileInfo(string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath);
        if (!file.Exists)
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = GetContentType(file.Name);
        await using var stream = file.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body);
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

    private async Task HandleUnauthorizedRequestAsync(HttpContext context, bool isApiRequest)
    {
        if (_sessionService.IsPasswordMode(_options.AuthMode))
        {
            if (!_sessionService.IsPasswordConfigured(_options.Password))
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

    private string GetDashboardShellPrefix()
    {
        var suffix = "/admin/auth";
        return _pathPrefix.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? _pathPrefix[..^suffix.Length] + "/"
            : $"{_pathPrefix}/";
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static IFileProvider CreateFileProvider(IHostEnvironment environment)
    {
        var sourceRoot = TryFindDevelopmentAssetRoot(environment.ContentRootPath, Path.Combine("src", "SqlOS", "AuthServer", "Dashboard", "wwwroot"));
        if (environment.IsDevelopment() && sourceRoot != null)
        {
            return new PhysicalFileProvider(sourceRoot);
        }

        return new ManifestEmbeddedFileProvider(typeof(SqlOSDashboardMiddleware).Assembly, "AuthServer/Dashboard/wwwroot");
    }

    private static string? TryFindDevelopmentAssetRoot(string contentRootPath, string relativeAssetPath)
    {
        for (var current = new DirectoryInfo(contentRootPath); current != null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relativeAssetPath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string BuildLoginRedirectPath(HttpContext context)
    {
        var shellPrefix = GetDashboardShellPrefix().TrimEnd('/');
        var requestedPath = $"{context.Request.Path}{context.Request.QueryString}";
        var encodedNext = Uri.EscapeDataString(requestedPath);
        return $"{shellPrefix}/login?next={encodedNext}";
    }
}
