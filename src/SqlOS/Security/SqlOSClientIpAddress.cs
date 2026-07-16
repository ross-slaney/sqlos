using Microsoft.AspNetCore.Http;

namespace SqlOS.Security;

internal static class SqlOSClientIpAddress
{
    public const string Unknown = "unknown";

    public static string Get(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? Unknown;
}
