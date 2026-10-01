using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// An identity process refused the request: a stable <see cref="Code"/> and the exact 7.x public
/// message (<c>docs/architecture/domain-model.md</c> §8).
/// </summary>
/// <remarks>
/// A refusal the process decides is an outcome, not an exception. The surfaces map it exactly as
/// 7.x mapped the exception it replaces (through <c>SqlOSPublicAuthErrorMapper</c>), and the public
/// facades throw <see cref="ToException"/>, their 7.x contract. A collaborator another layer owns
/// (the hub, invitations, the admission gate) still refuses by throwing until its layer returns
/// outcomes.
/// </remarks>
internal sealed record IdentityRefusal(string Code, string Message)
{
    /// <summary>The 7.x exception for this refusal.</summary>
    public InvalidOperationException ToException() => new(Message);

    public override string ToString() => $"{nameof(IdentityRefusal)}({Code})";
}

/// <summary>The refusals the identity processes decide, with their 7.x public messages.</summary>
internal static class IdentityRefusals
{
    public static readonly IdentityRefusal EmailRequired = new("email_required", "Email address is required.");
    public static readonly IdentityRefusal PasswordLoginDisabled = new("password_login_disabled", "Local password authentication is disabled.");
    public static readonly IdentityRefusal PasswordSignupDisabled = new("password_signup_disabled", "Password signup is disabled.");
    public static readonly IdentityRefusal EmailNotVerified = new("email_not_verified", "Email must be verified before password login.");

    /// <summary>The one answer to a wrong password, an unknown address, a missing password and an inactive account.</summary>
    public static readonly IdentityRefusal InvalidCredentials = new("invalid_credentials", SqlOSPasswordLoginAbuseService.PublicFailureMessage);

    public static readonly IdentityRefusal EmailCodesUnavailable = new("email_codes_unavailable", "Email sign-in is unavailable.");
    public static readonly IdentityRefusal InvalidCode = new("invalid_code", "The sign-in code is invalid or expired.");
    public static readonly IdentityRefusal SignInLinksUnavailable = new("sign_in_links_unavailable", "Magic-link sign-in is unavailable.");
    public static readonly IdentityRefusal InvalidLink = new("invalid_link", "The sign-in link is invalid or expired.");
    public static readonly IdentityRefusal PhoneCodesUnavailable = new("phone_codes_unavailable", "Phone sign-in is unavailable.");
    public static readonly IdentityRefusal PhoneSignupWithInvitation = new("phone_signup_with_invitation", "Phone signup is not available for email invitations.");
    public static readonly IdentityRefusal PhoneNumberTaken = new("phone_number_taken", "An account already exists for this phone number. Sign in with a phone code instead.");
    public static readonly IdentityRefusal InvitationSignupNeedsEmailCodes = new("invitation_signup_needs_email_codes", "Invitation signup without a password requires Email OTP to be enabled.");
    public static readonly IdentityRefusal InvitedAccountMissing = new("invited_account_missing", "Create an account to accept this invitation.");
    public static readonly IdentityRefusal InvitedAccountInactive = new("invited_account_inactive", "This invited account is inactive. Contact the workspace admin.");
}
