namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Field names whose values are secrets, one-time codes, or per-run handles, and the placeholder
/// kind each becomes. The renderer consults this for JSON properties, form fields, query
/// parameters, HTML hidden inputs, and JWT claims, and registers matching values with the
/// <see cref="Scrubber"/>. Anything not listed is still caught by the scrubber's patterns; a role
/// only makes placeholders readable (<c>{refresh-token#1}</c> instead of <c>{token#3}</c>) and
/// covers short values the patterns cannot recognize safely.
/// </summary>
public static class ValueRoles
{
    private static readonly Dictionary<string, string> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "code",
        ["state"] = "state",
        ["nonce"] = "nonce",
        ["code_verifier"] = "pkce-verifier",
        ["codeVerifier"] = "pkce-verifier",
        ["code_challenge"] = "pkce-challenge",
        ["codeChallenge"] = "pkce-challenge",
        ["access_token"] = "access-token",
        ["accessToken"] = "access-token",
        ["refresh_token"] = "refresh-token",
        ["refreshToken"] = "refresh-token",
        ["id_token"] = "id-token",
        ["idToken"] = "id-token",
        ["id_token_hint"] = "id-token",
        ["device_code"] = "device-code",
        ["deviceCode"] = "device-code",
        ["user_code"] = "user-code",
        ["userCode"] = "user-code",
        ["client_secret"] = "client-secret",
        ["clientSecret"] = "client-secret",
        ["registration_access_token"] = "registration-token",
        ["__RequestVerificationToken"] = "csrf",
        ["csrfToken"] = "csrf",
        ["consentToken"] = "consent-token",
        ["pendingToken"] = "pending-token",
        ["pendingAuthToken"] = "pending-token",
        ["mfaToken"] = "mfa-token",
        ["challengeToken"] = "challenge-token",
        ["signupToken"] = "signup-token",
        ["enrollmentToken"] = "enrollment-token",
        ["invitationToken"] = "invitation-token",
        ["resetToken"] = "reset-token",
        ["verificationToken"] = "verification-token",
        ["token"] = "link-token",
        ["scimToken"] = "scim-token",
        ["bearerToken"] = "scim-token",
        ["secret"] = "totp-secret",
        ["cursor"] = "cursor",
        ["nextCursor"] = "cursor",
        ["previousCursor"] = "cursor",
        ["password"] = "password",
        ["newPassword"] = "password",
        ["currentPassword"] = "password",
        ["otp"] = "otp",
        ["recoveryCode"] = "recovery-code",
        ["at_hash"] = "hash",
        ["c_hash"] = "hash",
        ["s_hash"] = "hash",
        ["jti"] = "jti",
        ["kid"] = "kid",
        ["x5t"] = "hash",
        ["fingerprint"] = "hash",
        ["SAMLRequest"] = "saml-request",
        ["SAMLResponse"] = "saml-response",
        ["RelayState"] = "relay-state",
        ["session_state"] = "session-state",
        ["ui_context"] = "ui-context"
    };

    private static readonly HashSet<string> ArrayRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "recoveryCodes"
    };

    /// <summary>Returns the placeholder kind for a field, or null when the field has no secret role.</summary>
    public static string? KindFor(string fieldName)
        => Roles.TryGetValue(fieldName, out var kind) ? kind : null;

    /// <summary>Returns the kind for each element of an array-valued field (for example recovery codes).</summary>
    public static string? ElementKindFor(string fieldName)
        => ArrayRoles.Contains(fieldName) ? "recovery-code" : null;
}
