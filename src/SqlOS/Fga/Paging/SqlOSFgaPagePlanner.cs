using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Paging;

/// <summary>A page SqlOS will find itself: the rows of the query, in its order, by the walk.</summary>
internal interface ISqlOSFgaPagePlan
{
    /// <summary>The element type of the result: the entity, or what a trailing <c>Select</c> projects to.</summary>
    Type ResultType { get; }

    /// <summary>Whether the query asked for one row (<c>First</c>, <c>FirstOrDefault</c>, <c>Single</c>, <c>SingleOrDefault</c>).</summary>
    bool Single { get; }

    /// <summary>Runs the walk and loads the rows: a <c>List&lt;ResultType&gt;</c>.</summary>
    Task<IList> RunAsync(CancellationToken cancellationToken);

    /// <summary>The one row a single-row query returns, with that method's semantics (empty, default, more than one).</summary>
    object? SingleResult(IList rows);
}

/// <summary>
/// Recognizes a page over a filter from <c>BuildFilterAsync</c> in the LINQ query the application wrote, and
/// plans its walk. The shape is the one an application writes anyway: the entity's set, <c>Where</c> with the
/// filter composed unchanged, any other <c>Where</c> over the entity's own columns (a keyset cursor among
/// them), <c>OrderBy</c>/<c>ThenBy</c> (all ascending or all descending), optionally <c>Skip</c>, then
/// <c>Take</c> or a single-row method, optionally a <c>Select</c> on the way out; <c>Include</c>,
/// <c>AsNoTracking</c> and the like pass through. Anything else is not a page and runs as EF Core would.
/// </summary>
internal static class SqlOSFgaPagePlanner
{
    /// <summary>True in the async flow of a walk (and of a plan's translation), so the queries SqlOS issues itself are not planned again.</summary>
    public static readonly AsyncLocal<bool> Walking = new();

