namespace SqlOS.AuthServer.Configuration;

using SqlOS.AuthServer.Interfaces;

public sealed class SqlOSEmailOtpOptions
{
    public string? AzureCommunicationServicesConnectionString { get; set; }
    public string? FromAddress { get; set; }
    public string Subject { get; set; } = "Your SqlOS sign-in code";
    public string ApplicationName { get; set; } = "SqlOS";
    public int CodeLength { get; set; } = 6;
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; set; } = 5;
    public int MaxChallengesPerHour { get; set; } = 5;
    public int MaxChallengesPerIpPerHour { get; set; } = 60;
    public int MaxChallengesPerClientPerHour { get; set; } = 300;
    public Func<SqlOSEmailOtpMessageContext, SqlOSAuthEmailMessage>? BuildMessage { get; set; }

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(AzureCommunicationServicesConnectionString)
            && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed record SqlOSEmailOtpMessageContext(
    string Purpose,
    string Email,
    string MaskedEmail,
    string Code,
    DateTime ExpiresAt,
    TimeSpan ChallengeLifetime,
    string ApplicationName);
