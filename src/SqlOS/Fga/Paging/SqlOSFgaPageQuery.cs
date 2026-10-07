using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Pagination;

namespace SqlOS.Fga.Paging;

/// <summary>
/// What a page query asks for, read off the LINQ query the application wrote: the entity and its table, the
/// filters (as the SQL EF Core translates them to, so any filter EF Core can translate works), the order (which
/// must be one the application declared an index for, so each stream of the page is one index seek), and the
/// query to materialize the page's rows with (the application's query without its filters and order, so
/// <c>Include</c>, <c>AsNoTracking</c> and the like still apply).
/// </summary>
internal sealed class SqlOSFgaPageQuery<T> where T : class, IHasResourceId
{
    private static readonly MethodInfo EFPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

    private SqlOSFgaPageQuery(
        IEntityType entityType,
        SqlOSFgaScopeTable table,
        string? indexSuffix,
        IReadOnlyList<SqlOSFgaPageColumn> orderColumns,
        string tableAlias,
        string? predicateSql,
        IReadOnlyList<DbParameter> predicateParameters,
        IQueryable<T> materialization,
        string fingerprint)
    {
        EntityType = entityType;
        Table = table;
        IndexSuffix = indexSuffix;
        OrderColumns = orderColumns;
        TableAlias = tableAlias;
        PredicateSql = predicateSql;
        PredicateParameters = predicateParameters;
        Materialization = materialization;
        Fingerprint = fingerprint;
    }

    public IEntityType EntityType { get; }

    /// <summary>The table as the database routines address it.</summary>
    public SqlOSFgaScopeTable Table { get; }

    /// <summary>The suffix of the declared index the page's order matches, or null for the key index.</summary>
    public string? IndexSuffix { get; }

    /// <summary>The page's order: the application's sort columns, then the key (the last column).</summary>
    public IReadOnlyList<SqlOSFgaPageColumn> OrderColumns { get; }

    public SqlOSFgaPageColumn Key => OrderColumns[^1];

    /// <summary>The alias EF Core gave the table in the filter SQL; the page's statements use the same one.</summary>
    public string TableAlias { get; }

    /// <summary>The application's filters as one SQL predicate over <see cref="TableAlias"/>, or null when there are none.</summary>
    public string? PredicateSql { get; }

    public IReadOnlyList<DbParameter> PredicateParameters { get; }

    /// <summary>The application's query without its filters and order, to load the page's rows by key.</summary>
    public IQueryable<T> Materialization { get; }

    /// <summary>Binds a cursor to this query shape: the entity, the order, the filters and the permission.</summary>
    public string Fingerprint { get; }

    public string SortKey => $"{EntityType.Name}:{string.Join(",", OrderColumns.Select(c => c.Column))}";

    /// <summary>Reads the query. Throws <see cref="InvalidOperationException"/> with the fix when the query is not a page query SqlOS can run.</summary>
    public static SqlOSFgaPageQuery<T> Analyze(IQueryable<T> query, DbContext context, string permissionKey)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var model = context.Model;
        var entityType = model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not an entity of {context.GetType().Name}.");
        var table = SqlOSFgaScopeColumns.Tables(model).FirstOrDefault(t => t.Table == entityType.GetTableName() && t.Schema == (entityType.GetSchema() ?? model.GetDefaultSchema()))
            ?? throw new InvalidOperationException(
                $"{typeof(T).Name} is not a protected entity of {context.GetType().Name}: it has no {SqlOSFgaLineage.ScopeColumn} column SqlOS maintains. "
                + "Map it in the context registered with SqlOS; pages only work on its entities.");

        var shape = new Shape();
        shape.Read(query.Expression);
        var store = StoreObjectIdentifier.Table(table.Table, table.Schema);

        var key = entityType.FindPrimaryKey()!;
        if (key.Properties.Count != 1)
        {
            throw new InvalidOperationException($"{typeof(T).Name} has a composite key; SqlOS pages need a single-column key.");
        }

