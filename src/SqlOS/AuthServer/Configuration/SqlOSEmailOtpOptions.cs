namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSEmailOtpOptions
{
    public string? AzureCommunicationServicesConnectionString { get; set; }
    public string? FromAddress { get; set; }
    public string Subject { get; set; } = "Your SqlOS sign-in code";
    public int CodeLength { get; set; } = 6;
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; set; } = 5;
    public int MaxChallengesPerHour { get; set; } = 5;

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(AzureCommunicationServicesConnectionString)
            && !string.IsNullOrWhiteSpace(FromAddress);
}
