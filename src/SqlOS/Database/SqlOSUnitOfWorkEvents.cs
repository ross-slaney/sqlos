using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;

namespace SqlOS.Database;

/// <summary>
/// Events a unit of work records without an aggregate to carry them, such as failure records. The
/// <c>DbContext</c> is the unit of work, so its next save drains them together with the events of
/// its tracked aggregates, in raise order.
/// </summary>
/// <remarks>
/// A buffer belongs to one lease of its context (<see cref="DbContext.ContextId"/>): a pooled
/// context handed to the next scope starts with an empty buffer, so records a scope never saved
/// cannot reach another scope's audit log.
/// </remarks>
internal static class SqlOSUnitOfWorkEvents
{
    private static readonly ConditionalWeakTable<DbContext, LeasedBuffer> Buffers = new();

    /// <summary>The buffer of <paramref name="context"/>'s current lease, created on first use.</summary>
    public static DomainEventBuffer Of(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryGet(context, out var buffer))
        {
            return buffer;
        }

        var leased = new LeasedBuffer(context.ContextId, new DomainEventBuffer());
        Buffers.AddOrUpdate(context, leased);
        return leased.Buffer;
    }

    /// <summary>The buffer of <paramref name="context"/>'s current lease, if it has one.</summary>
    public static bool TryGet(DbContext context, [NotNullWhen(true)] out DomainEventBuffer? buffer)
    {
        buffer = Buffers.TryGetValue(context, out var leased) && leased.Lease == context.ContextId
            ? leased.Buffer
            : null;
        return buffer is not null;
    }

    private sealed record LeasedBuffer(DbContextId Lease, DomainEventBuffer Buffer);
}
