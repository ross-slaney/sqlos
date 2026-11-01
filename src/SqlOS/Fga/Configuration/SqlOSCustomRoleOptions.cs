using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>Host-owned policy for customer role administration. Disabled until all required policy is configured.</summary>
public sealed class SqlOSCustomRoleOptions
{
    public string? ManagePermission { get; set; }
    public HashSet<string> OwnerResourceTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Validate current tenant membership for a recipient (including group/agent eligibility). No ID-prefix inference.</summary>
    public Func<ISqlOSFgaDbContext, string, string, CancellationToken, Task<bool>>? IsEligibleRecipientAsync { get; set; }
}
