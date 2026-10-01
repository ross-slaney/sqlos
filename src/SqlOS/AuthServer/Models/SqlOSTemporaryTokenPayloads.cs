using System.Text.Json.Nodes;

namespace SqlOS.AuthServer.Models;

// The payloads of SqlOS's temporary token kinds (SqlOSTemporaryTokenKinds). Each is stored as JSON
// with System.Text.Json's defaults, so its member names and their order are the stored format:
// renaming, reordering or retyping a member changes tokens already issued. A member added later
// needs a default, so tokens minted before it still read.

/// <summary>A password-reset link: the address it was sent to.</summary>
internal sealed record PasswordResetPayload(string EmailId, string NormalizedEmail);

/// <summary>A record of one password-reset request, kept for the request window.</summary>
internal sealed record PasswordResetRequestPayload(
    string NormalizedEmail,
    string? IpAddress,
    string? ClientKey,
    string Surface);

/// <summary>An email-verification link: the address to verify.</summary>
internal sealed record EmailVerificationPayload(string EmailId);

/// <summary>A direct-login sign-in waiting for its organization to be chosen.</summary>
internal sealed record PendingAuthPayload(string ClientId, string AuthenticationMethod);

/// <summary>
/// A hosted sign-in waiting for its organization to be chosen. <c>AuthenticatedAt</c> and
/// <c>CredentialSignIn</c> default so tokens minted before they existed still read; null means
/// "unknown" and issuance falls back to its own resolution.
/// </summary>
internal sealed record PendingAuthorizationPayload(
    string AuthorizationRequestId,
    string AuthenticationMethod,
    DateTime? AuthenticatedAt = null,
    bool CredentialSignIn = false);

/// <summary>
/// A hosted sign-in waiting for the user's consent to a third-party client.
/// <c>AuthenticatedAt</c>, <c>ClientMetadataFingerprint</c> and <c>CredentialSignIn</c> default so
/// consent tokens minted before they existed still read: a null <c>AuthenticatedAt</c> falls back
/// to issuance-time resolution, a null fingerprint skips the staleness check, and a missing
/// <c>CredentialSignIn</c> keeps the presented-session check.
/// </summary>
internal sealed record PendingConsentPayload(
    string AuthorizationRequestId,
    string AuthenticationMethod,
    DateTime? AuthenticatedAt = null,
    string? ClientMetadataFingerprint = null,
    bool CredentialSignIn = false);

/// <summary>The handles a hosted authorization continues with after an interstitial step.</summary>
internal sealed record AuthorizationContinuationPayload(
    string AuthorizationRequestId,
    string? MfaToken,
    string? PendingToken,
    string? ConsentToken = null);

/// <summary>A sign-in link: where it was sent and the sign-in context it completes.</summary>
internal sealed record MagicLinkPayload(
    string Email,
    string NormalizedEmail,
    string MaskedEmail,
    string? UserEmailId,
    string? AuthorizationRequestId,
    string? ClientApplicationId,
    string? RequestedOrganizationId,
    string? IpAddress,
    string? UserAgent,
    bool Sent);

/// <summary>An email-code signup waiting for its code: the challenge it is bound to and the account to create.</summary>
internal sealed record EmailOtpSignupPayload(
    string ChallengeTokenHash,
    string? AuthorizationRequestId,
    string? ClientId,
    string? ClientApplicationId,
    string DisplayName,
    string Email,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);

/// <summary>A phone-code signup waiting for its code: the challenge it is bound to and the account to create.</summary>
internal sealed record PhoneOtpSignupPayload(
    string ChallengeTokenHash,
    string? AuthorizationRequestId,
    string? ClientId,
    string? ClientApplicationId,
    string DisplayName,
    string PhoneNumber,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);

/// <summary>An upstream OIDC sign-in started for a browser client (the provider's state).</summary>
internal sealed record OidcBrowserRequestPayload(
    string ClientId,
    string RedirectUri,
    string State,
    string CodeChallenge,
    string CodeChallengeMethod,
    string ConnectionId,
    string? Email,
    string ProviderNonce,
    string ProviderCodeVerifier,
    string CallbackUri);

/// <summary>An upstream OIDC sign-in started from a hosted authorization request (the provider's state).</summary>
internal sealed record OidcAuthorizationRequestPayload(
    string AuthorizationRequestId,
    string ConnectionId,
    string ProviderNonce,
    string ProviderCodeVerifier,
    string CallbackUri,
    string? Email);

/// <summary>The code a browser client exchanges after an upstream OIDC sign-in.</summary>
internal sealed record OidcBrowserCodePayload(
    string ClientId,
    string RedirectUri,
    string CodeChallenge,
    string CodeChallengeMethod,
    string AuthenticationMethod);

/// <summary>An authenticator enrollment waiting for its first code.</summary>
internal sealed record TotpEnrollmentPayload(
    string AuthenticatorId,
    TotpEnrollmentChallengeBinding? ChallengeBinding = null);

/// <summary>The MFA challenge an enrollment started from must still be the one that finishes it.</summary>
internal sealed record TotpEnrollmentChallengeBinding(
    string ChallengeTokenId,
    string UserId,
    string ClientApplicationId,
    string? OrganizationId,
    string Flow,
    string ClientId,
    string? AuthorizationRequestId,
    string? Resource);

/// <summary>
/// An issuer-session cookie. <c>AuthenticatedAt</c> defaults so cookies minted before it existed
/// still read; a default value means "unknown" and falls back to the token's <c>CreatedAt</c>.
/// </summary>
internal sealed record IssuerSessionPayload(string AuthenticationMethod, DateTime AuthenticatedAt = default);
