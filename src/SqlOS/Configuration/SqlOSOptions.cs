using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Calendar.Configuration;
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
    public SqlOSBrowserSecurityOptions BrowserSecurity { get; } = new();
    public SqlOSFgaOptions Fga { get; } = new();
    public SqlOSAuthServerOptions AuthServer { get; } = new();
    public SqlOSEmailOptions Email { get; } = new();
    public SqlOSCalendarOptions Calendar { get; } = new();

    /// <summary>
    /// Configures SqlOS for one first-party public PKCE application.
    /// </summary>
    /// <param name="name">The application display name. It is also used to derive the client ID when one is not configured.</param>
    /// <param name="configure">An optional callback that overrides the single-application defaults.</param>
    /// <returns>The same options instance so that additional configuration can be chained.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> is empty or contains only whitespace.</exception>
    /// <remarks>
    /// Single-application mode disables dynamic client registration and resource indicators, and
    /// can apply the application name to the hosted sign-in page and transactional email branding.
    /// It cannot be combined with explicit startup client seeds.
    /// </remarks>
    public SqlOSOptions UseSingleApplication(string name, Action<SqlOSSingleApplicationOptions>? configure = null)
    {
        AuthServer.UseSingleApplication(name, configure);
        return this;
    }

    /// <summary>
    /// Configures SqlOS for one first-party public PKCE application using a configuration section.
    /// </summary>
    /// <param name="configuration">The application configuration containing the single-application settings.</param>
    /// <param name="sectionName">The path of the configuration section to bind.</param>
    /// <returns>The same options instance so that additional configuration can be chained.</returns>
    /// <exception cref="InvalidOperationException">
    /// The requested configuration section does not exist or does not contain a non-empty application name.
    /// </exception>
    /// <remarks>
    /// The section supports <c>Name</c>, <c>Origin</c>, <c>ClientId</c>, <c>Audience</c>,
    /// <c>RedirectPath</c>, <c>RedirectUris</c>, <c>AllowedScopes</c>,
    /// <c>EnabledCredentialTypes</c>, and the single-application branding switches.
    /// </remarks>
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

    public SqlOSOptions ConfigureCalendar(Action<SqlOSCalendarOptions> configure)
    {
        configure(Calendar);
        return this;
    }
}

public sealed class SqlOSBrowserSecurityOptions
{
    /// <summary>
    /// Placeholder replaced with a fresh nonce on every SqlOS HTML response.
    /// </summary>
    public const string NoncePlaceholder = "{nonce}";

    /// <summary>
    /// Content Security Policy applied to hosted SqlOS HTML. SqlOS owns each
    /// surface's <c>frame-ancestors</c> policy; callers cannot relax the anti-framing boundary.
    /// </summary>
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'none'; base-uri 'none'; object-src 'none'; form-action 'self'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; " +
        "script-src 'self' 'nonce-{nonce}'; style-src 'self' 'nonce-{nonce}'";
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
