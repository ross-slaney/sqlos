namespace SqlOS.Domain.Events;

/// <summary>A temporary token was issued.</summary>
internal sealed record TemporaryTokenIssued(string TokenId, string Purpose) : ISqlOSDomainEvent;

/// <summary>A single-use temporary token was spent on its purpose.</summary>
internal sealed record TemporaryTokenConsumed(string TokenId, string Purpose) : ISqlOSDomainEvent;

/// <summary>A temporary token was withdrawn without being used (replaced, revoked or undelivered).</summary>
internal sealed record TemporaryTokenRetired(string TokenId, string Purpose) : ISqlOSDomainEvent;

/// <summary>A temporary token's payload was rewritten (an MFA challenge counted a failure).</summary>
internal sealed record TemporaryTokenPayloadReplaced(string TokenId, string Purpose) : ISqlOSDomainEvent;

/// <summary>
/// What became of the flow a temporary token serves, recorded on the token
/// (<c>SqlOSTemporaryToken.Record</c>). An outcome belongs to one token kind, and a token accepts
/// only outcomes of its own kind.
/// </summary>
internal abstract record TemporaryTokenOutcome(string TokenId) : ISqlOSDomainEvent
{
    /// <summary>The kind of token this outcome is recorded on.</summary>
    public abstract TemporaryTokenKind Kind { get; }
}
