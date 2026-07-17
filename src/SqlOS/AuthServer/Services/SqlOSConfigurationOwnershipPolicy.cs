using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Services;

internal static class SqlOSConfigurationOwnershipPolicy
{
    public static SqlOSConfigurationOwnershipDto ToDto(string owner, string? sourceKey, DateTime? lastReconciledAt, string? fingerprint, DateTime? orphanedAt, bool canEmergencyDisable = true)
        => new(owner, sourceKey, lastReconciledAt, fingerprint, IsEditable(owner), canEmergencyDisable, orphanedAt != null);

    public static bool IsEditable(string owner)
        => string.Equals(owner, SqlOSConfigurationOwners.Dashboard, StringComparison.OrdinalIgnoreCase);

    public static void EnsureDashboardEditable(string owner, string resource)
    {
        if (!IsEditable(owner))
        {
            throw new InvalidOperationException($"{resource} is owned by the '{owner}' configuration source. Change its source configuration instead.");
        }
    }

    public static void EnsureCodeOwnership(string owner, string? sourceKey, string expectedSourceKey, string resource)
    {
        if (!string.Equals(owner, SqlOSConfigurationOwners.Code, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceKey, expectedSourceKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot reconcile {resource} from code because it is owned by '{owner}'"
                + (string.IsNullOrWhiteSpace(sourceKey) ? "." : $" with source key '{sourceKey}'.")
                + " Use a different stable key or remove the conflicting record explicitly.");
        }
    }

    public static string Fingerprint(object nonSecretConfiguration)
    {
        var json = JsonSerializer.Serialize(nonSecretConfiguration);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
