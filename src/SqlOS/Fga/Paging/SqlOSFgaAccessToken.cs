using System.Linq.Expressions;
using System.Reflection;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Who a filter from <c>BuildFilterAsync</c> is for: the caller's resolved subjects and the permission, with
/// the FGA options the filter was built under (the schema, the tables, the tree's depth). The filter carries
/// nothing else; the grants are read by the query that needs them, when it runs.
/// </summary>
internal sealed record SqlOSFgaAccessToken(
    IReadOnlyList<string> SubjectIds,
    string SubjectIdsJson,
    string PermissionId,
    string PermissionKey,
    int? TypeSeq,
    SqlOSFgaOptions Options);

/// <summary>
/// The filter <c>BuildFilterAsync</c> returns is <c>entity =&gt; SqlOSFgaAccess.Visible(entity, token)</c>: it
/// names who the rows are for, and that is all it costs to build. The context's query execution
/// (<c>UseSqlOSFga</c>) evaluates it when a query runs. A page over it is walked from the caller's grants,
/// which reads no grant up front; every other query gets the predicate over the caller's access roots, read
/// at that moment. On a context without SqlOS's query execution EF Core reports the call as untranslatable:
/// there is nothing else that evaluates it.
/// </summary>
internal static class SqlOSFgaAccess
{
    private static readonly MethodInfo VisibleMethod = typeof(SqlOSFgaAccess).GetMethod(nameof(Visible))!;
    private static readonly MethodInfo BuildMethod = typeof(SqlOSFgaFilterBuilder).GetMethod(nameof(SqlOSFgaFilterBuilder.Build))!;

    /// <summary>The marker a filter is made of. Never evaluated: the database answers it.</summary>
    public static bool Visible<T>(T entity, SqlOSFgaAccessToken token)
        => throw new InvalidOperationException(
            "A filter from BuildFilterAsync is evaluated by the database, through the context registered with SqlOS (UseSqlOSFga); it cannot run in memory.");

    public static Expression<Func<T, bool>> Filter<T>(SqlOSFgaAccessToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var entity = Expression.Parameter(typeof(T), "entity");
        return Expression.Lambda<Func<T, bool>>(Expression.Call(VisibleMethod.MakeGenericMethod(typeof(T)), entity, Expression.Constant(token)), entity);
    }

    /// <summary>The token of a filter that is the marker and nothing else (the filter composed unchanged), or null.</summary>
    public static SqlOSFgaAccessToken? TokenOf(LambdaExpression filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Parameters.Count == 1 && filter.Body is MethodCallExpression call && IsMarker(call) && call.Arguments[0] == filter.Parameters[0]
            ? TokenOf(call)
            : null;
    }

    /// <summary>Every token the query's filters carry, each once, in order of appearance.</summary>
    public static IReadOnlyList<SqlOSFgaAccessToken> Tokens(Expression query)
    {
        var search = new TokenSearch();
        search.Visit(query);
        return search.Found;
    }

    /// <summary>
    /// The query with every marker replaced by the predicate over the caller's access roots (the test EF Core
    /// translates: at the level of one root, the row's scope holds it), or by <c>false</c> for a caller with
    /// no root. <paramref name="rootsOf"/> is asked once per token.
    /// </summary>
    public static Expression Resolve(Expression query, Func<SqlOSFgaAccessToken, IReadOnlyList<SqlOSFgaAccessRoot>> rootsOf)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(rootsOf);
        return new Resolver(rootsOf).Visit(query);
    }

    private static bool IsMarker(MethodCallExpression call)
        => call.Method.IsGenericMethod && call.Method.GetGenericMethodDefinition() == VisibleMethod;

    /// <summary>The marker's token: the constant the filter was built with, or a captured reference to it, evaluated.</summary>
    private static SqlOSFgaAccessToken TokenOf(MethodCallExpression call)
    {
        var argument = call.Arguments[1];
        var value = argument is ConstantExpression constant ? constant.Value : Evaluate(argument);
        return value as SqlOSFgaAccessToken
            ?? throw new InvalidOperationException("The filter's token could not be read; a filter from BuildFilterAsync is composed as it was returned.");
    }

    private static object? Evaluate(Expression expression)
    {
        try
        {
            return Expression.Lambda(expression).Compile().DynamicInvoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TargetInvocationException)
        {
            return null;
        }
    }

    private sealed class TokenSearch : ExpressionVisitor
    {
        public List<SqlOSFgaAccessToken> Found { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsMarker(node))
            {
                var token = TokenOf(node);
                if (!Found.Contains(token))
                {
                    Found.Add(token);
                }
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class Resolver(Func<SqlOSFgaAccessToken, IReadOnlyList<SqlOSFgaAccessRoot>> rootsOf) : ExpressionVisitor
    {
        private readonly Dictionary<(SqlOSFgaAccessToken Token, Type Entity), LambdaExpression?> _predicates = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!IsMarker(node))
            {
                return base.VisitMethodCall(node);
            }

            var token = TokenOf(node);
            var entityType = node.Method.GetGenericArguments()[0];
            if (!_predicates.TryGetValue((token, entityType), out var predicate))
            {
                var roots = rootsOf(token);
                predicate = roots.Count == 0
                    ? null
                    : (LambdaExpression)BuildMethod.MakeGenericMethod(entityType).Invoke(null, [roots, token.SubjectIdsJson, token.TypeSeq, SqlOSFgaLineage.Levels(token.Options)])!;
                _predicates[(token, entityType)] = predicate;
            }

            if (predicate is null)
            {
                return Expression.Constant(false);
            }

            return new ParameterReplacer(predicate.Parameters[0], Visit(node.Arguments[0])).Visit(predicate.Body);
        }
    }

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>
/// What SqlOS's query execution did for the queries of the current async flow, for the tests and the
/// benchmarks: <see cref="Collect"/> starts collecting; <see cref="LastCounters"/> is then the counters of the
/// last page walked (null when none was), and <see cref="RootsResolved"/> the access roots read for queries
/// that ran as planned statements (none for a walked page, none for <c>BuildFilterAsync</c> itself).
/// </summary>
internal static class SqlOSFgaPageDiagnostics
{
    private static readonly AsyncLocal<Box?> Current = new();

    public static SqlOSFgaPageCounters? LastCounters
    {
        get => Current.Value?.Counters;
        set
        {
            if (Current.Value is { } box)
            {
                box.Counters = value;
            }
        }
    }

    /// <summary>Access roots transferred for planned statements since <see cref="Collect"/>, or null when none were.</summary>
    public static int? RootsResolved => Current.Value?.RootsResolved;

    /// <summary>Milliseconds spent reading those roots.</summary>
    public static double? RootsMs => Current.Value?.RootsMs;

    /// <summary>Starts (or restarts) collecting in this async flow.</summary>
    public static void Collect() => Current.Value = new Box();

    public static void RecordRoots(int count, double milliseconds)
    {
        if (Current.Value is { } box)
        {
            box.RootsResolved = (box.RootsResolved ?? 0) + count;
            box.RootsMs = (box.RootsMs ?? 0) + milliseconds;
        }
    }

    /// <summary>A holder the queries write into: a value set inside an awaited call does not flow back to the caller, a reference's contents do.</summary>
    private sealed class Box
    {
        public SqlOSFgaPageCounters? Counters { get; set; }

        public int? RootsResolved { get; set; }

        public double? RootsMs { get; set; }
    }
}
