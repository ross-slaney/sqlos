using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Query;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// An email-code challenge: a numeric code sent to one mailbox, answered within a lifetime and a
/// budget of attempts, for a sign-in or a sign-up in one sign-in context.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the shared parts: <see cref="Domain.Expiry"/>, <see cref="Domain.Consumption"/>,
/// <see cref="AttemptBudget"/> (<see cref="AttemptCount"/> of <see cref="MaxAttempts"/>) and
/// <see cref="HashedSecret"/> (only hashes of the challenge token and of the code are stored), plus
/// the stored recipient and invalidation.
/// </para>
/// <para>
/// The code goes only to the stored recipient (#422): <see cref="Email"/> is the address stored on
/// the account the typed address belongs to, never the spelling typed into the form, and a
/// challenge bound to an account answers only while that address is still the account's.
/// </para>
/// <para>
/// Attempts cannot be lost under concurrency (#424): an attempt is spent in the database, in one
/// conditional update, before its code is compared (<see cref="AtomicAttempts"/>), so concurrent
/// verifications can never spend more than <see cref="MaxAttempts"/> between them, and only a
/// reserved attempt (<see cref="EmailOtpAttemptReservation"/>) can register a code. A wrong code
/// that spent the last attempt invalidates the challenge the same way, once.
/// </para>
/// </remarks>
public sealed class SqlOSEmailOtpChallenge : ISqlOSAggregate
{
    internal const string SupersededReason = "superseded";
    internal const string DeliveryFailedReason = "delivery_failed";
    internal const string MaxAttemptsReason = "max_attempts";
    internal const string WrongCodeReason = "wrong_code";

    private readonly DomainEventBuffer _events = new();

    private SqlOSEmailOtpChallenge()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string ChallengeTokenHash { get; private set; } = string.Empty;
    public string CodeHash { get; private set; } = string.Empty;

