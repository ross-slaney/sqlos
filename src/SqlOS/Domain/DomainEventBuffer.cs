namespace SqlOS.Domain;

/// <summary>
/// The events an aggregate has raised and no save has committed yet.
/// </summary>
/// <remarks>
/// Every raised event gets a process-wide sequence number, so the save that drains several
/// aggregates (and the failure records of its unit of work) writes their audit rows in the order
/// the events happened, whichever entity raised them. A buffer is not thread-safe; like the
/// <c>DbContext</c> that tracks its aggregate, it belongs to one unit of work at a time.
/// </remarks>
internal sealed class DomainEventBuffer
{
    private static long s_lastSequence;
    private readonly List<RaisedDomainEvent> _pending = [];

    /// <summary>
    /// The sequence number of the last event raised in this process, by any buffer. An event raised
    /// after it was read has a greater sequence.
    /// </summary>
    public static long LastRaisedSequence => Interlocked.Read(ref s_lastSequence);

    /// <summary>The pending events, oldest first. Reading them does not drain them.</summary>
    public IReadOnlyList<ISqlOSDomainEvent> Pending => _pending.ConvertAll(static raised => raised.Event);

    public void Raise(ISqlOSDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _pending.Add(new RaisedDomainEvent(Interlocked.Increment(ref s_lastSequence), domainEvent));
    }

    /// <summary>Removes and returns the pending events, oldest first.</summary>
    public IReadOnlyList<RaisedDomainEvent> Drain()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    /// <summary>
    /// Removes the pending events raised after <paramref name="sequence"/> (see
    /// <see cref="LastRaisedSequence"/>). A unit of work that discards the changes it staged since
    /// then discards the events that described them, so no later save audits a change that never
    /// happened.
    /// </summary>
    public void DiscardRaisedAfter(long sequence) => _pending.RemoveAll(raised => raised.Sequence > sequence);

    /// <summary>
    /// Puts drained events back, in sequence order, ahead of anything raised since. A save that
    /// fails calls this so the unit of work is exactly as it was before the save.
    /// </summary>
    public void Requeue(IEnumerable<RaisedDomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _pending.InsertRange(0, events);
        _pending.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
    }
}

/// <summary>A domain event with the order in which it was raised.</summary>
internal readonly record struct RaisedDomainEvent(long Sequence, ISqlOSDomainEvent Event);
