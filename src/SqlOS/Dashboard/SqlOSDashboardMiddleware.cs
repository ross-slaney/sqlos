using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Security;

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
    private readonly bool _scimEnabled;
    private readonly SqlOSDashboardOptions _options;
    private readonly SqlOSDashboardSessionService _sessionService;
    private readonly IFileProvider _fileProvider;
    private readonly SqlOSBrowserSecurityHeaders _securityHeaders;

    public SqlOSDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSDashboardOptions options,
        bool scimEnabled,
        SqlOSDashboardSessionService sessionService,
        IOptions<SqlOSOptions> hostOptions)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _scimEnabled = scimEnabled;
        _options = options;
        _sessionService = sessionService;
        _securityHeaders = new SqlOSBrowserSecurityHeaders(hostOptions);
        _fileProvider = CreateFileProvider();
    }

    public async Task InvokeAsync(
        HttpContext context,
        SqlOSDashboardLoginThrottlingService loginThrottlingService)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!IsPathOrChild(path, _pathPrefix))
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

        _securityHeaders.ApplyBaseline(context.Response);

        if (IsDashboardAuthEndpoint(relativePath))
        {
            await HandleDashboardAuthRequestAsync(context, relativePath, loginThrottlingService);
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

    private async Task HandleDashboardAuthRequestAsync(
        HttpContext context,
        string relativePath,
        SqlOSDashboardLoginThrottlingService loginThrottlingService)
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
            var clientIp = GetClientIpAddress(context);
            _sessionService.ClearSession(context, _pathPrefix);
            await RecordDashboardAuditAsync(
                context,
                "dashboard.logout",
                clientIp,
                new { reason = "operator_requested" });
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

            var clientIp = GetClientIpAddress(context);
            var now = DateTimeOffset.UtcNow;
            var rejection = await loginThrottlingService.GetRejectionAsync(
                clientIp,
                _options.LoginThrottling,
                now,
                context.RequestAborted);
            if (rejection != null)
            {
                await RecordDashboardAuditAsync(
                    context,
                    "dashboard.login.rate-limited",
                    clientIp,
                    new
                    {
                        scope = rejection.Scope,
                        retry_after_seconds = GetRetryAfterSeconds(rejection.RetryAfter, now)
                    });
                await WriteThrottleResponseAsync(context, rejection, now);
                return;
            }

            if (!_sessionService.VerifyPassword(_options.Password!, payload.Password))
            {
                await RecordDashboardAuditAsync(
                    context,
                    "dashboard.login.failure",
                    clientIp,
                    new { reason = "invalid_password" });

                var lockout = await loginThrottlingService.RecordFailureAsync(
                    clientIp,
                    _options.LoginThrottling,
                    now,
                    context.RequestAborted);
                if (lockout.PerIpLockedUntil is { } perIpLockedUntil)
                {
                    await RecordDashboardAuditAsync(
                        context,
                        "dashboard.login.lockout",
                        clientIp,
                        new
                        {
                            scope = "ip",
                            retry_after_seconds = GetRetryAfterSeconds(perIpLockedUntil, now)
                        });
                }

                if (lockout.GlobalLockedUntil is { } globalLockedUntil)
                {
                    await RecordDashboardAuditAsync(
                        context,
                        "dashboard.login.lockout",
                        clientIp,
                        new
                        {
                            scope = "global",
                            retry_after_seconds = GetRetryAfterSeconds(globalLockedUntil, now)
                        });
                }

                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("{\"error\":\"Invalid password\"}");
                return;
            }

            await loginThrottlingService.RecordSuccessAsync(
                clientIp,
                _options.LoginThrottling,
                now,
                context.RequestAborted);
            var allowInsecureCookie = _isDevelopment && !context.Request.IsHttps;
            var expiresAt = _sessionService.CreateSession(context, _pathPrefix, _options.SessionLifetime, allowInsecureCookie);
            await RecordDashboardAuditAsync(
                context,
                "dashboard.login.success",
                clientIp,
                new { expires_at = expiresAt.UtcDateTime });

            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                authenticated = true,
                expiresAt = expiresAt.UtcDateTime
            }));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static async Task WriteThrottleResponseAsync(
        HttpContext context,
        SqlOSDashboardLoginThrottleRejection rejection,
        DateTimeOffset now)
    {
        var retryAfterSeconds = GetRetryAfterSeconds(rejection.RetryAfter, now);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Too many dashboard login attempts. Try again later.",
            scope = rejection.Scope,
            retryAfterSeconds
        }));
    }

    private static async Task RecordDashboardAuditAsync(
        HttpContext context,
        string eventType,
        string clientIp,
        object? data)
    {
        var adminService = context.RequestServices.GetService<SqlOSAdminService>();
        if (adminService == null)
        {
            return;
        }

        await adminService.RecordAuditAsync(
            eventType,
            "dashboard",
            actorId: null,
            ipAddress: clientIp,
            data: data,
            cancellationToken: context.RequestAborted);
    }

    private static string GetClientIpAddress(HttpContext context)
        => SqlOSClientIpAddress.Get(context);

    private static int GetRetryAfterSeconds(DateTimeOffset retryAfter, DateTimeOffset now)
        => Math.Max(1, (int)Math.Ceiling((retryAfter - now).TotalSeconds));

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
        if (IsPathOrChild(relativePath, "admin/fga"))
        {
            return true;
        }

        if (IsPathOrChild(relativePath, "admin/email/api"))
        {
            return true;
        }

        if (IsPathOrChild(relativePath, "admin/audit/api"))
        {
            return true;
        }

        if (IsPathOrChild(relativePath, "admin/calendar/api"))
        {
            return true;
        }

        if (IsPathOrChild(relativePath, "auth")
            || IsPathOrChild(relativePath, "admin/auth/api")
            || IsPathOrChild(relativePath, "admin/auth/sso-portal")
            || IsPathOrChild(relativePath, "admin/auth/.well-known")
            || IsPathOrChild(relativePath, "admin/auth/saml"))
        {
            return true;
        }

        if (Path.HasExtension(relativePath)
            && (IsPathOrChild(relativePath, "admin/auth")
                || IsPathOrChild(relativePath, "admin/fga")
                || IsPathOrChild(relativePath, "admin/audit")
                || IsPathOrChild(relativePath, "admin/email")
                || IsPathOrChild(relativePath, "admin/calendar")))
        {
            return true;
        }

        if (embedMode && (IsPathOrChild(relativePath, "admin/auth")
                          || IsPathOrChild(relativePath, "admin/fga")
                          || IsPathOrChild(relativePath, "admin/audit")
                          || IsPathOrChild(relativePath, "admin/email")
                          || IsPathOrChild(relativePath, "admin/calendar")))
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

        if (IsPathOrChild(relativePath, "admin/auth")
            || IsPathOrChild(relativePath, "admin/audit")
            || IsPathOrChild(relativePath, "admin/email")
            || IsPathOrChild(relativePath, "admin/calendar"))
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
        html = html.Replace(
            "__SQL_OS_DASHBOARD_CAPABILITIES_JSON__",
            JsonSerializer.Serialize(new { scimEnabled = _scimEnabled }),
            StringComparison.Ordinal);
        html = html.Replace("__SQL_OS_BASE_PATH__", _pathPrefix, StringComparison.Ordinal);
        html = _securityHeaders.PrepareHtml(context, html);
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

    private static bool IsPathOrChild(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return path.StartsWith("/", StringComparison.Ordinal);
        }

        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DashboardLoginRequest(string Password);
}
