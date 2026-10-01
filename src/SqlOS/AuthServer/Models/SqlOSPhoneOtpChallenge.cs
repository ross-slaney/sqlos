using SqlOS.AuthServer.Interfaces;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// A phone-code challenge: a code the SMS provider sends to one number and checks, for a sign-in,
/// a sign-up or an enrollment in one sign-in context.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the shared parts: <see cref="Domain.Expiry"/>, <see cref="Domain.Consumption"/>,
/// <see cref="AttemptBudget"/> (one provider check per challenge) and <see cref="HashedSecret"/>
/// (only hashes of the challenge token and of the number are stored; the provider keeps the
/// code), plus the stored recipient and invalidation.
/// </para>
/// <para>
/// The code goes only to the stored recipient: <see cref="PhoneNumberEncrypted"/> protects the
/// number the challenge was issued for, and the provider sends to and checks against exactly that
/// number. A rejected check invalidates the challenge and an approved one spends it, so one
/// provider check is all a challenge ever needs.
/// </para>
/// </remarks>
public sealed class SqlOSPhoneOtpChallenge : ISqlOSAggregate
{
    /// <summary>The provider checks a challenge's code at most once.</summary>
    internal const int MaxProviderChecks = 1;

    internal const string SupersededReason = "superseded";
    internal const string DeliveryFailedReason = "delivery_failed";
    internal const string NotStartedReason = "not_started";
    internal const string ProviderRejectedReason = "provider_rejected";
    internal const int MaxReasonLength = 120;

    private readonly DomainEventBuffer _events = new();

    private SqlOSPhoneOtpChallenge()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string ChallengeTokenHash { get; private set; } = string.Empty;
    public string PhoneNumberHash { get; private set; } = string.Empty;

    /// <summary>The stored recipient, protected: the only number the code is sent to and checked against.</summary>
    public string PhoneNumberEncrypted { get; private set; } = string.Empty;

