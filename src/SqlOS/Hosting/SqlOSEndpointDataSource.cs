using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace SqlOS.Hosting;

/// <summary>
/// The endpoints SqlOS maps on the application's behalf. The auth-server/admin routes are withheld
/// while <see cref="SqlOSEndpointMappingState.MappedByApplication"/> is set, so an obsolete
/// manual <c>MapAuthServer()</c> call anywhere in application startup never
/// produces duplicate routes, whether it runs before or after the SqlOS startup filter.
/// </summary>
internal sealed class SqlOSEndpointDataSource : EndpointDataSource
{
    private readonly SqlOSEndpointMappingState _state;
    private readonly IReadOnlyList<EndpointDataSource> _core;
    private readonly IReadOnlyList<EndpointDataSource> _shared;

    public SqlOSEndpointDataSource(
        SqlOSEndpointMappingState state,
        IReadOnlyList<EndpointDataSource> core,
        IReadOnlyList<EndpointDataSource> shared)
    {
        _state = state;
        _core = core;
        _shared = shared;
    }

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            var sources = _state.MappedByApplication ? _shared : _core.Concat(_shared);
            return sources.SelectMany(source => source.Endpoints).ToArray();
        }
    }

    public override IChangeToken GetChangeToken()
        => new CompositeChangeToken(
            _core.Concat(_shared).Select(source => source.GetChangeToken())
                .Append(_state.GetChangeToken())
                .ToArray());
}
