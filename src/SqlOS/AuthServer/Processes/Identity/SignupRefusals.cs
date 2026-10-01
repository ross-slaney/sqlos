using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>The refusals every sign-up shares, with their 7.x public messages.</summary>
internal static class SignupRefusals
{
    public static readonly IdentityRefusal DisplayNameRequired = new("display_name_required", "Display name is required.");

    /// <summary>
    /// The join policy's refusal when a sign-up without an invitation names an existing
    /// organization (<see cref="SqlOSSignupJoinPolicy"/>); null when it names none.
    /// </summary>
    public static IdentityRefusal? ForOrganizationJoin(string? organizationId)
        => string.IsNullOrWhiteSpace(organizationId)
            ? null
            : new IdentityRefusal("organization_join_refused", SqlOSSignupJoinPolicy.UnauthorizedOrganizationJoinMessage);
}
