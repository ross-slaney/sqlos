namespace SqlOS.AuthServer.Models;

/// <summary>
/// A <see cref="SqlOSUser"/>'s own MFA settings, which override the organization's and the
/// global policy when set: whether the account requires MFA, and whether it may enroll itself.
/// </summary>
/// <remarks>
/// Part of the <see cref="SqlOSUser"/> aggregate. SqlOS sets it in one case: confirming an
/// authenticator opts the account into MFA (<see cref="RequireMfa"/> becomes true unless the
/// account already chose).
/// </remarks>
public sealed class SqlOSUserMfaPolicyOverride
{
    private SqlOSUserMfaPolicyOverride()
    {
    }

    public string UserId { get; private set; } = string.Empty;
    public bool? RequireMfa { get; private set; }
    public bool? UserSelfEnrollmentEnabled { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public SqlOSUser? User { get; private set; }

    /// <summary>The settings of an account that opted into MFA and chose nothing else.</summary>
    internal static SqlOSUserMfaPolicyOverride OptedIn(string userId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new SqlOSUserMfaPolicyOverride
        {
            UserId = userId,
            RequireMfa = true,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Opts the account into MFA unless it already chose whether to require it. Returns true when
    /// this call opted it in.
    /// </summary>
    internal bool OptIn(DateTime now)
    {
        if (RequireMfa != null)
        {
            return false;
        }

        RequireMfa = true;
        UpdatedAt = now;
        return true;
    }
}
