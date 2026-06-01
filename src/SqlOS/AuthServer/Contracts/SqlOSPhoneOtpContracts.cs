using System.Text.Json.Nodes;

namespace SqlOS.AuthServer.Contracts;

public sealed record SqlOSPhoneOtpStartRequest(
    string PhoneNumber,
    string ClientId,
    string? OrganizationId);

public sealed record SqlOSPhoneOtpSignupStartRequest(
    string DisplayName,
    string PhoneNumber,
    string ClientId,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);

public sealed record SqlOSPhoneOtpStartResult(
    string ChallengeToken,
    string PhoneNumber,
    string MaskedPhoneNumber,
    string Message,
    DateTime ExpiresAt,
    DateTime NextAllowedSendAt);

public sealed record SqlOSPhoneOtpSignupStartResult(
    string ChallengeToken,
    string SignupToken,
    string PhoneNumber,
    string MaskedPhoneNumber,
    string Message,
    DateTime ExpiresAt,
    DateTime NextAllowedSendAt);

public sealed record SqlOSPhoneOtpVerifyRequest(
    string ChallengeToken,
    string Code);

public sealed record SqlOSPhoneOtpSignupVerifyRequest(
    string SignupToken,
    string ChallengeToken,
    string Code);

public sealed record SqlOSPhoneOtpEnrollmentStartRequest(
    string PhoneNumber);

public sealed record SqlOSPhoneOtpEnrollmentVerifyRequest(
    string ChallengeToken,
    string Code);
