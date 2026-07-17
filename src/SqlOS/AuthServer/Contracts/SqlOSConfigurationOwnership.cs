namespace SqlOS.AuthServer.Contracts;

/// <summary>Identifies the control plane that owns a persisted configuration record.</summary>
public static class SqlOSConfigurationOwners
{
    public const string Code = "code";
    public const string Dashboard = "dashboard";
    public const string Dynamic = "dynamic";
    public const string System = "system";
    public const string External = "external";
}

/// <summary>Ownership and reconciliation state exposed consistently by administration surfaces.</summary>
public sealed record SqlOSConfigurationOwnershipDto(
    string Owner,
    string? SourceKey,
    DateTime? LastReconciledAt,
    string? ConfigurationFingerprint,
    bool IsEditable,
    bool CanEmergencyDisable,
    bool IsOrphaned);