    private static readonly ConcurrentDictionary<string, byte> Warned = new();
    private static readonly MethodInfo BuildMethod = typeof(SqlOSFgaPagePlanner).GetMethod(nameof(Build), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static ISqlOSFgaPagePlan? TryPlan(Expression query, DbContext context, SqlOSFgaOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (Walking.Value)
        {
            return null;
        }

        var shape = PageShape.Read(query);
        if (shape is null)
        {
            return null;
        }

        // Translating the page's filters runs a query of its own through this same compiler.
        Walking.Value = true;
        try
        {
            return (ISqlOSFgaPagePlan?)BuildMethod.MakeGenericMethod(shape.EntityType).Invoke(null, [shape, context, options, logger]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException reason)
        {
            // A page SqlOS cannot walk runs as EF Core would, the filter as a predicate, and the reason is
            // logged once per query shape, the way an unindexed sort is reported.
            var key = $"{shape.EntityType.FullName}:{string.Join(",", shape.Order.Select(o => o.Property + (o.Descending ? " desc" : "")))}:{reason.Message}";
            if (Warned.TryAdd(key, 0))
            {
                logger.LogWarning(
                    "A page over {Entity} runs as a plain filtered query rather than SqlOS's walk, so its cost is the optimizer's choice. {Reason}",
                    shape.EntityType.Name,
                    reason.Message);
            }

            return null;
        }
        finally
        {
            Walking.Value = false;
        }
    }

    private static ISqlOSFgaPagePlan Build<T>(PageShape shape, DbContext context, SqlOSFgaOptions options, ILogger logger)
        where T : class, IHasResourceId
    {
        var query = SqlOSFgaPageQuery<T>.Create(context, shape.Filters, shape.Order, shape.Materialization);
        return new Plan<T>(shape, query, context, options, logger);
    }

    private sealed class Plan<T>(PageShape shape, SqlOSFgaPageQuery<T> query, DbContext context, SqlOSFgaOptions options, ILogger logger) : ISqlOSFgaPagePlan
        where T : class, IHasResourceId
    {
        public Type ResultType => shape.Projection?.ReturnType ?? typeof(T);

        public bool Single => shape.Terminal != Terminal.None;

        public async Task<IList> RunAsync(CancellationToken cancellationToken)
        {
            Walking.Value = true;
            try
            {
                var wanted = shape.Skip + shape.PageSize;
                if (wanted == 0)
                {
                    return Empty();
                }

                var clock = Stopwatch.StartNew();
                var provider = SqlOSDatabase.Resolve(context.Database);
                var backend = new SqlOSFgaPageBackend<T>(context, provider, options, query, shape.Token.SubjectIdsJson, shape.Token.PermissionId);
                var executor = new SqlOSFgaPageExecutor(backend);
                var counters = executor.Counters;
                var positions = await executor.PageAsync(query.After, wanted, cancellationToken).ConfigureAwait(false);
                counters.WalkMs = clock.Elapsed.TotalMilliseconds;
                if (shape.Skip > 0)
                {
                    positions = positions.Skip(shape.Skip).ToList();
                }

                IList rows;
                if (positions.Count == 0)
                {
                    rows = Empty();
                }
                else
                {
                    clock.Restart();
                    rows = await SqlOSFgaPageLoader.LoadAsync(query, positions.Select(p => p[^1]!).ToList(), shape.Projection, cancellationToken).ConfigureAwait(false);
                    counters.LoadMs = clock.Elapsed.TotalMilliseconds;
                }

                SqlOSFgaPageDiagnostics.LastCounters = counters;
                logger.LogDebug("SqlOS page over {Entity}: {Counters}", typeof(T).Name, counters);
                return rows;
            }
            finally
            {
                Walking.Value = false;
            }
        }

        public object? SingleResult(IList rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            return shape.Terminal switch
            {
                Terminal.First => rows.Count > 0 ? rows[0] : throw new InvalidOperationException("Sequence contains no elements"),
                Terminal.FirstOrDefault => rows.Count > 0 ? rows[0] : Default(ResultType),
                Terminal.Single => rows.Count switch
                {
                    1 => rows[0],
                    0 => throw new InvalidOperationException("Sequence contains no elements"),
                    _ => throw new InvalidOperationException("Sequence contains more than one element"),
                },
                Terminal.SingleOrDefault => rows.Count switch
                {
                    1 => rows[0],
                    0 => Default(ResultType),
                    _ => throw new InvalidOperationException("Sequence contains more than one element"),
                },
                _ => throw new InvalidOperationException("Not a single-row query."),
            };
        }

        private IList Empty() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ResultType))!;

        private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private enum Terminal
    {
        None,
        First,
        FirstOrDefault,
        Single,
        SingleOrDefault,
    }

    /// <summary>The parts of a page query, read from the outside in.</summary>
    private sealed class PageShape
    {
        private static readonly HashSet<string> Passthrough =
        [
            nameof(EntityFrameworkQueryableExtensions.Include), nameof(EntityFrameworkQueryableExtensions.ThenInclude),
            nameof(EntityFrameworkQueryableExtensions.AsNoTracking), nameof(EntityFrameworkQueryableExtensions.AsNoTrackingWithIdentityResolution),
            nameof(EntityFrameworkQueryableExtensions.AsTracking), nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters),
            nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes), nameof(EntityFrameworkQueryableExtensions.TagWith),
            nameof(EntityFrameworkQueryableExtensions.TagWithCallSite), nameof(RelationalQueryableExtensions.AsSplitQuery),
            nameof(RelationalQueryableExtensions.AsSingleQuery),
        ];

        private readonly List<LambdaExpression> _overRows = [];

        public Type EntityType { get; private set; } = null!;

        public SqlOSFgaAccessToken Token { get; private set; } = null!;

        /// <summary>The application's own filters (a keyset cursor among them), the authorization filter excluded.</summary>
        public List<LambdaExpression> Filters { get; } = [];

        public List<(string Property, bool Descending)> Order { get; } = [];

        public LambdaExpression? Projection { get; private set; }

        public Expression Materialization { get; private set; } = null!;

        public int Skip { get; private set; }

        public Terminal Terminal { get; private set; }

        /// <summary>The rows the walk is asked for: the Take, or what the single-row method needs to decide.</summary>
        public int PageSize { get; private set; }

        public static PageShape? Read(Expression query)
        {
            var shape = new PageShape();
            return shape.ReadCore(query) ? shape : null;
        }