    /// <summary>The stored recipient: the only address the code is sent to.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>The canonical key of the address the challenge was requested for.</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    public string? UserId { get; private set; }
    public string? UserEmailId { get; private set; }
    public string? AuthorizationRequestId { get; private set; }
    public string? ClientApplicationId { get; private set; }
    public string? RequestedOrganizationId { get; private set; }
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime LastSentAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public DateTime? InvalidatedAt { get; private set; }
    public string? InvalidatedReason { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public SqlOSUser? User { get; private set; }
    public SqlOSUserEmail? UserEmail { get; private set; }
    public SqlOSAuthorizationRequest? AuthorizationRequest { get; private set; }
    public SqlOSClientApplication? ClientApplication { get; private set; }

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    internal Expiry Expiry => new(ExpiresAt);

    internal Consumption Consumption => new(ConsumedAt);

    internal HashedSecret Code => HashedSecret.FromStored(HashedSecretScheme.Sha256, CodeHash);

    internal bool IsInvalidated => InvalidatedAt is not null;

    /// <summary>
    /// True while an attempt is left in the budget. A stored limit of zero (a host that configured
    /// none) leaves no attempt, as in 7.x.
    /// </summary>
    internal bool HasAttemptLeft => MaxAttempts > 0 && !new AttemptBudget(AttemptCount, MaxAttempts).IsExhausted;

    /// <summary>
    /// The purpose verification audits record: a challenge bound to an account signs in, one
    /// without an account signs up.
    /// </summary>
    internal string VerificationPurpose => UserId is null ? EmailOtpPurposes.Signup : EmailOtpPurposes.Login;

    /// <summary>
    /// Issues a challenge for <paramref name="request"/>. The recipient is
    /// <paramref name="accountEmail"/>'s stored address when the requested address belongs to an
    /// account, and the requested address otherwise (a sign-up or an unknown address).
    /// </summary>
    internal static IssuedEmailOtpChallenge Issue(
        EmailOtpChallengeRequest request,
        SqlOSUserEmail? accountEmail,
        EmailOtpChallengeSettings settings,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var challengeToken = OpaqueSecret.Issue();
        var code = NewCode(settings.CodeLength);
        var challenge = new SqlOSEmailOtpChallenge
        {
            Id = SqlOSIds.New("otp"),
            ChallengeTokenHash = challengeToken.Hash.Hash,
            CodeHash = HashedSecret.Sha256(CodeSecret(challengeToken.Value, code)).Hash,
            Email = accountEmail?.Email.Trim() ?? request.RequestedAddress.Address,
            NormalizedEmail = request.RequestedAddress.Canonical,
            UserId = accountEmail?.UserId,
            UserEmailId = accountEmail?.Id,
            AuthorizationRequestId = request.Context.AuthorizationRequestId,
            ClientApplicationId = request.Context.ClientApplicationId,
            RequestedOrganizationId = request.Context.RequestedOrganizationId,
            AttemptCount = 0,
            MaxAttempts = settings.MaxAttempts,
            CreatedAt = now,
            ExpiresAt = now.Add(settings.Lifetime),
            LastSentAt = now,
            IpAddress = request.IpAddress,
            UserAgent = request.UserAgent
        };
        challenge._events.Raise(new EmailOtpChallengeIssued(challenge.Id));
        return new IssuedEmailOtpChallenge(challenge, challengeToken.Value, code);
    }

    /// <summary>
    /// True while the challenge can be answered at <paramref name="now"/>: unspent, not
    /// invalidated, unexpired and with an attempt left.
    /// </summary>
    internal bool IsOpen(DateTime now)
        => !Consumption.IsConsumed && !IsInvalidated && !Expiry.IsExpired(now) && HasAttemptLeft;

    /// <summary>
    /// True when the code still reaches the account it signs in to: a challenge bound to an account
    /// answers only while the address it was sent to is still that account's (#422). Needs
    /// <see cref="UserEmail"/> loaded.
    /// </summary>
    internal bool IsStillAddressedToItsAccount()
        => UserId is null
            || (UserEmail is not null
                && string.Equals(UserEmail.UserId, UserId, StringComparison.Ordinal)
                && EmailAddress.TryParse(UserEmail.Email, out var stored)
                && string.Equals(stored.Canonical, NormalizedEmail, StringComparison.Ordinal));

    /// <summary>
    /// True when the challenge answers the authorization request a surface expects: the same
    /// request, or no request when the surface has none.
    /// </summary>
    internal bool AnswersAuthorizationRequest(string? expectedAuthorizationRequestId)
        => string.IsNullOrWhiteSpace(expectedAuthorizationRequestId)
            ? string.IsNullOrWhiteSpace(AuthorizationRequestId)
            : string.Equals(AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal);

    /// <summary>True when the challenge was started in <paramref name="context"/> (compared ordinally).</summary>
    internal bool WasStartedIn(EmailOtpChallengeContext context)
        => string.Equals(AuthorizationRequestId, context.AuthorizationRequestId, StringComparison.Ordinal)
            && string.Equals(ClientApplicationId, context.ClientApplicationId, StringComparison.Ordinal)
            && string.Equals(RequestedOrganizationId, context.RequestedOrganizationId, StringComparison.Ordinal);

    /// <summary>True when the code went out less than <paramref name="cooldown"/> before <paramref name="now"/>.</summary>
    internal bool WasSentWithin(TimeSpan cooldown, DateTime now) => LastSentAt > now.Subtract(cooldown);

    /// <summary>A newer challenge for the same address and context replaces this open one.</summary>
    internal void Supersede(DateTime now)
    {
        if (Close(SupersededReason, now))
        {
            _events.Raise(new EmailOtpChallengeSuperseded(Id));
        }
    }

    /// <summary>Withdraws the open challenge because its account's sessions were revoked for <paramref name="reason"/>.</summary>
    internal void Withdraw(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Close(reason, now))
        {
            _events.Raise(new EmailOtpChallengeWithdrawn(Id, reason));
        }
    }

    /// <summary>Records that the code went out to the stored recipient, or was withheld.</summary>
    internal void RecordStart(EmailOtpChallengeRequest request, bool codeSent)
    {
        ArgumentNullException.ThrowIfNull(request);
        _events.Raise(new EmailOtpChallengeStarted(
            Id,
            request.Purpose,
            Masked.Email(request.RequestedAddress.Address),
            IpAddress,
            ClientApplicationId,
            AuthorizationRequestId,
            RequestedOrganizationId,
            codeSent));
    }

