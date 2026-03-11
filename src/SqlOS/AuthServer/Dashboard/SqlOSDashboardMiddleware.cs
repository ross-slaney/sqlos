using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SqlOS.AuthServer.Configuration;

namespace SqlOS.AuthServer.Dashboard;

public sealed class SqlOSDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSAuthServerDashboardOptions _options;
    private readonly IFileProvider _fileProvider;

    public SqlOSDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSAuthServerDashboardOptions options)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _options = options;
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

        if (_options.AuthorizationCallback != null)
        {
            if (!await _options.AuthorizationCallback(context))
            {
                context.Response.StatusCode = 404;
                return;
            }
        }
        else if (!_isDevelopment)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var relativePath = path[_pathPrefix.Length..].TrimStart('/');
        if (relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".well-known/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("saml/", StringComparison.OrdinalIgnoreCase))
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
}
