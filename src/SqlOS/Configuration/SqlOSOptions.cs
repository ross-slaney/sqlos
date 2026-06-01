using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Email.Configuration;
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
    public SqlOSEmailOptions Email { get; } = new();

    public SqlOSOptions UseSingleApplication(string name, Action<SqlOSSingleApplicationOptions>? configure = null)
    {
        AuthServer.UseSingleApplication(name, configure);
        return this;
    }

    public SqlOSOptions UseSingleApplication(IConfiguration configuration, string sectionName = "SqlOS:Application")
    {
        AuthServer.UseSingleApplication(configuration, sectionName);
        return this;
    }

    public SqlOSOptions ConfigureEmail(Action<SqlOSEmailOptions> configure)
    {
        configure(Email);
        return this;
    }
}

public sealed class SqlOSDashboardOptions
{
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8);

    public SqlOSDashboardAuthMode AuthMode { get; set; } = SqlOSDashboardAuthMode.DevelopmentOnly;
    public string? Password { get; set; }
    public TimeSpan SessionLifetime { get; set; } = DefaultSessionLifetime;
    public SqlOSDashboardLoginThrottlingOptions LoginThrottling { get; } = new();
    public Func<HttpContext, Task<bool>>? AuthorizationCallback { get; set; }
}

public sealed class SqlOSDashboardLoginThrottlingOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxFailuresPerIp { get; set; } = 5;
    public int MaxGlobalFailures { get; set; } = 25;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
}
