using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Records the change-tracker state before an operation so everything staged after it can be
/// discarded later without touching changes the caller had already staged before it: the rows
/// it added, the values it changed, and the domain events its aggregates raised.
/// </summary>
/// <remarks>
/// Discarding the events matters as much as discarding the values: a staged claim that is reverted
/// must not leave its <c>UserEmailClaimed</c> event behind for a later save of the same unit of work
/// to turn into an audit row. Failure records (<c>IAuditRecorder</c>) describe attempts that did
/// happen and are kept.
/// </remarks>
internal sealed class SqlOSTrackedChangeSnapshot
{
    private readonly HashSet<object> _tracked;
    private readonly HashSet<object> _pending;
    private readonly long _lastEventSequence;

    private SqlOSTrackedChangeSnapshot(HashSet<object> tracked, HashSet<object> pending, long lastEventSequence)
    {
        _tracked = tracked;
        _pending = pending;
        _lastEventSequence = lastEventSequence;
    }

    public static SqlOSTrackedChangeSnapshot? Capture(ISqlOSAuthServerDbContext context)
    {
        if (context is not DbContext dbContext)
        {
            return null;
        }

        var tracked = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            tracked.Add(entry.Entity);
            if (entry.State != EntityState.Unchanged)
            {
                pending.Add(entry.Entity);
            }
        }

        return new SqlOSTrackedChangeSnapshot(tracked, pending, DomainEventBuffer.LastRaisedSequence);
    }

    public void Revert(ISqlOSAuthServerDbContext context)
    {
        if (context is not DbContext dbContext)
        {
            return;
        }

        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is ISqlOSAggregate aggregate)
            {
                aggregate.Events.DiscardRaisedAfter(_lastEventSequence);
            }

            if (_pending.Contains(entry.Entity))
            {
                continue;
            }

            if (entry.State == EntityState.Added && !_tracked.Contains(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
        }
    }
}
