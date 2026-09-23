using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;

namespace SqlOS.Security;

/// <summary>
/// Shared CSRF boundary for SqlOS mutations authenticated by the dashboard session
/// cookie or the SSO-portal session cookie. Requests that do not present either cookie
/// stay on their existing bearer or callback path.
/// </summary>
internal static class SqlOSCookieMutationCsrf
{
    internal const string HeaderName = "X-SqlOS-Request";
    internal const string HeaderValue = "1";
    internal const string DashboardSessionCookieName = "SqlOS.Dashboard.Session";
    internal const string DefaultPortalCookieName = "sqlos_sso_portal";
    internal const string RejectionError = "csrf_rejected";
    internal const string RejectionMessage = "The request could not be verified.";
    internal const string RejectionEventType = "security.csrf_rejected";

    internal static async Task<bool> RejectIfRequiredAsync(HttpContext context)
    {
        var decision = Evaluate(context);
        if (decision.IsAllowed)
        {
            return false;
        }

        await RecordRejectionAsync(context, decision.Reason);
        await SqlOSCookieMutationCsrfResult.Instance.ExecuteAsync(context);
        return true;
    }

    internal static SqlOSCookieMutationCsrfDecision Evaluate(HttpContext context)
    {
        var request = context.Request;
        if (!IsUnsafeMethod(request.Method))
        {
            return SqlOSCookieMutationCsrfDecision.Allow;
        }

        var options = ResolveOptions(context);
        if (!HasAmbientSessionCookie(request, options))
        {
            return SqlOSCookieMutationCsrfDecision.Allow;
        }

        if (!HasRequiredHeader(request))
        {
            return SqlOSCookieMutationCsrfDecision.Reject(
                request.Headers[HeaderName].Count > 1 ? "ambiguous_header" : "missing_header");
        }

        return HasTrustedBrowserSource(request, options, out var reason)
            ? SqlOSCookieMutationCsrfDecision.Allow
            : SqlOSCookieMutationCsrfDecision.Reject(reason);
    }

    internal static bool IsUnsafeMethod(string method)
        => !HttpMethods.IsGet(method)
            && !HttpMethods.IsHead(method)
            && !HttpMethods.IsOptions(method);

    private static SqlOSAuthServerOptions? ResolveOptions(HttpContext context)
        => context.RequestServices.GetService<IOptions<SqlOSAuthServerOptions>>()?.Value
            ?? context.RequestServices.GetService<IOptions<SqlOSOptions>>()?.Value.AuthServer;

    private static bool HasAmbientSessionCookie(HttpRequest request, SqlOSAuthServerOptions? options)
    {
        if (request.Cookies.ContainsKey(DashboardSessionCookieName))
        {
            return true;
        }

        var portalCookieName = options?.SsoPortal.CookieName;
        if (string.IsNullOrWhiteSpace(portalCookieName))
        {
            portalCookieName = DefaultPortalCookieName;
        }

        return request.Cookies.ContainsKey(portalCookieName.Trim());
    }

    private static bool HasRequiredHeader(HttpRequest request)
    {
        var values = request.Headers[HeaderName];
        return values.Count == 1
            && string.Equals(values[0]?.Trim(), HeaderValue, StringComparison.Ordinal);
    }