        var order = new List<SqlOSFgaPageColumn>();
        foreach (var name in shape.OrderProperties)
        {
            var property = entityType.FindProperty(name)
                ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} is not a mapped property; a page can only sort by mapped columns.");
            if (property.IsNullable)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{name} is nullable. A page sorts by non-nullable columns (NULLs have no single place in a keyset order); sort by a non-nullable column or make it required.");
            }

            order.Add(Column(property, store));
        }

        var keyProperty = key.Properties[0];
        if (order.All(c => c.Property != keyProperty))
        {
            order.Add(Column(keyProperty, store));
        }

        var columns = order.Select(c => c.Column).ToList();
        string? suffix;
        if (columns.SequenceEqual(table.KeyColumns, StringComparer.Ordinal))
        {
            suffix = null;
        }
        else
        {
            var declared = table.Orders.FirstOrDefault(o => o.Columns.SequenceEqual(columns, StringComparer.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No declared index on {typeof(T).Name} covers the order ({string.Join(", ", columns)}). A page is one index seek per access root, "
                    + $"so the order must be one the entity declares an index for: declare HasIndex({string.Join(", ", order.Where(c => c.Property != keyProperty).Select(c => c.Property.Name))}) "
                    + "(SqlOS mirrors each declared index per level of the resource tree), or sort by the key.");
            suffix = declared.Suffix;
        }

        // The filters, as SQL: EF Core translates them in a query of the same table alone, and the page's
        // statements take the WHERE clause as written. Anything that needs another table in FROM (a join
        // through a navigation, say) is refused rather than guessed at; subqueries inside the WHERE are fine.
        string alias;
        string? predicate = null;
        var parameters = new List<DbParameter>();
        var filtered = shape.Filters.Aggregate((IQueryable<T>)context.Set<T>(), (q, filter) => q.Where((Expression<Func<T, bool>>)filter));
        var keyLambda = KeyLambda(keyProperty);
        var projected = (IQueryable)typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Select) && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(T), keyProperty.ClrType)
            .Invoke(null, [filtered, keyLambda])!;
        using (var command = projected.CreateDbCommand())
        {
            (alias, predicate) = ParseFilterSql(command.CommandText, typeof(T).Name);
            if (predicate is not null)
            {
                foreach (DbParameter parameter in command.Parameters)
                {
                    parameters.Add(parameter);
                }
            }
        }

        var materialization = query.Provider.CreateQuery<T>(shape.Materialization(query.Expression));
        var fingerprint = SqlOSCursorCodec.Fingerprint(
            typeof(T).FullName,
            permissionKey,
            string.Join(",", columns),
            predicate,
            string.Join("|", parameters.Select(p => $"{p.ParameterName}={FormatForFingerprint(p.Value)}")));
        return new SqlOSFgaPageQuery<T>(entityType, table, suffix, order, alias, predicate, parameters, materialization, fingerprint);
    }

    private static SqlOSFgaPageColumn Column(IProperty property, StoreObjectIdentifier store)
        => new(property, property.GetColumnName(store)!, property.GetRelationalTypeMapping());

    private static LambdaExpression KeyLambda(IProperty key)
    {
        var e = Expression.Parameter(typeof(T), "e");
        return Expression.Lambda(Expression.Call(EFPropertyMethod.MakeGenericMethod(key.ClrType), e, Expression.Constant(key.Name)), e);
    }

    private static string FormatForFingerprint(object? value)
        => value switch
        {
            null or DBNull => "null",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            byte[] bytes => Convert.ToHexString(bytes),
            _ => value.ToString() ?? "",
        };

    /// <summary>
    /// EF Core writes a single-table query as <c>SELECT …</c>, then <c>FROM table AS alias</c>, then optionally
    /// <c>WHERE …</c> (which may span lines). Returns the alias and the WHERE clause.
    /// </summary>
    internal static (string Alias, string? Predicate) ParseFilterSql(string sql, string entityName)
    {
        var lines = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var from = Array.FindIndex(lines, l => l.StartsWith("FROM ", StringComparison.Ordinal));
        if (from < 0)
        {
            throw Unsupported(entityName, sql);
        }

        var match = Regex.Match(lines[from], @"^FROM (?<table>.+) AS (?<alias>\S+)$");
        if (!match.Success)
        {
            throw Unsupported(entityName, sql);
        }

        var alias = match.Groups["alias"].Value;
        if (from == lines.Length - 1)
        {
            return (alias, null);
        }

        if (!lines[from + 1].StartsWith("WHERE ", StringComparison.Ordinal))
        {
            throw Unsupported(entityName, sql);
        }

        var predicate = string.Join('\n', lines.Skip(from + 1)).Substring("WHERE ".Length);
        if (Regex.IsMatch(predicate, @"^\s*(ORDER BY|LIMIT|OFFSET|GROUP BY)\s", RegexOptions.Multiline))
        {
            throw Unsupported(entityName, sql);
        }

        return (alias, predicate);
    }

    private static InvalidOperationException Unsupported(string entityName, string sql)
        => new(
            $"The filters of this page query over {entityName} translate to SQL that is not a WHERE clause over the entity's table alone "
            + "(a join through a navigation, or a grouping). A page filters by the entity's own columns, with subqueries allowed; "
            + $"move other conditions into the page's rows after they load. EF Core produced:\n{sql}");

    /// <summary>
    /// The calls a page query is made of: <c>Where</c> (kept as filters), <c>OrderBy</c>/<c>ThenBy</c> (the order),
    /// and calls that shape materialization without changing which rows qualify (<c>Include</c>, <c>AsNoTracking</c>,
    /// <c>IgnoreQueryFilters</c>, <c>TagWith</c>…), over the entity's query root. Anything else is refused.
    /// </summary>
    private sealed class Shape : ExpressionVisitor
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

        public List<LambdaExpression> Filters { get; } = [];
        public List<string> OrderProperties { get; } = [];

        public void Read(Expression expression)
        {
            var orders = new List<string>();
            var current = expression;
            while (current is MethodCallExpression call)
            {
                var method = call.Method;
                if (method.DeclaringType == typeof(Queryable) && method.Name == nameof(Queryable.Where) && call.Arguments.Count == 2
                    && Unquote(call.Arguments[1]) is LambdaExpression { Parameters.Count: 1 } filter)
                {
                    Filters.Insert(0, filter);
                }
                else if (method.DeclaringType == typeof(Queryable) && method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.ThenBy) && call.Arguments.Count == 2)
                {
                    orders.Insert(0, OrderProperty(call));
                }
                else if (method.DeclaringType == typeof(Queryable) && method.Name is nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenByDescending))
                {
                    throw new InvalidOperationException("A SqlOS page sorts ascending; descending orders are not supported yet.");
                }
                else if ((method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) || method.DeclaringType == typeof(RelationalQueryableExtensions)) && Passthrough.Contains(method.Name))
                {
                    // Kept for materialization.
                }
                else
                {
                    throw new InvalidOperationException(
                        $"A SqlOS page query is Where and OrderBy/ThenBy over the entity (with Include, AsNoTracking and the like); "
                        + $"{method.Name} is not supported in it. Apply it to the page's rows after they load.");
                }

                current = call.Arguments[0];
            }

            if (current is not QueryRootExpression root || root.ElementType != typeof(T))
            {
                throw new InvalidOperationException($"A SqlOS page query must start from the context's set of {typeof(T).Name}.");
            }

            OrderProperties.AddRange(orders);
        }

        /// <summary>The expression with the filters and the order removed.</summary>
        public Expression Materialization(Expression expression) => Visit(expression);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable)
                && node.Method.Name is nameof(Queryable.Where) or nameof(Queryable.OrderBy) or nameof(Queryable.ThenBy))
            {
                return Visit(node.Arguments[0]);
            }

            return base.VisitMethodCall(node);
        }

        private static string OrderProperty(MethodCallExpression call)
        {
            if (Unquote(call.Arguments[1]) is LambdaExpression lambda)
            {
                var body = lambda.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : lambda.Body;
                if (body is MemberExpression { Expression: ParameterExpression parameter } member && parameter == lambda.Parameters[0])
                {
                    return member.Member.Name;
                }
            }

            throw new InvalidOperationException("A SqlOS page sorts by properties of the entity (e => e.Property); other key selectors are not supported.");
        }

        private static Expression Unquote(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;
    }
}

/// <summary>A column of the page's order: the property, its column name, and the type mapping that creates its parameters.</summary>
internal sealed record SqlOSFgaPageColumn(IProperty Property, string Column, RelationalTypeMapping Mapping);
