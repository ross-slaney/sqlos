using Microsoft.AspNetCore.Routing;

namespace SqlOS.Extensions;

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthServer(this IEndpointRouteBuilder endpoints, string? pathPrefix = null)
    {
        SqlOS.AuthServer.Extensions.EndpointRouteBuilderExtensions.MapAuthServer(endpoints, pathPrefix);
        return endpoints;
    }
}
