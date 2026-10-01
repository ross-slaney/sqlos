using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;
using SqlOS.Domain;

namespace SqlOS.AuditLogs;

/// <summary>
/// Records outcomes that change no state but are audited, such as a failed password or a rejected
/// code, inside the current unit of work.
/// </summary>
/// <remarks>
/// A failure record is a domain event without an aggregate. It goes through the same
/// <see cref="SqlOSAuditProjection"/> as every other event and is written by the next save of the
/// unit of work, in the order it was recorded relative to the events of that save, so a process
/// that must persist a failure records it and saves once. A save that fails, or is rolled back,
/// writes no row for it.
/// </remarks>
internal interface IAuditRecorder
{
    void Record(ISqlOSDomainEvent failure);
}

/// <summary>Stages failure records on the scope's SqlOS <c>DbContext</c>.</summary>
internal sealed class SqlOSAuditRecorder : IAuditRecorder
{
    private readonly DbContext _context;

    public SqlOSAuditRecorder(ISqlOSAuthServerDbContext context)
    {
        _context = context as DbContext
            ?? throw new ArgumentException("The SqlOS unit of work must be an EF Core DbContext.", nameof(context));
    }

    public void Record(ISqlOSDomainEvent failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        SqlOSUnitOfWorkEvents.Of(_context).Raise(failure);
    }
}
