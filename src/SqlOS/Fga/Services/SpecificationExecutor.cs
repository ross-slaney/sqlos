using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Specifications;

namespace SqlOS.Fga.Services;

public class SpecificationExecutor : ISpecificationExecutor
{
    private readonly ISqlOSFgaAuthService _authorizationService;
    private readonly ILogger<SpecificationExecutor> _logger;

    public SpecificationExecutor(
        ISqlOSFgaAuthService authorizationService,
        ILogger<SpecificationExecutor> logger)
    {
        _authorizationService = authorizationService;
        _logger = logger;
    }

    public async Task<PaginatedResult<TDto>> ExecuteAsync<TEntity, TDto>(
        DbSet<TEntity> dbSet,
        PagedSpecification<TEntity> specification,
        string subjectId,
        Func<TEntity, TDto> selector,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasResourceId
    {
        if (string.IsNullOrEmpty(specification.RequiredPermission))
        {
            throw new InvalidOperationException(
                $"Specification {specification.GetType().Name} must define RequiredPermission to use this overload.");
        }

        var query = specification.ConfigureQuery(dbSet.AsQueryable());

        return await ExecuteAsync(
            query, specification, subjectId, specification.RequiredPermission,
            selector, cancellationToken);
    }

    public async Task<PaginatedResult<TDto>> ExecuteAsync<TEntity, TDto>(
        IQueryable<TEntity> query,
        PagedSpecification<TEntity> specification,
        string subjectId,
        string permissionKey,
        Func<TEntity, TDto> selector,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasResourceId
    {
        var authFilter = await _authorizationService.GetAuthorizationFilterAsync<TEntity>(
            subjectId, permissionKey);

        query = query.Where(authFilter);

        var userFilter = specification.ToExpression();
        query = query.Where(userFilter);

        if (!string.IsNullOrEmpty(specification.Cursor))
        {
            var cursorFilter = specification.GetCursorFilter(specification.Cursor);
            query = query.Where(cursorFilter);
        }

        query = specification.ApplySort(query);
        query = query.Take(specification.SafePageSize + 1);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            try
            {
                var sql = query.ToQueryString();
                _logger.LogDebug("Executing specification {SpecificationType} - Generated SQL:\n{Sql}",
                    specification.GetType().Name, sql);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not generate SQL string for logging");
            }
        }

        var entities = await query.ToListAsync(cancellationToken);

        var hasMore = entities.Count > specification.SafePageSize;
        if (hasMore)
        {
            entities = entities.Take(specification.SafePageSize).ToList();
        }

        var dtos = entities.Select(selector).ToList();

        string? nextCursor = null;
        if (hasMore && entities.Count > 0)
        {
            nextCursor = specification.BuildCursor(entities[^1]);
        }

        return PaginatedResult<TDto>.Create(dtos, specification.SafePageSize, nextCursor);
    }

    public async Task<long> CountAsync<TEntity>(
        DbSet<TEntity> dbSet,
        PagedSpecification<TEntity> specification,
        string subjectId,
        CancellationToken cancellationToken = default)
        where TEntity : class, IHasResourceId
    {
        if (string.IsNullOrEmpty(specification.RequiredPermission))
        {
            throw new InvalidOperationException(
                $"Specification {specification.GetType().Name} must define RequiredPermission to use this overload.");
        }

        var query = specification.ConfigureQuery(dbSet.AsQueryable());

        var authFilter = await _authorizationService.GetAuthorizationFilterAsync<TEntity>(
            subjectId, specification.RequiredPermission);

        query = query.Where(authFilter);

        var userFilter = specification.ToExpression();
        query = query.Where(userFilter);

        return await query.LongCountAsync(cancellationToken);
    }
}