    /// <summary>The code could not be sent: the challenge can never be answered.</summary>
    internal void FailDelivery(EmailOtpChallengeRequest request, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        Close(DeliveryFailedReason, now);
        _events.Raise(new EmailOtpDeliveryFailed(
            Id,
            request.Purpose,
            Masked.Email(request.RequestedAddress.Address),
            IpAddress,
            ClientApplicationId,
            RequestedOrganizationId));
    }

    /// <summary>
    /// Registers a code presented against an attempt the database has already reserved for this
    /// challenge. A right code is accepted and audited; the caller then completes the challenge
    /// (or, for a sign-up, completes it with the account it creates). A wrong code changes nothing
    /// yet: the attempt is already spent, and the caller records the rejection once the database
    /// has decided whether it exhausted the budget.
    /// </summary>
    internal EmailOtpAttemptOutcome RegisterAttempt(EmailOtpAttemptReservation reservation, string challengeToken, string code)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (!string.Equals(reservation.ChallengeId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The attempt was reserved for challenge '{reservation.ChallengeId}', not '{Id}'.", nameof(reservation));
        }

        if (!Code.Matches(CodeSecret(challengeToken, code)))
        {
            return EmailOtpAttemptOutcome.Rejected;
        }

        _events.Raise(new EmailOtpCodeAccepted(
            Id,
            VerificationPurpose,
            Masked.Email(Email),
            IpAddress,
            UserId,
            ClientApplicationId,
            AuthorizationRequestId));
        return EmailOtpAttemptOutcome.Accepted;
    }

    /// <summary>Records a wrong code; <paramref name="attemptsExhausted"/> when it spent the last attempt and invalidated the challenge.</summary>
    internal void RejectCode(bool attemptsExhausted)
        => _events.Raise(new EmailOtpCodeRejected(
            Id,
            VerificationPurpose,
            Masked.Email(Email),
            IpAddress,
            ClientApplicationId,
            AuthorizationRequestId,
            attemptsExhausted ? MaxAttemptsReason : WrongCodeReason));

    /// <summary>
    /// Spends the challenge after its code was accepted. The completed challenge proves its
    /// recipient's mailbox.
    /// </summary>
    internal OwnershipProof Complete(DateTime now)
    {
        ConsumedAt = Consumption.Consume(now).ConsumedAt;
        _events.Raise(new EmailOtpChallengeConsumed(Id));
        return new OwnershipProof(EmailAddress.Parse(Email), OwnershipProofMethod.EmailOtp);
    }

    /// <summary>
    /// Spends one attempt on this tracked challenge, for a store without set-based updates (the
    /// EF Core in-memory provider). A relational store spends it with <see cref="AtomicAttempts"/>.
    /// </summary>
    internal void SpendAttemptInMemory() => AttemptCount = new AttemptBudget(AttemptCount, MaxAttempts).Spend().Spent;

    /// <summary>
    /// Invalidates this tracked challenge once its attempts are all spent, for a store without
    /// set-based updates. Returns true when this call invalidated it.
    /// </summary>
    internal bool ExhaustInMemory(DateTime now) => !HasAttemptLeft && Close(MaxAttemptsReason, now);

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

    // The 7.x code hash: the SHA-256 of the raw challenge token and the code, so a code is only
    // ever valid together with the challenge token it was issued with.
    private static string CodeSecret(string challengeToken, string code) => $"{challengeToken}:{code}";

