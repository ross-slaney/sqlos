using Microsoft.AspNetCore.Http;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSClientRegistrationOptions
{
    public SqlOSCimdOptions Cimd { get; } = new();
    public SqlOSDynamicClientRegistrationOptions Dcr { get; } = new();
}

public sealed class SqlOSCimdOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxMetadataBytes { get; set; } = 256 * 1024;
    public List<string> TrustedHosts { get; } = [];
    public Func<SqlOSCimdTrustContext, CancellationToken, Task<SqlOSClientRegistrationPolicyDecision>>? TrustPolicy { get; set; }
}

public sealed class SqlOSDynamicClientRegistrationOptions
{
    public bool Enabled { get; set; }
    public bool RequirePkce { get; set; } = true;
    public bool PublicClientsOnly { get; set; } = true;
    public bool AllowHttpsRedirectUris { get; set; } = true;
    public bool AllowLoopbackRedirectUris { get; set; } = true;
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);
    public int MaxRegistrationsPerWindow { get; set; } = 25;
    public bool EnableAutomaticCleanup { get; set; } = true;
    public TimeSpan StaleClientRetention { get; set; } = TimeSpan.FromDays(30);
    public Func<SqlOSDynamicClientRegistrationPolicyContext, CancellationToken, Task<SqlOSClientRegistrationPolicyDecision>>? Policy { get; set; }
}

public sealed class SqlOSResourceIndicatorOptions
{
    public bool Enabled { get; set; } = true;
    public bool PreserveClientAudienceFallback { get; set; } = true;
    public bool EnforceOnTokenExchange { get; set; } = true;
    public bool PreserveOriginalBindingOnRefresh { get; set; } = true;
}

public sealed record SqlOSClientRegistrationPolicyDecision(
    bool Allowed,
    string? Reason = null)
{
    public static SqlOSClientRegistrationPolicyDecision Allow() => new(true);

    public static SqlOSClientRegistrationPolicyDecision Deny(string? reason = null) => new(false, reason);
}

public sealed class SqlOSCimdTrustContext
{
    public required HttpContext HttpContext { get; init; }
    public required string ClientId { get; init; }
    public required Uri ClientIdUri { get; init; }
    public string? ClientName { get; init; }
    public IReadOnlyList<string> RedirectUris { get; init; } = Array.Empty<string>();
    public string? TokenEndpointAuthMethod { get; init; }
}

public sealed class SqlOSDynamicClientRegistrationPolicyContext
{
    public required HttpContext HttpContext { get; init; }
    public string? ClientName { get; init; }
    public IReadOnlyList<string> RedirectUris { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GrantTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ResponseTypes { get; init; } = Array.Empty<string>();
    public string? TokenEndpointAuthMethod { get; init; }
    public string? SoftwareId { get; init; }
    public string? SoftwareVersion { get; init; }
}
