namespace SqlOS.Fga.Models;

/// <summary>
/// Service account for automated/system access.
/// </summary>
public sealed class SqlOSFgaServiceAccount
{
    private SqlOSFgaServiceAccount()
    {
    }

    internal SqlOSFgaServiceAccount(
        string id,
        string subjectId,
        string clientId,
        string clientSecretHash,
        string? description,
        DateTime? expiresAt,
        string configurationOwner,
        string? configurationSourceKey,
        DateTime now)
    {
        Id = id;
        SubjectId = subjectId;
        ClientId = clientId;
        ClientSecretHash = clientSecretHash;
        Description = description;
        ExpiresAt = expiresAt;
        ConfigurationOwner = configurationOwner;
        ConfigurationSourceKey = configurationSourceKey;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; private set; } = string.Empty;
    public string SubjectId { get; private set; } = string.Empty;
    public string ClientId { get; private set; } = string.Empty;
    public string ClientSecretHash { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public string ConfigurationOwner { get; private set; } = "dashboard";
    public string? ConfigurationSourceKey { get; private set; }
    public string? ConfigurationFingerprint { get; private set; }
    public DateTime? LastReconciledAt { get; private set; }
    public DateTime? ConfigurationOrphanedAt { get; private set; }

    // Navigation
    public SqlOSFgaSubject? Subject { get; private set; }

    internal void Describe(string? description, DateTime now)
        => (Description, UpdatedAt) = (description, now);

    internal void ChangeCredential(string clientId, string clientSecretHash, DateTime now)
        => (ClientId, ClientSecretHash, UpdatedAt) = (clientId, clientSecretHash, now);

    /// <summary>Returns whether the expiry changed.</summary>
    internal bool ChangeExpiry(DateTime? expiresAt, DateTime now)
    {
        UpdatedAt = now;
        var changed = ExpiresAt != expiresAt;
        ExpiresAt = expiresAt;
        return changed;
    }

    internal void RecordUse(DateTime now) => (LastUsedAt, UpdatedAt) = (now, now);

    internal void Touch(DateTime now) => UpdatedAt = now;

    /// <summary>Its code seed was reconciled with <paramref name="fingerprint"/>.</summary>
    internal void Reconciled(string fingerprint, DateTime now)
        => (ConfigurationFingerprint, LastReconciledAt, ConfigurationOrphanedAt, UpdatedAt) = (fingerprint, now, null, now);

    /// <summary>Its code seed no longer exists.</summary>
    internal void Orphaned(DateTime now) => ConfigurationOrphanedAt = now;
}
