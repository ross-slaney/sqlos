using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The one admission gate a process passes before it spends something an attacker could abuse:
/// a delivery (an email code, a sign-in link, a phone code or a password-reset email), an MFA
/// factor comparison, or a password comparison. Every admission is decided before the work it
/// admits, atomically across replicas.
/// </summary>
/// <remarks>
/// The gate composes the admission services and keeps their algorithms: delivery buckets
/// (<see cref="SqlOSDeliveryAdmissionService"/>), MFA-attempt reservations
/// (<see cref="SqlOSMfaAttemptAdmissionService"/>) and password-login reservations
/// (<see cref="SqlOSPasswordLoginAbuseService"/>). It reads where a request came from as plain
/// values (<see cref="AdmissionOrigin"/>), never from the HTTP request, and picks each kind's
/// limits from the SqlOS options.
/// </remarks>
internal interface IAdmissionGate
{
    /// <summary>Admits one email code for the address, or names the limit it reached.</summary>
    Task<DeliveryAdmission> AdmitEmailCodeAsync(
        EmailAddress address,
        AdmissionOrigin origin,
        string? clientApplicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Admits one sign-in link for the address, or names the limit it reached.</summary>
    Task<DeliveryAdmission> AdmitSignInLinkAsync(
        EmailAddress address,
        AdmissionOrigin origin,
        string? clientApplicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Admits one phone code for the number (by its hash), or names the limit it reached.</summary>
    Task<DeliveryAdmission> AdmitPhoneCodeAsync(
        string phoneNumberHash,
        string? userId,
        AdmissionOrigin origin,
        string? clientApplicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Admits one password-reset email for the address, or names the limit it reached.</summary>
    Task<DeliveryAdmission> AdmitPasswordResetEmailAsync(
        string normalizedEmail,
        string? userId,
        AdmissionOrigin origin,
        string? clientKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Gives back an admitted delivery that was never made, such as a resend the cooldown refused.</summary>
    Task WithdrawAsync(DeliveryAdmission admission, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves one comparison of an MFA factor against <paramref name="challenge"/> and returns the
    /// reservation to settle; throws the public MFA failure when a budget is spent.
    /// </summary>
    Task<string> AdmitMfaAttemptAsync(
        SqlOSTemporaryToken challenge,
        AdmissionOrigin origin,
        string? authorizationRequestId,
        CancellationToken cancellationToken);

    /// <summary>Settles an MFA reservation whose factor was wrong: the attempt stays counted.</summary>
    Task RecordMfaAttemptFailedAsync(string reservationId, CancellationToken cancellationToken);

    /// <summary>Settles an MFA reservation whose factor was right: the attempt is given back.</summary>
    Task RecordMfaAttemptSucceededAsync(string reservationId, CancellationToken cancellationToken);

    /// <summary>True when the user's MFA budget admits no further challenge.</summary>
    Task<bool> IsMfaBudgetExhaustedAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Describes a password comparison for an address, before it is admitted.</summary>
    SqlOSPasswordLoginAttempt BeginPasswordAttempt(
        string normalizedEmail,
        AdmissionOrigin origin,
        string? clientKey = null,
        string? authorizationRequestId = null,
        string? surface = null,
        string? userId = null);

    /// <summary>
    /// Reserves the password comparison in every applicable bucket; records the refusal and throws
    /// the public password failure when a bucket is locked.
    /// </summary>
    Task AdmitPasswordAttemptAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken);

    /// <summary>Settles a password comparison that failed, locking the buckets it exhausts.</summary>
    Task RecordPasswordAttemptFailedAsync(SqlOSPasswordLoginAttempt attempt, string failureReason, CancellationToken cancellationToken);

    /// <summary>Settles a password comparison that succeeded.</summary>
    Task RecordPasswordAttemptSucceededAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>Where an admitted request came from, as the admission buckets key it.</summary>
/// <param name="IpAddress">The client IP address.</param>
/// <param name="UserAgent">The raw <c>User-Agent</c> header: empty when the request sent none.</param>
internal readonly record struct AdmissionOrigin(string? IpAddress, string? UserAgent)
{
    /// <summary>No request: host code and background work.</summary>
    public static AdmissionOrigin None => default;

    /// <summary>The 7.x reading of a request: the connection's remote address and its <c>User-Agent</c> header.</summary>
    public static AdmissionOrigin Of(HttpContext? httpContext)
        => new(httpContext?.Connection.RemoteIpAddress?.ToString(), httpContext?.Request.Headers.UserAgent.ToString());

    /// <summary>The origin of the request a unit of work serves.</summary>
    public static AdmissionOrigin Of(SqlOSRequestContext request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AdmissionOrigin(request.IpAddress, request.UserAgent);
    }
}

/// <summary>The admission gate over SqlOS's admission services.</summary>
internal sealed class SqlOSAdmissionGate : IAdmissionGate
{
    private readonly SqlOSDeliveryAdmissionService _deliveries;
    private readonly SqlOSMfaAttemptAdmissionService _mfaAttempts;
    private readonly SqlOSPasswordLoginAbuseService _passwordAttempts;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSAdmissionGate(
        SqlOSDeliveryAdmissionService deliveries,
        SqlOSMfaAttemptAdmissionService mfaAttempts,
        SqlOSPasswordLoginAbuseService passwordAttempts,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _deliveries = deliveries;
        _mfaAttempts = mfaAttempts;
        _passwordAttempts = passwordAttempts;
        _options = options.Value;
    }

    /// <summary>
    /// The gate a service builds from its own dependencies, using the admission services it was
    /// given and building the others as dependency injection would.
    /// </summary>
    public static SqlOSAdmissionGate Create(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSDeliveryAdmissionService? deliveries = null,
        SqlOSMfaAttemptAdmissionService? mfaAttempts = null,
        SqlOSPasswordLoginAbuseService? passwordAttempts = null)
        => new(
            deliveries ?? SqlOSDeliveryAdmissionService.For(context, options),
            mfaAttempts ?? new SqlOSMfaAttemptAdmissionService(context, cryptoService, options),
            passwordAttempts ?? new SqlOSPasswordLoginAbuseService(context, adminService, cryptoService, options),
            options);

    public Task<DeliveryAdmission> AdmitEmailCodeAsync(
        EmailAddress address,
        AdmissionOrigin origin,
        string? clientApplicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _deliveries.AdmitEmailOtpAsync(address.Canonical, origin.IpAddress, clientApplicationId, _options.EmailOtp, now, cancellationToken);

    public Task<DeliveryAdmission> AdmitSignInLinkAsync(
        EmailAddress address,
        AdmissionOrigin origin,
        string? clientApplicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _deliveries.AdmitMagicLinkAsync(address.Canonical, origin.IpAddress, clientApplicationId, _options.MagicLink, now, cancellationToken);

    public Task<DeliveryAdmission> AdmitPhoneCodeAsync(
        string phoneNumberHash,
        string? userId,
        AdmissionOrigin origin,
        string? clientApplicationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _deliveries.AdmitPhoneOtpAsync(phoneNumberHash, userId, origin.IpAddress, clientApplicationId, _options.PhoneOtp, now, cancellationToken);

    public Task<DeliveryAdmission> AdmitPasswordResetEmailAsync(
        string normalizedEmail,
        string? userId,
        AdmissionOrigin origin,
        string? clientKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _deliveries.AdmitPasswordResetAsync(normalizedEmail, userId, origin.IpAddress, clientKey, _options.PasswordReset, now, cancellationToken);

    public Task WithdrawAsync(DeliveryAdmission admission, DateTimeOffset now, CancellationToken cancellationToken)
        => _deliveries.WithdrawAsync(admission, now, cancellationToken);

    public Task<string> AdmitMfaAttemptAsync(
        SqlOSTemporaryToken challenge,
        AdmissionOrigin origin,
        string? authorizationRequestId,
        CancellationToken cancellationToken)
        => _mfaAttempts.ReserveAsync(challenge, origin, authorizationRequestId, cancellationToken);

    public Task RecordMfaAttemptFailedAsync(string reservationId, CancellationToken cancellationToken)
        => _mfaAttempts.RecordFailureAsync(reservationId, cancellationToken);

    public Task RecordMfaAttemptSucceededAsync(string reservationId, CancellationToken cancellationToken)
        => _mfaAttempts.RecordSuccessAsync(reservationId, cancellationToken);

    public Task<bool> IsMfaBudgetExhaustedAsync(string userId, CancellationToken cancellationToken)
        => _mfaAttempts.IsUserCapacityExhaustedAsync(userId, cancellationToken);

    public SqlOSPasswordLoginAttempt BeginPasswordAttempt(
        string normalizedEmail,
        AdmissionOrigin origin,
        string? clientKey = null,
        string? authorizationRequestId = null,
        string? surface = null,
        string? userId = null)
        => _passwordAttempts.CreateAttempt(normalizedEmail, origin, clientKey, authorizationRequestId, surface, userId);

    public Task AdmitPasswordAttemptAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken)
        => _passwordAttempts.ReserveAsync(attempt, cancellationToken);

    public Task RecordPasswordAttemptFailedAsync(SqlOSPasswordLoginAttempt attempt, string failureReason, CancellationToken cancellationToken)
        => _passwordAttempts.RecordFailureAsync(attempt, failureReason, cancellationToken);

    public Task RecordPasswordAttemptSucceededAsync(SqlOSPasswordLoginAttempt attempt, CancellationToken cancellationToken)
        => _passwordAttempts.RecordSuccessAsync(attempt, cancellationToken);
}