    private static string NewCode(int length)
    {
        var maxValue = (int)Math.Pow(10, Math.Max(1, length));
        return RandomNumberGenerator.GetInt32(0, maxValue)
            .ToString($"D{length}", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The attempt budget as the database applies it (#424): one conditional update spends an
    /// attempt of a challenge that is open (the same rule as <see cref="IsOpen"/>), and one
    /// invalidates a challenge whose attempts are all spent, once. Neither goes through the change
    /// tracker, so no later save of a tracked challenge writes a stale count over them.
    /// </summary>
    internal static class AtomicAttempts
    {
        public static Expression<Func<SetPropertyCalls<SqlOSEmailOtpChallenge>, SetPropertyCalls<SqlOSEmailOtpChallenge>>> Spend { get; }
            = setters => setters.SetProperty(challenge => challenge.AttemptCount, challenge => challenge.AttemptCount + 1);

        /// <summary>The challenge, while it is open at <paramref name="now"/> with an attempt left.</summary>
        public static Expression<Func<SqlOSEmailOtpChallenge, bool>> CanSpend(string challengeId, DateTime now)
            => challenge => challenge.Id == challengeId
                && challenge.ConsumedAt == null
                && challenge.InvalidatedAt == null
                && challenge.ExpiresAt > now
                && challenge.AttemptCount < challenge.MaxAttempts;

        /// <summary>The challenge, while it is unspent and not invalidated but has no attempt left.</summary>
        public static Expression<Func<SqlOSEmailOtpChallenge, bool>> IsSpentOut(string challengeId)
            => challenge => challenge.Id == challengeId
                && challenge.ConsumedAt == null
                && challenge.InvalidatedAt == null
                && challenge.AttemptCount >= challenge.MaxAttempts;

        public static Expression<Func<SetPropertyCalls<SqlOSEmailOtpChallenge>, SetPropertyCalls<SqlOSEmailOtpChallenge>>> Invalidate(DateTime now)
            => setters => setters
                .SetProperty(challenge => challenge.InvalidatedAt, now)
                .SetProperty(challenge => challenge.InvalidatedReason, MaxAttemptsReason);
    }
}

/// <summary>The purposes an email-code challenge is started for.</summary>
internal static class EmailOtpPurposes
{
    public const string Login = "login";
    public const string Signup = "signup";
}

/// <summary>The sign-in context a challenge belongs to: one code per address and context at a time.</summary>
internal sealed record EmailOtpChallengeContext(
    string? AuthorizationRequestId,
    string? ClientApplicationId,
    string? RequestedOrganizationId);

/// <summary>What a request asked for when it started an email-code challenge.</summary>
/// <param name="RequestedAddress">The address as typed (trimmed, in NFC).</param>
/// <param name="Purpose"><see cref="EmailOtpPurposes.Login"/> or <see cref="EmailOtpPurposes.Signup"/>.</param>
/// <param name="Context">The sign-in context the challenge belongs to.</param>
/// <param name="IpAddress">The address the request came from.</param>
/// <param name="UserAgent">The request's <c>User-Agent</c>, empty when it sent none.</param>
internal sealed record EmailOtpChallengeRequest(
    EmailAddress RequestedAddress,
    string Purpose,
    EmailOtpChallengeContext Context,
    string? IpAddress,
    string? UserAgent);

/// <summary>The configured shape of new challenges.</summary>
internal sealed record EmailOtpChallengeSettings(int CodeLength, int MaxAttempts, TimeSpan Lifetime);

/// <summary>
/// A challenge at the moment it is issued: the entity to store, the raw challenge token for the
/// browser, and the code for the recipient's mailbox. <see cref="ToString"/> shows neither secret.
/// </summary>
internal sealed record IssuedEmailOtpChallenge(SqlOSEmailOtpChallenge Challenge, string ChallengeToken, string Code)
{
    public override string ToString() => $"{nameof(IssuedEmailOtpChallenge)}({Challenge.Id})";
}

/// <summary>
/// An attempt of a challenge's budget that the database spent before the code is compared (#424).
/// Only the attempt ledger, after its conditional update spent one, creates one, so a code can
/// never be compared without a counted attempt.
/// </summary>
internal sealed class EmailOtpAttemptReservation : ISqlOSProof
{
    internal EmailOtpAttemptReservation(string challengeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ChallengeId = challengeId;
    }

    public string ChallengeId { get; }
}

/// <summary>How a registered attempt turned out.</summary>
internal enum EmailOtpAttemptOutcome
{
    /// <summary>The code was right.</summary>
    Accepted = 1,

    /// <summary>The code was wrong.</summary>
    Rejected = 2
}
