using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Services;

namespace SqlOS.Hosting;

/// <summary>
/// Wraps an existing endpoint data source so mapped endpoints whose route pattern sits under a
/// declared <c>Api</c>/<c>Mcp</c> surface validate a bearer token for that surface's audience
/// before the original endpoint runs. This is not pipeline middleware: CORS, authentication, and
/// the rest of the host pipeline run first.
/// </summary>
internal sealed class SqlOSSurfaceEndpointDataSource : EndpointDataSource
{
    private readonly EndpointDataSource _inner;
    private readonly IReadOnlyList<(SqlOSSingleApplicationSurface Surface, SqlOSAccessTokenValidationOptions Options)> _surfaces;
    private IReadOnlyList<Endpoint>? _endpoints;
    private IChangeToken? _changeToken;

    public SqlOSSurfaceEndpointDataSource(
        EndpointDataSource inner,
        IReadOnlyList<SqlOSSingleApplicationSurface> surfaces)
    {
        _inner = inner;
        _surfaces = surfaces
            .Select(surface => (surface, SqlOSAccessTokenValidationMiddleware.ValidateOptions(
                new SqlOSAccessTokenValidationOptions
                {
                    ExpectedAudience = surface.Audience,
                    Realm = surface.Realm,
                    ResourceMetadataUrl = surface.MetadataUrl
                })))
            .ToArray();
    }

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            Refresh();
            return _endpoints!;
        }
    }

    public override IChangeToken GetChangeToken()
    {
        Refresh();
        return _changeToken!;
    }

    private void Refresh()
    {
        var token = _inner.GetChangeToken();
        if (_endpoints != null && _changeToken != null && !_changeToken.HasChanged)
        {
            return;
        }

        _changeToken = token;
        _endpoints = _inner.Endpoints.Select(Protect).ToArray();
    }

    private Endpoint Protect(Endpoint endpoint)
    {
        if (endpoint is not RouteEndpoint route
            || route.Metadata.GetMetadata<SqlOSSurfaceProtectedMetadata>() != null)
        {
            return endpoint;
        }

        foreach (var (surface, options) in _surfaces)
        {
            if (!SqlOSSingleApplicationSurfaces.MatchesRoute(route.RoutePattern, surface.Path))
            {
                continue;
            }

            var next = route.RequestDelegate ?? (_ => Task.CompletedTask);
            var metadata = new EndpointMetadataCollection(
                route.Metadata.Append(new SqlOSSurfaceProtectedMetadata(surface.Audience)));
            return new RouteEndpoint(
                context =>
                {
                    var validator = new SqlOSAccessTokenValidationMiddleware(next, options);
                    return validator.InvokeAsync(
                        context,
                        context.RequestServices.GetRequiredService<SqlOSAuthService>());
                },
                route.RoutePattern,
                route.Order,
                metadata,
                route.DisplayName);
        }

        return endpoint;
    }
}

internal sealed class SqlOSSurfaceProtectedMetadata
{
    public SqlOSSurfaceProtectedMetadata(string audience)
    {
        Audience = audience;
    }

    public string Audience { get; }
}
