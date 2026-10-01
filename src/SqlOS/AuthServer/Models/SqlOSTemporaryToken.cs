using System.Linq.Expressions;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuthServer.Models;

/// <summary>
/// A short-lived bearer handle SqlOS hands out once and stores only as a hash: a sign-in link, a
/// pending sign-in step, a signup or enrollment token, an issuer-session cookie.
/// </summary>
/// <remarks>
/// <para>
/// Every token belongs to one <see cref="TemporaryTokenKind"/> (<see cref="SqlOSTemporaryTokenKinds"/>),
/// and is issued, read and spent only through it: the kind fixes the purpose, the payload type,
/// the lifetime and the bindings, so a token minted for one purpose is never accepted for another.
/// The lifecycle rules are the parts SqlOS shares: <see cref="Domain.Expiry"/> (a token is expired
/// from its expiry instant on), <see cref="Domain.Consumption"/> (spent once) and
/// <see cref="HashedSecret"/> (only the SHA-256 hash of the raw token is stored).
/// </para>
/// <para>
/// <see cref="ConsumedAt"/> and <see cref="PayloadJson"/> are concurrency tokens, so two requests
/// that both see a token unspent cannot both spend it, and a payload rewrite cannot overwrite a
/// concurrent one.
/// </para>
/// </remarks>
public sealed class SqlOSTemporaryToken : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();

    private SqlOSTemporaryToken()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string Purpose { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public string? UserId { get; private set; }
    public string? ClientApplicationId { get; private set; }
    public string? OrganizationId { get; private set; }
    public string? IssuerSessionFamilyId { get; private set; }
    public string? PayloadJson { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }

    public SqlOSIssuerSessionFamily? IssuerSessionFamily { get; private set; }

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    internal Expiry Expiry => new(ExpiresAt);

    internal Consumption Consumption => new(ConsumedAt);

    internal HashedSecret Secret => HashedSecret.FromStored(HashedSecretScheme.Sha256, TokenHash);

    /// <summary>
    /// Issues a token of <paramref name="kind"/>: a new random raw token, of which only the hash is
    /// kept, bound to <paramref name="binding"/> and carrying <paramref name="payload"/>. The
    /// <paramref name="lifetime"/> is the kind's, resolved by <see cref="TemporaryTokenLifetime.Resolve"/>,
    /// and the token lives from <paramref name="now"/>.
    /// </summary>
    internal static IssuedTemporaryToken Issue<TPayload>(
        TemporaryTokenKind<TPayload> kind,
        TPayload? payload,
        TemporaryTokenBinding binding,
        TimeSpan lifetime,
        DateTime now)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(kind);
        kind.EnsureAllows(binding);
        var secret = OpaqueSecret.Issue();
        var token = new SqlOSTemporaryToken
        {
            Id = SqlOSIds.New("tmp"),
            Purpose = kind.Purpose,
            TokenHash = secret.Hash.Hash,
            UserId = binding.UserId,
            ClientApplicationId = binding.ClientApplicationId,
            OrganizationId = binding.OrganizationId,
            IssuerSessionFamilyId = binding.IssuerSessionFamilyId,
            PayloadJson = kind.Serialize(payload),
            CreatedAt = now,
            // 7.x adds the lifetime as configured; a non-positive one issues a token that is
            // already expired, which is what a host that configured it gets.
            ExpiresAt = now.Add(lifetime)
        };
        token._events.Raise(new TemporaryTokenIssued(token.Id, token.Purpose));
        if (kind.IssuedEvent(token.Id, binding) is { } issuedFact)
        {
            token._events.Raise(issuedFact);
        }

        return new IssuedTemporaryToken(token, secret.Value);
    }

    /// <summary>Tokens of <paramref name="kind"/>.</summary>
    internal static Expression<Func<SqlOSTemporaryToken, bool>> OfKind(TemporaryTokenKind kind)
    {
        var purpose = kind.Purpose;
        return token => token.Purpose == purpose;
    }

    /// <summary>The token whose raw value is <paramref name="rawToken"/> (matched by its stored hash).</summary>
    internal static Expression<Func<SqlOSTemporaryToken, bool>> Presented(string rawToken)
    {
        var hash = HashedSecret.Sha256(rawToken).Hash;
        return token => token.TokenHash == hash;
    }

    /// <summary>
    /// Tokens that can still be used at <paramref name="now"/>: unspent and unexpired. It is the
    /// query form of <see cref="IsUsable"/>.
    /// </summary>
    internal static Expression<Func<SqlOSTemporaryToken, bool>> UsableAt(DateTime now)
        => token => token.ConsumedAt == null && token.ExpiresAt > now;

    internal bool Is(TemporaryTokenKind kind) => string.Equals(Purpose, kind.Purpose, StringComparison.Ordinal);

    internal bool IsUsable(DateTime now) => !Consumption.IsConsumed && !Expiry.IsExpired(now);

    /// <summary>The payload this token carries, read as <paramref name="kind"/>'s payload type.</summary>
    internal TPayload? ReadPayload<TPayload>(TemporaryTokenKind<TPayload> kind)
        where TPayload : class
    {
        EnsureKind(kind);
        return kind.Deserialize(PayloadJson);
    }

    /// <summary>
    /// Spends the token on its purpose. Only single-use kinds are spent, once, and never after
    /// they expire.
    /// </summary>
    internal void Consume(TemporaryTokenKind kind, DateTime now)
    {
        EnsureKind(kind);
        if (!kind.IsSingleUse)
        {
            throw new InvalidOperationException($"Temporary tokens of purpose '{Purpose}' are not spent by use; retire them instead.");
        }

        Expiry.EnsureActive(now);
        ConsumedAt = Consumption.Consume(now).ConsumedAt;
        _events.Raise(new TemporaryTokenConsumed(Id, Purpose));
    }

    /// <summary>
    /// Withdraws the token without using it: a newer one replaced it, its session or account was
    /// revoked, or it never reached its recipient. A token that is already spent or retired is left
    /// as it is.
    /// </summary>
    /// <remarks>7.x marks a withdrawn token the way it marks a spent one, with <see cref="ConsumedAt"/>.</remarks>
    internal void Retire(DateTime now)
    {
        if (Consumption.IsConsumed)
        {
            return;
        }

        ConsumedAt = Consumption.Consume(now).ConsumedAt;
        _events.Raise(new TemporaryTokenRetired(Id, Purpose));
    }

    /// <summary>Replaces the payload, for a kind whose payload records progress (an MFA challenge's failures).</summary>
    internal void ReplacePayload<TPayload>(TemporaryTokenKind<TPayload> kind, TPayload payload)
        where TPayload : class
    {
        EnsureKind(kind);
        ArgumentNullException.ThrowIfNull(payload);
        PayloadJson = kind.Serialize(payload);
        _events.Raise(new TemporaryTokenPayloadReplaced(Id, Purpose));
    }

    /// <summary>
    /// Records what became of the flow this token serves, for example that a sign-in link was
    /// sent. The outcome commits, and is audited, with the token's next save; it must belong to
    /// this token's kind.
    /// </summary>
    internal void Record(TemporaryTokenOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        EnsureKind(outcome.Kind);
        if (!string.Equals(outcome.TokenId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The outcome is for token '{outcome.TokenId}', not '{Id}'.", nameof(outcome));
        }

        _events.Raise(outcome);
    }

    private void EnsureKind(TemporaryTokenKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!Is(kind))
        {
            throw new InvalidOperationException($"The temporary token is of purpose '{Purpose}', not '{kind.Purpose}'.");
        }
    }
}

/// <summary>A token at the moment it is issued: the entity to store, and the raw token to hand out once.</summary>
internal sealed record IssuedTemporaryToken(SqlOSTemporaryToken Token, string RawToken)
{
    public override string ToString() => $"{nameof(IssuedTemporaryToken)}({Token.Purpose}, {Token.Id})";
}
