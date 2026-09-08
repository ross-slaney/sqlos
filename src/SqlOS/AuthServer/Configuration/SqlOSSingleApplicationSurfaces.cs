using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;
using SqlOS.Configuration;

namespace SqlOS.AuthServer.Configuration;

internal enum SqlOSSingleApplicationSurfaceKind
{
    Api,
    Mcp
}

/// <summary>
/// One same-process protected surface derived from the single-application description.
/// </summary>
/// <param name="Kind">Whether the surface is the REST API or the MCP endpoint.</param>
/// <param name="Path">The normalized absolute path prefix, for example <c>/api</c>.</param>
/// <param name="Audience">The RFC 8707 resource identifier and required token <c>aud</c>: <c>{Origin}{Path}</c>.</param>
/// <param name="Realm">The RFC 6750 realm advertised in challenges.</param>
/// <param name="MetadataPath">The path of the RFC 9728 protected-resource document advertised in challenges.</param>
/// <param name="MetadataUrl">The absolute URL of that document.</param>
internal sealed record SqlOSSingleApplicationSurface(
    SqlOSSingleApplicationSurfaceKind Kind,
    string Path,
    string Audience,
    string Realm,
    string MetadataPath,
    string MetadataUrl);

/// <summary>
/// Derives protocol consequences (audiences, realms, protected-resource documents) from the
/// <see cref="SqlOSApplicationOptions.Api"/> and <see cref="SqlOSApplicationOptions.Mcp"/>
/// declarations. This is the single source of truth shared by the options derivation, the startup
/// validator, the client seed, the startup filter, and the metadata endpoints.
/// </summary>
internal static class SqlOSSingleApplicationSurfaces
{
    public const string ProtectedResourceWellKnownPath = "/.well-known/oauth-protected-resource";
    public const string DefaultAudienceSentinel = "sqlos";

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    public static bool HasApi(SqlOSApplicationOptions? application)
        => NormalizePath(application?.Api) != null;

    public static bool HasMcp(SqlOSApplicationOptions? application)
        => NormalizePath(application?.Mcp) != null;

    public static bool HasAnySurface(SqlOSApplicationOptions? application)
        => HasApi(application) || HasMcp(application);

    /// <summary>
    /// Returns the application origin as <c>scheme://authority</c> when it is an absolute http(s)
    /// URI without path, query, or fragment; otherwise <see langword="null"/>.
    /// </summary>
    public static string? TryGetOrigin(SqlOSApplicationOptions? application)
    {
        if (application == null
            || string.IsNullOrWhiteSpace(application.Origin)
            || !Uri.TryCreate(application.Origin.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrWhiteSpace(uri.Query)
            || !string.IsNullOrWhiteSpace(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static string? ResolveApiAudience(SqlOSApplicationOptions? application)
    {
        var path = NormalizePath(application?.Api);
        var origin = TryGetOrigin(application);
        return path == null || origin == null ? null : $"{origin}{path}";
    }

    public static string? ResolveMcpAudience(SqlOSApplicationOptions? application)
    {
        var path = NormalizePath(application?.Mcp);
        var origin = TryGetOrigin(application);
        return path == null || origin == null ? null : $"{origin}{path}";
    }

    /// <summary>
    /// Resolves the first-party client's audience: the explicit <see cref="SqlOSSingleApplicationOptions.Audience"/>,
    /// else <c>{Origin}{Api}</c> when an API surface is declared, else the client ID.
    /// </summary>
    public static string ResolveClientAudience(SqlOSSingleApplicationOptions application, string clientId)
    {
        if (!string.IsNullOrWhiteSpace(application.Audience))
        {
            return application.Audience.Trim();
        }

        return ResolveApiAudience(application) ?? clientId;
    }

    public static IReadOnlyList<SqlOSSingleApplicationSurface> Describe(SqlOSApplicationOptions? application)
    {
        if (application == null)
        {
            return [];
        }

        var origin = TryGetOrigin(application);
        if (origin == null)
        {
            return [];
        }

        var name = string.IsNullOrWhiteSpace(application.Name) ? "SqlOS" : application.Name.Trim();
        var surfaces = new List<SqlOSSingleApplicationSurface>(2);

        if (NormalizePath(application.Api) is { } apiPath)
        {
            surfaces.Add(new SqlOSSingleApplicationSurface(
                SqlOSSingleApplicationSurfaceKind.Api,
                apiPath,
                $"{origin}{apiPath}",
                $"{name} API",
                ProtectedResourceWellKnownPath,
                $"{origin}{ProtectedResourceWellKnownPath}"));
        }

        if (NormalizePath(application.Mcp) is { } mcpPath)
        {
            var metadataPath = $"{ProtectedResourceWellKnownPath}{mcpPath}";
            surfaces.Add(new SqlOSSingleApplicationSurface(
                SqlOSSingleApplicationSurfaceKind.Mcp,
                mcpPath,
                $"{origin}{mcpPath}",
                $"{name} MCP",
                metadataPath,
                $"{origin}{metadataPath}"));
        }

        return surfaces;
    }

    public static bool Matches(PathString requestPath, string surfacePath)
        => requestPath.StartsWithSegments(surfacePath);

    /// <summary>
    /// True when the route's leading literal segments are exactly the surface path
    /// (<c>/api</c> matches <c>/api/me</c> and <c>/api/{id}</c>, not <c>/apiary</c> or
    /// <c>/{surface}/me</c>). Used to attach token validation to mapped endpoints.
    /// </summary>
    public static bool MatchesRoute(RoutePattern pattern, string surfacePath)
    {
        var expected = surfacePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length == 0 || pattern.PathSegments.Count < expected.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            var segment = pattern.PathSegments[i];
            if (!segment.IsSimple
                || segment.Parts[0] is not RoutePatternLiteralPart literal
                || !string.Equals(literal.Content, expected[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the host-level consequences of the single-application description that live
    /// outside <see cref="SqlOSAuthServerOptions"/>: FGA authorization seeds and host extensions.
    /// Idempotent for a given options instance.
    /// </summary>
    public static void ApplyHostConfiguration(SqlOSOptions options)
    {
        var application = options.AuthServer.Application;
        if (application == null || application.AuthorizationConfigurations.Count == 0)
        {
            return;
        }

        foreach (var configure in application.AuthorizationConfigurations)
        {
            options.Fga.Seed(configure);
        }

        application.AuthorizationConfigurations.Clear();
    }
}
