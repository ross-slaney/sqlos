namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSDeviceAuthorizationOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(15);

    public int PollingIntervalSeconds { get; set; } = 5;

    public int MaxStartsPerClientPerHour { get; set; } = 120;

    public int MaxStartsPerIpPerHour { get; set; } = 60;

    public string UserCodeAlphabet { get; set; } = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public int UserCodeSegmentLength { get; set; } = 4;

    public int UserCodeSegmentCount { get; set; } = 2;
}
