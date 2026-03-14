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
        Fga.DashboardPathPrefix = "/sqlos/admin/fga";
        AuthServer.BasePath = "/sqlos/auth";
        AuthServer.Issuer = "https://localhost/sqlos/auth";
    }

    public string DashboardBasePath { get; set; } = "/sqlos";
    public bool EnableFga { get; private set; } = true;
    public bool EnableAuthServer { get; private set; } = true;
    public SqlOSDashboardOptions Dashboard { get; } = new();
    public SqlOSFgaOptions Fga { get; } = new();
    public SqlOSAuthServerOptions AuthServer { get; } = new();

    public SqlOSOptions UseFGA(Action<SqlOSFgaOptions>? configure = null)
    {
        EnableFga = true;
        configure?.Invoke(Fga);
        return this;
    }

    public SqlOSOptions UseAuthServer(Action<SqlOSAuthServerOptions>? configure = null)
    {
        EnableAuthServer = true;
        configure?.Invoke(AuthServer);
        return this;
    }

    public SqlOSOptions DisableFGA()
    {
        EnableFga = false;
        return this;
    }

    public SqlOSOptions DisableAuthServer()
    {
        EnableAuthServer = false;
        return this;
    }
}

public sealed class SqlOSDashboardOptions
{
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8);

    public SqlOSDashboardAuthMode AuthMode { get; set; } = SqlOSDashboardAuthMode.DevelopmentOnly;
    public string? Password { get; set; }
    public TimeSpan SessionLifetime { get; set; } = DefaultSessionLifetime;
    public Func<HttpContext, Task<bool>>? AuthorizationCallback { get; set; }
}
