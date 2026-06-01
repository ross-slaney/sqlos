namespace SqlOS.AuthServer.Interfaces;

public interface ISqlOSOtpDeliveryChannel
{
    Task<SqlOSOtpDeliveryStartResult> StartAsync(
        string e164PhoneNumber,
        SqlOSOtpDeliveryContext context,
        CancellationToken cancellationToken = default);

    Task<SqlOSOtpDeliveryCheckResult> CheckAsync(
        string e164PhoneNumber,
        string code,
        SqlOSOtpDeliveryContext context,
        CancellationToken cancellationToken = default);
}

public sealed record SqlOSOtpDeliveryContext(
    string Purpose,
    string? ClientApplicationId,
    string? AuthorizationRequestId,
    string? IpAddress,
    string? UserAgent);

public sealed record SqlOSOtpDeliveryStartResult(
    bool Accepted,
    string Provider,
    string? ProviderChallengeId,
    string? ProviderStatus,
    string? SanitizedError = null);

public sealed record SqlOSOtpDeliveryCheckResult(
    bool Approved,
    string Provider,
    string? ProviderChallengeId,
    string? ProviderStatus,
    string? SanitizedError = null);
