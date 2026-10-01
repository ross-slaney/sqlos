namespace SqlOS.Domain;

/// <summary>
/// Reacts to a domain event after the transaction that recorded it has committed, for work that
/// must never run for a change that rolled back (sending a message, warming a cache).
/// </summary>
/// <remarks>
/// Internal handlers are registered in dependency injection. Each save's handlers run in a scope
/// of their own, with the saving scope's <see cref="SqlOSRequestContext"/>, in the order the events
/// were raised; a handler that needs the database uses that scope's context, never the one that
/// committed. The data is already committed when a handler runs, so a handler that throws surfaces
/// to the caller of the save (or of the transaction's commit), exactly as a failure after
/// <c>SaveChangesAsync</c> did in 7.x. Public handlers come with #286.
/// </remarks>
internal interface ISqlOSPostCommitHandler<in TEvent>
    where TEvent : ISqlOSDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