    public string MaskedPhoneNumber { get; private set; } = string.Empty;
    public string Purpose { get; private set; } = PhoneOtpPurposes.Login;
    public string? UserId { get; private set; }
    public string? UserPhoneNumberId { get; private set; }
    public string? AuthorizationRequestId { get; private set; }
    public string? ClientApplicationId { get; private set; }
    public string? RequestedOrganizationId { get; private set; }
    public bool ProviderStarted { get; private set; }
    public string Provider { get; private set; } = DefaultProvider;
    public string? ProviderChallengeId { get; private set; }
    public string? ProviderStatus { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime LastSentAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public DateTime? InvalidatedAt { get; private set; }
    public string? InvalidatedReason { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public SqlOSUser? User { get; private set; }
    public SqlOSUserPhoneNumber? UserPhoneNumber { get; private set; }
    public SqlOSAuthorizationRequest? AuthorizationRequest { get; private set; }
    public SqlOSClientApplication? ClientApplication { get; private set; }

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    internal const string DefaultProvider = "twilio_verify";

    internal Expiry Expiry => new(ExpiresAt);

    internal Consumption Consumption => new(ConsumedAt);

    internal AttemptBudget ProviderChecks => new(AttemptCount, MaxProviderChecks);

    internal bool IsInvalidated => InvalidatedAt is not null;

    /// <summary>
    /// Issues a challenge for <paramref name="request"/>. The service protects the recipient
    /// (<paramref name="protectedRecipient"/>) with the host's data protection; the challenge keeps
    /// its hash, its masked form and the protected value.
    /// </summary>
    internal static IssuedPhoneOtpChallenge Issue(
        PhoneOtpChallengeRequest request,
        string protectedRecipient,
        TimeSpan lifetime,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedRecipient);
        var challengeToken = OpaqueSecret.Issue();
        var challenge = new SqlOSPhoneOtpChallenge
        {
            Id = SqlOSIds.New("potp"),
            ChallengeTokenHash = challengeToken.Hash.Hash,
            PhoneNumberHash = HashedSecret.Sha256(request.Recipient.E164).Hash,
            PhoneNumberEncrypted = protectedRecipient,
            MaskedPhoneNumber = Masked.Phone(request.Recipient.E164),
            Purpose = request.Purpose,
            UserId = request.UserId,
            UserPhoneNumberId = request.UserPhoneNumberId,
            AuthorizationRequestId = request.Context.AuthorizationRequestId,
            ClientApplicationId = request.Context.ClientApplicationId,
            RequestedOrganizationId = request.Context.RequestedOrganizationId,
            ProviderStarted = false,
            Provider = DefaultProvider,
            AttemptCount = 0,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
            LastSentAt = now,
            IpAddress = request.IpAddress,
            UserAgent = request.UserAgent
        };
        challenge._events.Raise(new PhoneOtpChallengeIssued(challenge.Id));
        return new IssuedPhoneOtpChallenge(challenge, challengeToken.Value, request.Recipient);
    }

    /// <summary>True while the challenge can be answered at <paramref name="now"/>: unspent, not invalidated and unexpired.</summary>
    internal bool IsOpen(DateTime now) => !Consumption.IsConsumed && !IsInvalidated && !Expiry.IsExpired(now);

    /// <summary>True when the challenge was issued for <paramref name="purpose"/>.</summary>
    internal bool IsFor(string purpose) => string.Equals(Purpose, purpose, StringComparison.Ordinal);

    /// <summary>
    /// True when the challenge answers the authorization request a surface expects: the same
    /// request, or no request when the surface has none.
    /// </summary>
    internal bool AnswersAuthorizationRequest(string? expectedAuthorizationRequestId)
        => string.IsNullOrWhiteSpace(expectedAuthorizationRequestId)
            ? string.IsNullOrWhiteSpace(AuthorizationRequestId)
            : string.Equals(AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal);

    /// <summary>True when the challenge was started for <paramref name="purpose"/> in <paramref name="context"/> (compared ordinally).</summary>
    internal bool WasStartedIn(PhoneOtpChallengeContext context, string purpose)
        => string.Equals(AuthorizationRequestId, context.AuthorizationRequestId, StringComparison.Ordinal)
            && string.Equals(ClientApplicationId, context.ClientApplicationId, StringComparison.Ordinal)
            && string.Equals(RequestedOrganizationId, context.RequestedOrganizationId, StringComparison.Ordinal)
            && IsFor(purpose);

    /// <summary>True when the code went out less than <paramref name="cooldown"/> before <paramref name="now"/>.</summary>
    internal bool WasSentWithin(TimeSpan cooldown, DateTime now) => LastSentAt > now.Subtract(cooldown);

    /// <summary>A newer challenge for the same number, purpose and context replaces this open one.</summary>
    internal void Supersede(DateTime now)
    {
        if (Close(SupersededReason, now))
        {
            _events.Raise(new PhoneOtpChallengeSuperseded(Id));
        }
    }

    /// <summary>Withdraws the open challenge because its account's sessions were revoked for <paramref name="reason"/>.</summary>
    internal void Withdraw(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Close(reason, now))
        {
            _events.Raise(new PhoneOtpChallengeWithdrawn(Id, reason));
        }
    }

    /// <summary>The provider accepted the send: the code is on its way to the stored recipient.</summary>
    internal void RecordProviderStart(SqlOSOtpDeliveryStartResult delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ProviderStarted = true;
        Provider = delivery.Provider;
        ProviderChallengeId = delivery.ProviderChallengeId;
        ProviderStatus = delivery.ProviderStatus;
        RaiseStarted(codeSent: true);
    }

    /// <summary>No code was sent, because no active account owns the number; the challenge answers nothing.</summary>
    internal void RecordStartWithoutSending() => RaiseStarted(codeSent: false);

    /// <summary>The provider refused to send the code: the challenge can never be answered.</summary>
    internal void FailDelivery(SqlOSOtpDeliveryStartResult delivery, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        Close(DeliveryFailedReason, now);
        ProviderStatus = delivery.ProviderStatus;
        _events.Raise(new PhoneOtpDeliveryFailed(
            Id,
            Purpose,
            MaskedPhoneNumber,
            IpAddress,
            ClientApplicationId,
            RequestedOrganizationId,
            delivery.ProviderStatus));
    }

    /// <summary>
    /// Registers the provider's check of a presented code: it spends the challenge's one check and
    /// records the provider's answer. The caller then completes the challenge or rejects the code.
    /// </summary>
    internal PhoneOtpCheckOutcome RegisterCheck(SqlOSOtpDeliveryCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);
        AttemptCount = ProviderChecks.Spend().Spent;
        ProviderStatus = check.ProviderStatus;
        if (!string.IsNullOrWhiteSpace(check.ProviderChallengeId))
        {
            ProviderChallengeId = check.ProviderChallengeId;
        }

