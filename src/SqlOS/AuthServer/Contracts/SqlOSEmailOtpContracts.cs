using System.Text.Json.Nodes;

namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSEmailOtpStartRequest(
    string Email,
    string ClientId,
    string? OrganizationId);

public sealed record SqlOSEmailOtpSignupStartRequest(
    string DisplayName,
    string Email,
    string ClientId,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);

public sealed record SqlOSEmailOtpStartResult(
    string ChallengeToken,
    string Email,
    string MaskedEmail,
    string Message,
    DateTime ExpiresAt,
    DateTime NextAllowedSendAt);

public sealed record SqlOSEmailOtpSignupStartResult(
    string ChallengeToken,
    string SignupToken,
    string Email,
    string MaskedEmail,
    string Message,
    DateTime ExpiresAt,
    DateTime NextAllowedSendAt);

public sealed record SqlOSEmailOtpVerifyRequest(
    string ChallengeToken,
    string Code);

public sealed record SqlOSEmailOtpSignupVerifyRequest(
    string SignupToken,
    string ChallengeToken,
    string Code);

public sealed record SqlOSResolvedCredentialSettings(
    string[] EnabledCredentialTypes,
    bool PasswordEnabled,
    bool PasswordSignupEnabled,
    bool EmailOtpEnabled);
