using SqlOS.AuthServer.Interfaces;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSPasswordResetOptions
{
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromHours(1);
    public int MaxRequestsPerEmailPerWindow { get; set; } = 5;
    public int MaxRequestsPerIpPerWindow { get; set; } = 60;
    public int MaxRequestsPerClientPerWindow { get; set; } = 300;
    public string Subject { get; set; } = "Reset your {applicationName} password";
    /// <summary>
    /// Builds the reset URL from trusted server-side configuration. The returned value must be an
    /// absolute HTTPS URL (or loopback HTTP URL for development) without user information.
    /// <see cref="SqlOSPasswordResetUrlContext.ClientId"/>
    /// is populated only for active first-party clients resolved by SqlOS.
    /// </summary>
    public Func<SqlOSPasswordResetUrlContext, string>? BuildResetUrl { get; set; }
    public Func<SqlOSPasswordResetMessageContext, SqlOSAuthEmailMessage>? BuildMessage { get; set; }
}

public sealed record SqlOSPasswordResetUrlContext(
    string Token,
    string Email,
    string MaskedEmail,
    DateTime ExpiresAt,
    TimeSpan TokenLifetime,
    string? ClientId);

public sealed record SqlOSPasswordResetMessageContext(
    string ApplicationName,
    string Email,
    string MaskedEmail,
    string ResetUrl,
    DateTime ExpiresAt,
    TimeSpan TokenLifetime)
{
    public SqlOSAuthEmailBranding Branding { get; init; } = SqlOSAuthEmailBranding.Default;
}
