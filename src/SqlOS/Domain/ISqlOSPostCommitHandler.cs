namespace SqlOS.Domain;

/// <summary>
/// Reacts to a domain event after the transaction that recorded it has committed, for work that
/// must never run for a change that rolled back (sending a message, warming a cache).
/// </summary>
/// <remarks>
/// Internal handlers are registered in dependency injection. They run once the transaction has
/// committed, in the order the events were raised, each event's handlers in a scope of their own
/// that carries the saving scope's <see cref="SqlOSRequestContext"/>; a handler that needs the
/// database uses that scope's context, never the one that committed. The change is committed when
/// a handler runs, so a handler that throws is logged and the other handlers still run: work that
/// must fail the request belongs in the process, before its save. Public handlers come with #286.
/// </remarks>
internal interface ISqlOSPostCommitHandler<in TEvent>
    where TEvent : ISqlOSDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
