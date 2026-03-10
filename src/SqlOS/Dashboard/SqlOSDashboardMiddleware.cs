using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Text;
using SqlOS.Configuration;

namespace SqlOS.Dashboard;

public sealed class SqlOSDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSDashboardOptions _options;
    private readonly ManifestEmbeddedFileProvider _fileProvider;

    public SqlOSDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSDashboardOptions options)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _options = options;
        _fileProvider = new ManifestEmbeddedFileProvider(typeof(SqlOSDashboardMiddleware).Assembly, "Dashboard/wwwroot");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!await IsAuthorizedAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var relativePath = path[_pathPrefix.Length..].TrimStart('/');
        var embedMode = string.Equals(context.Request.Query["embed"], "1", StringComparison.Ordinal);

        if (!path.EndsWith('/') && string.IsNullOrEmpty(relativePath))
        {
            context.Response.Redirect($"{_pathPrefix}/", permanent: false);
            return;
        }

        if (relativePath.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/auth/api/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/auth/.well-known/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/auth/saml/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/fga/api/", StringComparison.OrdinalIgnoreCase)
            || (Path.HasExtension(relativePath) && (relativePath.StartsWith("admin/auth/", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("admin/fga/", StringComparison.OrdinalIgnoreCase)))
            || (embedMode && (relativePath.StartsWith("admin/auth", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("admin/fga", StringComparison.OrdinalIgnoreCase))))
        {
            await _next(context);
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

        context.Response.ContentType = GetContentType(file.Name);
        await using var stream = file.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body);
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        if (_options.AuthorizationCallback != null)
        {
            return await _options.AuthorizationCallback(context);
        }

        return _isDevelopment;
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
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        if (relativePath.StartsWith("admin/auth", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/fga", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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
        html = html.Replace("__SQL_OS_BASE_PATH__", _pathPrefix, StringComparison.Ordinal);
        await context.Response.WriteAsync(html);
    }
}
