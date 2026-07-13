namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSMfaOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowUserSelfEnrollmentByDefault { get; set; } = true;
    public bool RecoveryCodesEnabledByDefault { get; set; } = true;
    public bool RequireForAllUsersByDefault { get; set; }
    public bool RequireForOwnersAndAdminsByDefault { get; set; }
    public List<string> RequiredRolesByDefault { get; set; } = ["owner", "admin"];
    public List<string> AvailableFactorsByDefault { get; set; } = ["totp", "recovery_code"];
    public SqlOSTotpMfaOptions Totp { get; } = new();

    public SqlOSMfaOptions Disable()
    {
        Enabled = false;
        return this;
    }
}

public sealed class SqlOSTotpMfaOptions
{
    public bool Enabled { get; set; } = true;
    public string Issuer { get; set; } = "SqlOS";
    public string Algorithm { get; set; } = "SHA1";
    public int Digits { get; set; } = 6;
    public int PeriodSeconds { get; set; } = 30;
    public int AllowedClockSkewSteps { get; set; } = 1;
    public int SecretBytes { get; set; } = 20;
    public int RecoveryCodeCount { get; set; } = 10;
    public TimeSpan EnrollmentTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ChallengeTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxFailedAttemptsPerChallenge { get; set; } = 5;
    public int MaxFailedAttemptsPerUser { get; set; } = 10;
    public int MaxFailedAttemptsPerIp { get; set; } = 25;
    public TimeSpan FailedAttemptWindow { get; set; } = TimeSpan.FromMinutes(10);
}
