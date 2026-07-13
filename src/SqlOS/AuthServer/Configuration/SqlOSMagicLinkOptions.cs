using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Interfaces;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSMagicLinkOptions
{
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromHours(1);
    public int MaxLinksPerEmailPerWindow { get; set; } = 5;
    public int MaxLinksPerIpPerWindow { get; set; } = 60;
    public int MaxLinksPerClientPerWindow { get; set; } = 300;
    public string Subject { get; set; } = "Sign in to {applicationName}";
    public string ApplicationName { get; set; } = "SqlOS";
    public Func<SqlOSMagicLinkUrlContext, string>? BuildLoginUrl { get; set; }
    public Func<SqlOSMagicLinkMessageContext, SqlOSAuthEmailMessage>? BuildMessage { get; set; }
}

public sealed record SqlOSMagicLinkUrlContext(
    string Token,
    string Email,
    string MaskedEmail,
    DateTime ExpiresAt,
    TimeSpan TokenLifetime,
    HttpContext? HttpContext);

public sealed record SqlOSMagicLinkMessageContext(
    string ApplicationName,
    string Email,
    string MaskedEmail,
    string LoginUrl,
    DateTime ExpiresAt,
    TimeSpan TokenLifetime)
{
    public SqlOSAuthEmailBranding Branding { get; init; } = SqlOSAuthEmailBranding.Default;
}