        return check.Approved ? PhoneOtpCheckOutcome.Approved : PhoneOtpCheckOutcome.Rejected;
    }

    /// <summary>
    /// Refuses the challenge for <paramref name="reason"/> (a rejected code's provider status, or
    /// <see cref="NotStartedReason"/>): it is invalidated and can never be answered.
    /// </summary>
    internal void RejectCode(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var stored = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
        Close(stored, now);
        _events.Raise(new PhoneOtpCodeRejected(
            Id,
            Purpose,
            MaskedPhoneNumber,
            IpAddress,
            ClientApplicationId,
            AuthorizationRequestId,
            stored));
    }

    /// <summary>Spends the challenge after the provider approved its code.</summary>
    internal void Complete(DateTime now)
    {
        ConsumedAt = Consumption.Consume(now).ConsumedAt;
        _events.Raise(new PhoneOtpChallengeConsumed(Id));
        _events.Raise(new PhoneOtpCodeAccepted(
            Id,
            Purpose,
            MaskedPhoneNumber,
            IpAddress,
            UserId,
            ClientApplicationId,
            AuthorizationRequestId,
            ProviderStatus));
    }

    /// <summary>Records that this enrollment challenge added its verified number to <paramref name="userId"/>'s account.</summary>
    internal void RecordEnrollment(string userId, string phoneNumberId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumberId);
        _events.Raise(new PhoneOtpPhoneEnrolled(Id, MaskedPhoneNumber, IpAddress, userId, phoneNumberId));
    }

    private void RaiseStarted(bool codeSent)
        => _events.Raise(new PhoneOtpChallengeStarted(
            Id,
            Purpose,
            MaskedPhoneNumber,
            IpAddress,
            ClientApplicationId,
            AuthorizationRequestId,
            RequestedOrganizationId,
            codeSent));

    private bool Close(string reason, DateTime now)
    {
        if (Consumption.IsConsumed || IsInvalidated)
        {
            return false;
        }

        InvalidatedAt = now;
        InvalidatedReason = reason;
        return true;
    }
}

/// <summary>The purposes a phone-code challenge is started for.</summary>
internal static class PhoneOtpPurposes
{
    public const string Login = "login";
    public const string Signup = "signup";
    public const string Enrollment = "enrollment";
}

/// <summary>The sign-in context a challenge belongs to: one code per number, purpose and context at a time.</summary>
internal sealed record PhoneOtpChallengeContext(
    string? AuthorizationRequestId,
    string? ClientApplicationId,
    string? RequestedOrganizationId);

/// <summary>What a request asked for when it started a phone-code challenge.</summary>
/// <param name="Recipient">The number to send the code to.</param>
/// <param name="Purpose">One of <see cref="PhoneOtpPurposes"/>.</param>
/// <param name="Context">The sign-in context the challenge belongs to.</param>
/// <param name="UserId">The account the challenge is for: the enrolling account, or the account that owns the number.</param>
/// <param name="UserPhoneNumberId">The account's verified number record, when the number belongs to an account.</param>
/// <param name="IpAddress">The address the request came from.</param>
/// <param name="UserAgent">The request's <c>User-Agent</c>, empty when it sent none.</param>
internal sealed record PhoneOtpChallengeRequest(
    PhoneNumber Recipient,
    string Purpose,
    PhoneOtpChallengeContext Context,
    string? UserId,
    string? UserPhoneNumberId,
    string? IpAddress,
    string? UserAgent);

/// <summary>
/// A challenge at the moment it is issued: the entity to store, the raw challenge token for the
/// browser, and the recipient the provider sends the code to. <see cref="ToString"/> shows no secret.
/// </summary>
internal sealed record IssuedPhoneOtpChallenge(SqlOSPhoneOtpChallenge Challenge, string ChallengeToken, PhoneNumber Recipient)
{
    public override string ToString() => $"{nameof(IssuedPhoneOtpChallenge)}({Challenge.Id})";
}

/// <summary>The provider's answer to a checked code.</summary>
internal enum PhoneOtpCheckOutcome
{
    Approved = 1,
    Rejected = 2
}
