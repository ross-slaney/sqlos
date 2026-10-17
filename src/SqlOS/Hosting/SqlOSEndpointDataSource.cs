using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace SqlOS.Hosting;

/// <summary>
/// The endpoints SqlOS maps on the application's behalf. Only the auth-server routes are withheld
/// while <see cref="SqlOSEndpointMappingState.MappedByApplication"/> is set, so an obsolete
/// manual <c>MapAuthServer()</c> call never registers those routes twice. Audit, email, and
/// calendar routes stay mapped, whether the call runs before or after the SqlOS startup filter.
/// </summary>
internal sealed class SqlOSEndpointDataSource : EndpointDataSource
{
    private readonly SqlOSEndpointMappingState _state;
    private readonly IReadOnlyList<EndpointDataSource> _authServer;
    private readonly IReadOnlyList<EndpointDataSource> _modules;
    private readonly IReadOnlyList<EndpointDataSource> _shared;

    public SqlOSEndpointDataSource(
        SqlOSEndpointMappingState state,
        IReadOnlyList<EndpointDataSource> authServer,
        IReadOnlyList<EndpointDataSource> modules,
        IReadOnlyList<EndpointDataSource> shared)
    {
        _state = state;
        _authServer = authServer;
        _modules = modules;
        _shared = shared;
    }

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            IEnumerable<EndpointDataSource> sources = _modules.Concat(_shared);
            if (!_state.MappedByApplication)
            {
                sources = _authServer.Concat(sources);
            }

            return sources.SelectMany(source => source.Endpoints).ToArray();
        }
    }

    public override IChangeToken GetChangeToken()
        => new CompositeChangeToken(
            _authServer.Concat(_modules).Concat(_shared).Select(source => source.GetChangeToken())
                .Append(_state.GetChangeToken())
                .ToArray());
}
