namespace SqlOS.Domain;

/// <summary>
/// Something that happened to an aggregate, named in the past tense (<c>EmailClaimed</c>,
/// <c>SessionRevoked</c>). Concrete events are records in <c>SqlOS.Domain.Events</c>.
/// </summary>
/// <remarks>
/// An aggregate raises events into its <see cref="DomainEventBuffer"/> while it changes. The save
/// that commits the change projects them into audit rows in the same transaction, and once that
/// transaction commits it hands them to the internal post-commit handlers. Failure records that
/// change no state reach the same pipeline through <c>IAuditRecorder</c>.
/// </remarks>
internal interface ISqlOSDomainEvent
{
}
