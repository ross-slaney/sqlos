using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlOS.Fga.Indexing;

#pragma warning disable EF1001 // EF has no public pre-parameter-extraction rewrite hook.

/// <summary>
/// Rewrites supported authorization pages into a single relational query. EF retains parameter binding,
/// execution strategy, connection/transaction ownership, tracking, projection, and materialization.
/// There is no application-side page executor or authorization result cache.
/// </summary>
internal sealed class SqlOSFgaNativeQueryCompiler(
    IQueryContextFactory queryContextFactory,
    ICompiledQueryCache compiledQueryCache,
    ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
    IDatabase database,
    IDiagnosticsLogger<DbLoggerCategory.Query> logger,
    ICurrentDbContext currentContext,
    IEvaluatableExpressionFilter evaluatableExpressionFilter,
    IModel model)
    : QueryCompiler(queryContextFactory, compiledQueryCache, compiledQueryCacheKeyGenerator, database, logger, currentContext, evaluatableExpressionFilter, model)
{
    public override TResult Execute<TResult>(Expression query)
        => base.Execute<TResult>(Rewrite(query));

    public override TResult ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken = default)
        => base.ExecuteAsync<TResult>(Rewrite(query), cancellationToken);

    private Expression Rewrite(Expression query)
    {
        if (SqlOSFgaNativePageRewriter.Suppressed.Value || !currentContext.Context.Database.IsRelational()) return query;
        return SqlOSFgaNativePageRewriter.Rewrite(query, currentContext.Context, logger.Logger);
    }
}

#pragma warning restore EF1001
