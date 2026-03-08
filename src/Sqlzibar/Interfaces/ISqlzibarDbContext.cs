using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Sqlzibar.Models;

namespace Sqlzibar.Interfaces;

/// <summary>
/// Minimal interface consumers implement on their DbContext.
/// NO DbSet properties required — Sqlzibar uses Set&lt;T&gt;() internally.
/// Consumer only implements the TVF method (3 lines of copy-paste).
/// </summary>
public interface ISqlzibarDbContext
{
    /// <summary>
    /// TVF for authorization query composition.
    /// Implementation: =&gt; FromExpression(() =&gt; IsResourceAccessible(resourceId, subjectIds, permissionId));
    /// </summary>
    IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId);

    /// <summary>
    /// Access to entity sets. Already on DbContext — auto-implemented.
    /// </summary>
    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    /// <summary>
    /// Access to database operations. Already on DbContext — auto-implemented.
    /// </summary>
    DatabaseFacade Database { get; }

    /// <summary>
    /// Save changes. Already on DbContext — auto-implemented.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
