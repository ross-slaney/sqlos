using SqlOS.AuthServer.Interfaces;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSInvitationOptions
{
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromDays(7);
    public int MaxInvitationsPerEmailPerHour { get; set; } = 10;
    public int MaxInvitationsPerIpPerHour { get; set; } = 100;
    public int MaxInvitationsPerOrganizationPerHour { get; set; } = 100;
    public int MaxInvitationsPerInviterPerHour { get; set; } = 50;
    public string? ApplicationName { get; set; }
    public Func<SqlOSInvitationMessageContext, SqlOSAuthEmailMessage>? BuildMessage { get; set; }
}

public sealed record SqlOSInvitationMessageContext(
    string ApplicationName,
    string OrganizationName,
    string Email,
    string MaskedEmail,
    string Role,
    string AcceptUrl,
    DateTime ExpiresAt,
    TimeSpan Lifetime)
{
    public SqlOSAuthEmailBranding Branding { get; init; } = SqlOSAuthEmailBranding.Default;
}