        private bool ReadCore(Expression query)
        {
            var sawPage = false;
            var sawRows = false;
            var sawFilter = false;
            var orders = new List<(string, bool)>();
            var current = query;
            while (current is MethodCallExpression call)
            {
                var method = call.Method;
                var name = method.Name;
                if (method.DeclaringType == typeof(Queryable))
                {
                    switch (name)
                    {
                        case nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)
                            when !sawPage && call.Arguments.Count is 1 or 2:
                            Terminal = name switch
                            {
                                nameof(Queryable.First) => Terminal.First,
                                nameof(Queryable.FirstOrDefault) => Terminal.FirstOrDefault,
                                nameof(Queryable.Single) => Terminal.Single,
                                _ => Terminal.SingleOrDefault,
                            };
                            PageSize = Terminal is Terminal.Single or Terminal.SingleOrDefault ? 2 : 1;
                            sawPage = true;
                            if (call.Arguments.Count == 2)
                            {
                                if (Unquote(call.Arguments[1]) is not LambdaExpression { Parameters.Count: 1 } predicate)
                                {
                                    return false;
                                }

                                AddFilter(predicate, ref sawFilter);
                                sawRows = true;
                            }

                            break;
                        case nameof(Queryable.Take) when !sawPage && call.Arguments.Count == 2:
                            if (Evaluate(call.Arguments[1]) is not { } take || take < 0)
                            {
                                return false;
                            }

                            PageSize = take;
                            sawPage = true;
                            break;
                        case nameof(Queryable.Skip) when sawPage && Skip == 0 && call.Arguments.Count == 2:
                            if (Evaluate(call.Arguments[1]) is not { } skip || skip < 0)
                            {
                                return false;
                            }

                            Skip = skip;
                            break;
                        case nameof(Queryable.Select) when Projection is null && !sawRows && call.Arguments.Count == 2 && Unquote(call.Arguments[1]) is LambdaExpression { Parameters.Count: 1 } selector:
                            // A projection on the way out only: the filters and the order below it are over the entity.
                            Projection = selector;
                            break;
                        case nameof(Queryable.Where) when call.Arguments.Count == 2 && Unquote(call.Arguments[1]) is LambdaExpression { Parameters.Count: 1 } filter:
                            AddFilter(filter, ref sawFilter);
                            sawRows = true;
                            break;
                        case nameof(Queryable.OrderBy) or nameof(Queryable.ThenBy) or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenByDescending) when call.Arguments.Count == 2:
                            if (Unquote(call.Arguments[1]) is not LambdaExpression { Parameters.Count: 1 } keySelector || OrderProperty(keySelector) is not { } property)
                            {
                                return false;
                            }

                            _overRows.Add(keySelector);
                            orders.Insert(0, (property, name.EndsWith("Descending", StringComparison.Ordinal)));
                            sawRows = true;
                            break;
                        default:
                            return false;
                    }
                }
                else if ((method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) || method.DeclaringType == typeof(RelationalQueryableExtensions)) && Passthrough.Contains(name))
                {
                    // Kept for materialization.
                }
                else
                {
                    return false;
                }

                current = call.Arguments[0];
            }

            if (!sawFilter || !sawPage || current is not EntityQueryRootExpression root || root.GetType() != typeof(EntityQueryRootExpression))
            {
                return false;
            }

            var entityType = root.ElementType;
            if (!entityType.IsClass || !typeof(IHasResourceId).IsAssignableFrom(entityType) || _overRows.Any(l => l.Parameters[0].Type != entityType))
            {
                return false;
            }

            EntityType = entityType;
            Order.AddRange(orders);
            Materialization = new MaterializationShape().Visit(query);
            return true;
        }

        private void AddFilter(LambdaExpression filter, ref bool sawFilter)
        {
            _overRows.Add(filter);
            if (!sawFilter && SqlOSFgaFilterRegistry.Find(filter) is { } token)
            {
                Token = token;
                sawFilter = true;
                return;
            }

            Filters.Insert(0, filter);
        }

        private static string? OrderProperty(LambdaExpression lambda)
        {
            var body = lambda.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : lambda.Body;
            return body is MemberExpression { Expression: ParameterExpression parameter } member && parameter == lambda.Parameters[0]
                ? member.Member.Name
                : null;
        }

        /// <summary>A Take or Skip count: a literal, or a captured variable, evaluated.</summary>
        private static int? Evaluate(Expression expression)
        {
            try
            {
                var value = expression is ConstantExpression constant ? constant.Value : Expression.Lambda(expression).Compile().DynamicInvoke();
                return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or FormatException or OverflowException or TargetInvocationException)
            {
                return null;
            }
        }

        private static Expression Unquote(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

        /// <summary>The query with everything that chooses, orders, pages or projects rows removed: what loads the page's rows by key.</summary>
        private sealed class MaterializationShape : ExpressionVisitor
        {
            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.DeclaringType == typeof(Queryable))
                {
                    return Visit(node.Arguments[0]);
                }

                return base.VisitMethodCall(node);
            }
        }
    }
}
