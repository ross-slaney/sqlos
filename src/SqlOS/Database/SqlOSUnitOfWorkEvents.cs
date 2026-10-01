using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using SqlOS.Domain;

namespace SqlOS.Database;

/// <summary>
/// Events a unit of work records without an aggregate to carry them, such as failure records. The
/// <c>DbContext</c> is the unit of work, so its next save drains them together with the events of
/// its tracked aggregates, in raise order.
/// </summary>
internal static class SqlOSUnitOfWorkEvents
{
    private static readonly ConditionalWeakTable<DbContext, DomainEventBuffer> Buffers = new();

    /// <summary>The buffer of <paramref name="context"/>, created on first use.</summary>
    public static DomainEventBuffer Of(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Buffers.GetValue(context, static _ => new DomainEventBuffer());
    }

    public static bool TryGet(DbContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DomainEventBuffer? buffer)
        => Buffers.TryGetValue(context, out buffer);
}
