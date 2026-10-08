using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SqlOS.Fga.Interfaces;

/// <summary>
/// What SqlOS needs of the application's DbContext. Every member is already on <see cref="DbContext"/>, so a
/// context implements it by declaring it: <c>class AppDbContext : DbContext, ISqlOSFgaDbContext</c>.
/// </summary>
public interface ISqlOSFgaDbContext
{
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
