using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlOS.Database;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Paging;

#pragma warning disable EF1001 // Internal EF Core API: the query compiler is the one place a library can take over how a query runs.

/// <summary>
/// EF Core's query compiler for a context SqlOS pages (<c>UseSqlOSFga</c>): where a filter from
/// <c>BuildFilterAsync</c> is evaluated. A query that is a page over the filter (the filter composed
/// unchanged, an order, a <c>Take</c>) is run by SqlOS's walk, which reads about a page's worth of rows for
/// any caller and no grant up front. Every other query over the filter runs as EF Core would, with the
/// marker replaced by the predicate over the caller's access roots, read at that moment (so a count or a join
/// pays for the roots when it runs, and a page never does). Queries SqlOS issues itself while walking or
/// resolving (the filter's translation, the roots, the page's rows) go straight to EF Core.
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
    private static readonly MethodInfo ResolvedTaskMethod = typeof(SqlOSFgaQueryCompiler).GetMethod(nameof(ResolvedTaskAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo SingleMethod = typeof(SqlOSFgaQueryCompiler).GetMethod(nameof(SingleAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly SqlOSFgaOptionsExtension? _extension = contextOptions.FindExtension<SqlOSFgaOptionsExtension>();

    public override TResult Execute<TResult>(Expression query)
    {
        if (_extension is null || SqlOSFgaPagePlanner.Walking.Value)
        {
            return base.Execute<TResult>(query);
        }

        // ToQueryString, CreateDbCommand and untyped enumeration ask for IEnumerable: they get the planned statement.
        if (typeof(TResult) != typeof(IEnumerable) && Plan(query) is { } plan)
        {
            return Result<TResult>(plan, plan.RunAsync(CancellationToken.None).GetAwaiter().GetResult());
        }

        return base.Execute<TResult>(ResolveRoots(query));
    }

    public override TResult ExecuteAsync<TResult>(Expression query, CancellationToken cancellationToken = default)
    {
        if (_extension is null || SqlOSFgaPagePlanner.Walking.Value)
        {
            return base.ExecuteAsync<TResult>(query, cancellationToken);
        }

        if (Plan(query) is { } plan)
        {
            return AsyncResult<TResult>(plan, cancellationToken);
        }

        var tokens = SqlOSFgaAccess.Tokens(query);
        if (tokens.Count == 0)
        {
            return base.ExecuteAsync<TResult>(query, cancellationToken);
        }

        var result = typeof(TResult);
        if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return (TResult)Activator.CreateInstance(typeof(ResolvedAsyncEnumerable<>).MakeGenericType(result.GetGenericArguments()[0]), this, query, tokens, cancellationToken)!;
        }

        if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return (TResult)ResolvedTaskMethod.MakeGenericMethod(result.GetGenericArguments()[0]).Invoke(this, [query, tokens, cancellationToken])!;
        }

        return base.ExecuteAsync<TResult>(ResolveRoots(query), cancellationToken);
    }

    private ISqlOSFgaPagePlan? Plan(Expression query)
    {
        var context = currentContext.Context;
        var loggerFactory = _extension!.LoggerFactory ?? context.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        return SqlOSFgaPagePlanner.TryPlan(query, context, loggerFactory.CreateLogger<SqlOSFgaQueryCompiler>());
    }

    private TResult ExecuteBase<TResult>(Expression query) => base.Execute<TResult>(query);

    private TResult ExecuteBaseAsync<TResult>(Expression query, CancellationToken cancellationToken) => base.ExecuteAsync<TResult>(query, cancellationToken);

    // ---- A planned statement: the markers replaced by the predicate over the roots, read now ----

    private Expression ResolveRoots(Expression query)
    {
        var tokens = SqlOSFgaAccess.Tokens(query);
        if (tokens.Count == 0)
        {
            return query;
        }

        var roots = new Dictionary<SqlOSFgaAccessToken, IReadOnlyList<SqlOSFgaAccessRoot>>();
        foreach (var token in tokens)
        {
            roots[token] = Roots(token);
        }

        return SqlOSFgaAccess.Resolve(query, token => roots[token]);
    }

    private async Task<Expression> ResolveRootsAsync(Expression query, IReadOnlyList<SqlOSFgaAccessToken> tokens, CancellationToken cancellationToken)
    {
        var roots = new Dictionary<SqlOSFgaAccessToken, IReadOnlyList<SqlOSFgaAccessRoot>>();
        foreach (var token in tokens)
        {
            roots[token] = await RootsAsync(token, cancellationToken).ConfigureAwait(false);
        }

        return SqlOSFgaAccess.Resolve(query, token => roots[token]);
    }

    /// <summary>
    /// The caller's access roots, read now: the resources their live subjects hold a current grant on with a
    /// role that includes the permission. Read by the query that needs them, each time it runs, so a statement
    /// sees the grants of the moment, as group membership was resolved when the filter was built.
    /// </summary>
    private IQueryable<SqlOSFgaAccessRoot> RootsQuery(SqlOSFgaAccessToken token)
    {
        var context = currentContext.Context;
        var provider = SqlOSDatabase.Resolve(context.Database);
        return context.Set<SqlOSFgaAccessRoot>()
            .FromSqlRaw(provider.BuildAccessRootsQuerySql(token.Options), token.SubjectIdsJson, token.PermissionId)
            .AsNoTracking();
    }

    private List<SqlOSFgaAccessRoot> Roots(SqlOSFgaAccessToken token)
    {
        SqlOSFgaPagePlanner.Walking.Value = true;
        var clock = Stopwatch.StartNew();
        try
        {
            var roots = RootsQuery(token).ToList();
            SqlOSFgaPageDiagnostics.RecordRoots(roots.Count, clock.Elapsed.TotalMilliseconds);
            return roots;
        }
        finally
        {
            SqlOSFgaPagePlanner.Walking.Value = false;
        }
    }

    private async Task<List<SqlOSFgaAccessRoot>> RootsAsync(SqlOSFgaAccessToken token, CancellationToken cancellationToken)
    {
        SqlOSFgaPagePlanner.Walking.Value = true;
        var clock = Stopwatch.StartNew();
        try
        {
            var roots = await RootsQuery(token).ToListAsync(cancellationToken).ConfigureAwait(false);
            SqlOSFgaPageDiagnostics.RecordRoots(roots.Count, clock.Elapsed.TotalMilliseconds);
            return roots;
        }
        finally
        {
            SqlOSFgaPagePlanner.Walking.Value = false;
        }
    }

    private async Task<T> ResolvedTaskAsync<T>(Expression query, IReadOnlyList<SqlOSFgaAccessToken> tokens, CancellationToken cancellationToken)
    {
        var resolved = await ResolveRootsAsync(query, tokens, cancellationToken).ConfigureAwait(false);
        return await ExecuteBaseAsync<Task<T>>(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The statement's rows as EF Core hands rows to <c>ToListAsync</c> and <c>await foreach</c>: the roots read when enumerated.</summary>
    private sealed class ResolvedAsyncEnumerable<T>(SqlOSFgaQueryCompiler compiler, Expression query, IReadOnlyList<SqlOSFgaAccessToken> tokens, CancellationToken cancellationToken) : IAsyncEnumerable<T>
    {
        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken enumerationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumerationToken);
            var resolved = await compiler.ResolveRootsAsync(query, tokens, linked.Token).ConfigureAwait(false);
            await foreach (var row in compiler.ExecuteBaseAsync<IAsyncEnumerable<T>>(resolved, cancellationToken).WithCancellation(enumerationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    // ---- A walked page ----

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
            return (TResult)SingleMethod.MakeGenericMethod(result.GetGenericArguments()[0]).Invoke(null, [plan, cancellationToken])!;
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

    /// <summary>The page's rows as EF Core hands rows to <c>ToListAsync</c> and <c>await foreach</c>: run when enumerated, once per enumeration.</summary>
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
