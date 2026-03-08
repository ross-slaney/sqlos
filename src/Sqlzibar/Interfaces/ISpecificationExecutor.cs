using Microsoft.EntityFrameworkCore;
using Sqlzibar.Specifications;

namespace Sqlzibar.Interfaces;

/// <summary>
/// Executes specifications against a queryable, combining user filters with authorization filters
/// and cursor-based pagination.
/// </summary>
public interface ISpecificationExecutor
{
    /// <summary>
    /// Executes a specification with authorization filtering, using the permission from the specification's RequiredPermission.
    /// </summary>
    Task<PaginatedResult<TDto>> ExecuteAsync<TEntity, TDto>(
        DbSet<TEntity> dbSet,
        PagedSpecification<TEntity> specification,
        string subjectId,
        Func<TEntity, TDto> selector,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasResourceId;

    /// <summary>
    /// Executes a specification with authorization filtering, using an explicit permission key.
    /// </summary>
    Task<PaginatedResult<TDto>> ExecuteAsync<TEntity, TDto>(
        IQueryable<TEntity> query,
        PagedSpecification<TEntity> specification,
        string subjectId,
        string permissionKey,
        Func<TEntity, TDto> selector,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasResourceId;

    /// <summary>
    /// Returns the total count of entities matching the specification's filter and auth scope.
    /// WARNING: This executes a COUNT(*) query which scans all matching rows.
    /// At large scale (millions of rows), this can be slow. Consider whether you truly
    /// need an exact count — cursor pagination works without it.
    /// </summary>
    Task<long> CountAsync<TEntity>(
        DbSet<TEntity> dbSet,
        PagedSpecification<TEntity> specification,
        string subjectId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasResourceId;
}