    private static bool HasTrustedBrowserSource(
        HttpRequest request,
        SqlOSAuthServerOptions? options,
        out string reason)
    {
        var origin = ReadSingleHeader(request.Headers.Origin, out var originAmbiguous);
        if (originAmbiguous)
        {
            reason = "ambiguous_origin";
            return false;
        }

        if (IsOpaque(origin))
        {
            reason = "untrusted_origin";
            return false;
        }

        var source = origin;
        if (IsAbsent(source))
        {
            var referer = ReadSingleHeader(request.Headers.Referer, out var refererAmbiguous);
            if (refererAmbiguous)
            {
                reason = "ambiguous_origin";
                return false;
            }

            if (IsOpaque(referer))
            {
                reason = "untrusted_origin";
                return false;
            }

            source = referer;
        }

        if (IsAbsent(source))
        {
            var fetchSite = ReadSingleHeader(request.Headers["Sec-Fetch-Site"], out var fetchSiteAmbiguous);
            if (fetchSiteAmbiguous)
            {
                reason = "ambiguous_origin";
                return false;
            }

            if (string.Equals(fetchSite?.Trim(), "same-origin", StringComparison.OrdinalIgnoreCase))
            {
                reason = string.Empty;
                return true;
            }

            reason = "untrusted_origin";
            return false;
        }

        if (!TryReadHttpOrigin(source, out var sourceOrigin))
        {
            reason = "untrusted_origin";
            return false;
        }

        var trusted = GetTrustedOrigins(request, options);
        if (trusted.Count == 0
            || !trusted.Contains(sourceOrigin))
        {
            reason = "untrusted_origin";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static HashSet<string> GetTrustedOrigins(HttpRequest request, SqlOSAuthServerOptions? options)
    {
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options != null)
        {
            AddConfiguredOrigin(trusted, options.PublicOrigin);
            AddConfiguredOrigin(trusted, options.Issuer);
        }

        if (request.Host.HasValue
            && (string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate($"{request.Scheme}://{request.Host.Value}", UriKind.Absolute, out var effective)
            && TryReadHttpOrigin(effective.AbsoluteUri, out var effectiveOrigin))
        {
            trusted.Add(effectiveOrigin);
        }

        return trusted;
    }

    private static void AddConfiguredOrigin(HashSet<string> trusted, string? value)
    {
        if (TryReadHttpOrigin(value, out var origin))
        {
            trusted.Add(origin);
        }
    }

    private static bool TryReadHttpOrigin(string? value, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return origin.Length > 0;
    }

    private static string? ReadSingleHeader(StringValues values, out bool ambiguous)
    {
        if (values.Count == 0)
        {
            ambiguous = false;
            return null;
        }

        if (values.Count != 1)
        {
            ambiguous = true;
            return null;
        }

        var text = values[0];
        if (string.IsNullOrWhiteSpace(text))
        {
            ambiguous = false;
            return null;
        }

        if (text.Contains(',', StringComparison.Ordinal))
        {
            ambiguous = true;
            return null;
        }

        ambiguous = false;
        return text;
    }

    private static bool IsOpaque(string? value)
        => string.Equals(value?.Trim(), "null", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsent(string? value)
        => string.IsNullOrWhiteSpace(value);

    internal static Task RecordRejectionAsync(HttpContext context, string reason)
        => RecordRejectionCoreAsync(context, reason);

    private static async Task RecordRejectionCoreAsync(HttpContext context, string reason)
    {
        var admin = context.RequestServices.GetService<SqlOSAdminService>();
        if (admin == null)
        {
            return;
        }

        try
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path.Length > 200)
            {
                path = path[..200];
            }

            await admin.RecordAuditAsync(
                RejectionEventType,
                "security",
                actorId: null,
                ipAddress: SqlOSClientIpAddress.Get(context),
                data: new
                {
                    reason,
                    method = context.Request.Method,
                    path
                },
                cancellationToken: context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The mutation is still rejected. Audit failure must not become a success path.
        }
    }
}

internal readonly record struct SqlOSCookieMutationCsrfDecision(bool IsAllowed, string Reason)
{
    public static SqlOSCookieMutationCsrfDecision Allow { get; } = new(true, string.Empty);

    public static SqlOSCookieMutationCsrfDecision Reject(string reason) => new(false, reason);
}

internal sealed class SqlOSCookieMutationCsrfMetadata
{
    public static SqlOSCookieMutationCsrfMetadata Instance { get; } = new();

    private SqlOSCookieMutationCsrfMetadata()
    {
    }
}

internal sealed class SqlOSCookieMutationCsrfFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var decision = SqlOSCookieMutationCsrf.Evaluate(context.HttpContext);
        if (!decision.IsAllowed)
        {
            await SqlOSCookieMutationCsrf.RecordRejectionAsync(context.HttpContext, decision.Reason);
            return SqlOSCookieMutationCsrfResult.Instance;
        }

        return await next(context);
    }
}

internal sealed class SqlOSCookieMutationCsrfResult : IResult
{
    public static SqlOSCookieMutationCsrfResult Instance { get; } = new();

    private SqlOSCookieMutationCsrfResult()
    {
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (httpContext.Response.HasStarted)
        {
            return;
        }

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-store";
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                error = SqlOSCookieMutationCsrf.RejectionError,
                message = SqlOSCookieMutationCsrf.RejectionMessage
            }),
            httpContext.RequestAborted);
    }
}
