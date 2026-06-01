namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSPhoneOtpOptions
{
    public bool Enabled { get; set; }
    public string? TwilioAccountSid { get; set; }
    public string? TwilioAuthToken { get; set; }
    public string? TwilioVerifyServiceSid { get; set; }
    public string DefaultRegion { get; set; } = "US";
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromHours(1);
    public int MaxSendsPerPhone { get; set; } = 5;
    public int MaxSendsPerAccount { get; set; } = 5;
    public int MaxSendsPerIp { get; set; } = 60;
    public int MaxSendsPerClient { get; set; } = 300;
    public string[] CountryAllowList { get; set; } = [];
    public string[] CountryDenyList { get; set; } = [];
    public bool SatisfiesMfa { get; set; }

    public bool HasTwilioCredentials
        => !string.IsNullOrWhiteSpace(TwilioAccountSid)
            && !string.IsNullOrWhiteSpace(TwilioAuthToken);

    public bool HasCompleteTwilioConfiguration
        => !string.IsNullOrWhiteSpace(TwilioVerifyServiceSid)
            && HasTwilioCredentials;

    public bool IsConfigured => Enabled && HasCompleteTwilioConfiguration;
}
