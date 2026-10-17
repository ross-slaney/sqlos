using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace SqlOS.Hosting;

/// <summary>
/// The endpoints SqlOS maps on the application's behalf. They join the application's route table
/// so one routing pass dispatches both.
/// </summary>
internal sealed class SqlOSEndpointDataSource : EndpointDataSource
{
    private readonly IReadOnlyList<EndpointDataSource> _sources;

    public SqlOSEndpointDataSource(IReadOnlyList<EndpointDataSource> sources)
    {
        _sources = sources;
    }

    public override IReadOnlyList<Endpoint> Endpoints
        => _sources.SelectMany(source => source.Endpoints).ToArray();

    public override IChangeToken GetChangeToken()
        => new CompositeChangeToken(_sources.Select(source => source.GetChangeToken()).ToArray());
}
