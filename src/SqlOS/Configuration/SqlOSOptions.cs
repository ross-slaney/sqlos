using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Configuration;
using SqlOS.Fga.Configuration;

namespace SqlOS.Configuration;

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
}

public sealed class SqlOSDashboardOptions
{
    public Func<HttpContext, Task<bool>>? AuthorizationCallback { get; set; }
}
