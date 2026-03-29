using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Configuration;
using SqlOS.Fga.Configuration;

namespace SqlOS.Configuration;

public enum SqlOSDashboardAuthMode
{
    DevelopmentOnly = 0,
    Password = 1
}

public sealed class SqlOSOptions
{
    public SqlOSOptions()
    {
        AuthServer.BasePath = "/sqlos/auth";
        AuthServer.Issuer = "https://localhost/sqlos/auth";
    }

    public string DashboardBasePath { get; set; } = "/sqlos";
    public SqlOSDashboardOptions Dashboard { get; } = new();
    public SqlOSFgaOptions Fga { get; } = new();
    public SqlOSAuthServerOptions AuthServer { get; } = new();
}

public sealed class SqlOSDashboardOptions
{
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8);

    public SqlOSDashboardAuthMode AuthMode { get; set; } = SqlOSDashboardAuthMode.DevelopmentOnly;
    public string? Password { get; set; }
    public TimeSpan SessionLifetime { get; set; } = DefaultSessionLifetime;
    public Func<HttpContext, Task<bool>>? AuthorizationCallback { get; set; }
}
