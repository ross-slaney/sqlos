using System.Collections;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SqlOS.Fga.Paging;

#pragma warning disable EF1001 // Internal EF Core API: the query compiler is the one place a library can take over how a query runs.

/// <summary>
/// EF Core's query compiler for a context SqlOS pages (<c>UseSqlOSFga</c>). A query that is a page over a
/// filter from <c>BuildFilterAsync</c> (the filter composed unchanged, an order, a <c>Take</c>) is run by
/// SqlOS's walk, which reads about a page's worth of rows for any caller; every other query runs as EF Core
/// would. Queries SqlOS runs itself while walking (the filter's translation, the page's rows) go straight
/// to EF Core.
/// </summary>
internal sealed class SqlOSFgaQueryCompiler(
    IQueryContextFactory queryContextFactory,
    ICompiledQueryCache compiledQueryCache,
    ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
    IDatabase database,
    IDiagnosticsLogger<DbLoggerCategory.Query> logger,
    ICurrentDbContext currentContext,
    IEvaluatableExpressionFilter evaluatableExpressionFilter,
    IModel model,
    IDbContextOptions contextOptions)
    : QueryCompiler(queryContextFactory, compiledQueryCache, compiledQueryCacheKeyGenerator, database, logger, currentContext, evaluatableExpressionFilter, model)
{
    private readonly SqlOSFgaOptionsExtension? _extension = contextOptions.FindExtension<SqlOSFgaOptionsExtension>();

    public override TResult Execute<TResult>(Expression query)
    {
        if (Plan(query) is { } plan)
        {
            return Result<TResult>(plan, plan.RunAsync(CancellationToken.None).GetAwaiter().GetResult());
        }

        return base.Execute<TResult>(query);
    }

    public override TResult ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken = default)
    {
        if (Plan(query) is { } plan)
        {
            return AsyncResult<TResult>(plan, cancellationToken);
        }

        return base.ExecuteAsync<TResult>(query, cancellationToken);
    }

    private ISqlOSFgaPagePlan? Plan(Expression query)
    {
        if (_extension is null || SqlOSFgaPagePlanner.Walking.Value)
        {
            return null;
        }

        var context = currentContext.Context;
        var loggerFactory = _extension.LoggerFactory ?? context.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        return SqlOSFgaPagePlanner.TryPlan(query, context, _extension.Options, loggerFactory.CreateLogger<SqlOSFgaQueryCompiler>());
    }

    private static TResult Result<TResult>(ISqlOSFgaPagePlan plan, IList rows)
    {
        var result = typeof(TResult);
        if (plan.Single)
        {
            return (TResult)plan.SingleResult(rows)!;
        }

        if (result.IsAssignableFrom(typeof(List<>).MakeGenericType(plan.ResultType)))
        {
            return (TResult)rows;
        }

        throw new InvalidOperationException($"SqlOS cannot return a walked page as {result.Name}.");
    }

    private static TResult AsyncResult<TResult>(ISqlOSFgaPagePlan plan, CancellationToken cancellationToken)
    {
        var result = typeof(TResult);
        if (plan.Single)
        {
            // Task<T> for First/FirstOrDefault/Single/SingleOrDefault.
            var element = result.GetGenericArguments()[0];
            return (TResult)typeof(SqlOSFgaQueryCompiler)
                .GetMethod(nameof(SingleAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(element)
                .Invoke(null, [plan, cancellationToken])!;
        }

        if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return (TResult)Activator.CreateInstance(typeof(PageAsyncEnumerable<>).MakeGenericType(plan.ResultType), plan, cancellationToken)!;
        }

        throw new InvalidOperationException($"SqlOS cannot return a walked page as {result.Name}.");
    }

    private static async Task<T> SingleAsync<T>(ISqlOSFgaPagePlan plan, CancellationToken cancellationToken)
    {
        var rows = await plan.RunAsync(cancellationToken).ConfigureAwait(false);
        return (T)plan.SingleResult(rows)!;
    }

    /// <summary>The page's rows as EF Core hands rows to <c>ToListAsync</c> and <c>await foreach</c>: run when enumerated, once.</summary>
    private sealed class PageAsyncEnumerable<T>(ISqlOSFgaPagePlan plan, CancellationToken cancellationToken) : IAsyncEnumerable<T>
    {
        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken enumerationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumerationToken);
            var rows = await plan.RunAsync(linked.Token).ConfigureAwait(false);
            foreach (T row in rows)
            {
                yield return row;
            }
        }
    }
}

#pragma warning restore EF1001
