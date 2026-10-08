using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Paging;

/// <summary>
/// What a page asks for, read off the LINQ query the application wrote: the entity and its table, the position
/// the page starts after (a keyset predicate in the query, when it has one), the other filters (as the SQL EF
/// Core translates them to, so any filter EF Core can translate works), the order (one the application
/// declared an index for, so each stream of the page is one index seek), and the query to materialize the
/// page's rows with (the application's query without its filters, order and page, so <c>Include</c>,
/// <c>AsNoTracking</c> and the like still apply).
/// </summary>
internal sealed class SqlOSFgaPageQuery<T> where T : class, IHasResourceId
{
    private static readonly MethodInfo EFPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

    private SqlOSFgaPageQuery(
        IEntityType entityType,
        SqlOSFgaScopeTable table,
        string? indexSuffix,
        IReadOnlyList<SqlOSFgaPageColumn> orderColumns,
        bool descending,
        object?[]? after,
        string tableAlias,
        string? predicateSql,
        IReadOnlyList<DbParameter> predicateParameters,
        IQueryable<T> materialization)
    {
        EntityType = entityType;
        Table = table;
        IndexSuffix = indexSuffix;
        OrderColumns = orderColumns;
        Descending = descending;
        After = after;
        TableAlias = tableAlias;
        PredicateSql = predicateSql;
        PredicateParameters = predicateParameters;
        Materialization = materialization;
    }

    public IEntityType EntityType { get; }

    /// <summary>The table as the database routines address it.</summary>
    public SqlOSFgaScopeTable Table { get; }

    /// <summary>The suffix of the declared index the page's order matches, or null for the key index.</summary>
    public string? IndexSuffix { get; }

    /// <summary>The page's order: the application's sort columns, then the key (the last column).</summary>
    public IReadOnlyList<SqlOSFgaPageColumn> OrderColumns { get; }

    public SqlOSFgaPageColumn Key => OrderColumns[^1];

    /// <summary>Whether the order runs backwards (every column descending).</summary>
    public bool Descending { get; }

    /// <summary>The position the page starts after (the order's values, key last), from a keyset predicate in the query; null for the first page.</summary>
    public object?[]? After { get; }

    /// <summary>The alias EF Core gave the table in the filter SQL; the page's statements use the same one.</summary>
    public string TableAlias { get; }

    /// <summary>The application's filters as one SQL predicate over <see cref="TableAlias"/>, or null when there are none.</summary>
    public string? PredicateSql { get; }

    public IReadOnlyList<DbParameter> PredicateParameters { get; }

    /// <summary>The application's query without its filters, order and page, to load the page's rows by key.</summary>
    public IQueryable<T> Materialization { get; }

    /// <summary>
    /// Reads the page's parts. Throws <see cref="InvalidOperationException"/> saying what to change when the
    /// query is one SqlOS cannot walk (an order no declared index covers, a filter that is not a predicate
    /// over the entity's own table); such a page runs as a plain query.
    /// </summary>
    public static SqlOSFgaPageQuery<T> Create(
        DbContext context,
        IReadOnlyList<LambdaExpression> filters,
        IReadOnlyList<(string Property, bool Descending)> order,
        Expression materialization)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(materialization);
        var model = context.Model;
        var entityType = model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not an entity of {context.GetType().Name}.");
        var table = SqlOSFgaScopeColumns.Tables(model).FirstOrDefault(t => t.Table == entityType.GetTableName() && t.Schema == (entityType.GetSchema() ?? model.GetDefaultSchema()))
            ?? throw new InvalidOperationException(
                $"{typeof(T).Name} is not a protected entity of {context.GetType().Name}: it has no {SqlOSFgaLineage.ScopeColumn} column SqlOS maintains. "
                + "Map it in the context registered with SqlOS; pages only work on its entities.");
        var store = StoreObjectIdentifier.Table(table.Table, table.Schema);

        var key = entityType.FindPrimaryKey()!;
        if (key.Properties.Count != 1)
        {
            throw new InvalidOperationException($"{typeof(T).Name} has a composite key; SqlOS pages need a single-column key.");
        }

        // The order ends at the key: the key is unique, so nothing after it decides anything, and the walk's
        // positions end with the key. An order without the key gets it appended.
        var keyProperty = key.Properties[0];
        var kept = order.TakeWhile((o, i) => i == 0 || order[i - 1].Property != keyProperty.Name).ToList();
        var descending = kept.Count > 0 && kept[0].Descending;
        if (kept.Any(o => o.Descending != descending))
        {
            throw new InvalidOperationException(
                $"The order of this page over {typeof(T).Name} mixes directions ({string.Join(", ", kept.Select(o => o.Property + (o.Descending ? " desc" : " asc")))}). "
                + "A page's order is all ascending or all descending: the index that serves it is read one way.");
        }

        var orderColumns = new List<SqlOSFgaPageColumn>();
        foreach (var (name, _) in kept)
        {
            var property = entityType.FindProperty(name)
                ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} is not a mapped property; a page can only sort by mapped columns.");
            if (property.IsNullable)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{name} is nullable. A page sorts by non-nullable columns (NULLs have no single place in a keyset order); sort by a non-nullable column or make it required.");
            }

            orderColumns.Add(Column(property, store));
        }

        if (orderColumns.Count == 0 || orderColumns[^1].Property != keyProperty)
        {
            orderColumns.Add(Column(keyProperty, store));
        }

        var columns = orderColumns.Select(c => c.Column).ToList();
        string? suffix;
        if (columns.SequenceEqual(table.KeyColumns, StringComparer.Ordinal))
        {
            suffix = null;
        }
        else
        {
            var declared = table.Orders.FirstOrDefault(o => o.Columns.SequenceEqual(columns, StringComparer.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No declared index on {typeof(T).Name} covers the order ({string.Join(", ", columns)}). A page is one index seek per place the caller is granted, "
                    + $"so the order must be one the entity declares an index for: declare HasIndex({string.Join(", ", orderColumns.Where(c => c.Property != keyProperty).Select(c => c.Property.Name))}) "
                    + "(SqlOS mirrors each declared index per level of the resource tree), or sort by the key.");
            suffix = declared.Suffix;
        }

        // A keyset predicate over the page's order is the position the page starts after, which every stream
        // of the page seeks to. Any other filter is a predicate the streams test.
        object?[]? after = null;
        var predicates = new List<LambdaExpression>();
        var orderNames = orderColumns.Select(c => c.Property.Name).ToList();
        foreach (var filter in filters)
        {
            if (after is null && SqlOSFgaKeyset.TryRead(filter, orderNames, descending, out var position))
            {
                after = Position(position, orderColumns);
                continue;
            }

            predicates.Add(filter);
        }

        // The filters, as SQL: EF Core translates them in a query of the same table alone (the entity's global
        // query filters included, unless the query ignores them), and the page's statements take the WHERE
        // clause as written. Anything that needs another table in FROM (a join through a navigation, say) is
        // refused rather than guessed at; subqueries inside the WHERE are fine.
        string alias;
        string? predicate = null;
        var parameters = new List<DbParameter>();
        var set = (IQueryable<T>)context.Set<T>();
        if (MethodSearch.Found(materialization, nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters)))
        {
            set = set.IgnoreQueryFilters();
        }

        var filtered = predicates.Aggregate(set, (q, filter) => q.Where((Expression<Func<T, bool>>)filter));
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

        var load = set.Provider.CreateQuery<T>(materialization);
        return new SqlOSFgaPageQuery<T>(entityType, table, suffix, orderColumns, descending, after, alias, predicate, parameters, load);
    }

    private static SqlOSFgaPageColumn Column(IProperty property, StoreObjectIdentifier store)
        => new(property, property.GetColumnName(store)!, property.GetRelationalTypeMapping());

    private static LambdaExpression KeyLambda(IProperty key)
    {
        var e = Expression.Parameter(typeof(T), "e");
        return Expression.Lambda(Expression.Call(EFPropertyMethod.MakeGenericMethod(key.ClrType), e, Expression.Constant(key.Name)), e);
    }

    /// <summary>The keyset's values as the database holds them: through the property's value converter, enums as their underlying number.</summary>
    private static object?[] Position(object?[] values, IReadOnlyList<SqlOSFgaPageColumn> columns)
    {
        var position = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var value = values[i];
            var converter = columns[i].Property.GetValueConverter();
            if (converter is not null)
            {
                value = converter.ConvertToProvider(value);
            }
            else if (value is Enum number)
            {
                value = Convert.ChangeType(number, Enum.GetUnderlyingType(number.GetType()), CultureInfo.InvariantCulture);
            }

            position[i] = value;
        }

        return position;
    }

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
            $"The filters of this page over {entityName} translate to SQL that is not a WHERE clause over the entity's table alone "
            + "(a join through a navigation, or a grouping). A page filters by the entity's own columns, with subqueries allowed; "
            + $"move other conditions into the page's rows after they load. EF Core produced:\n{sql}");

    /// <summary>Whether a query expression calls a method of the given name anywhere.</summary>
    private sealed class MethodSearch(string name) : ExpressionVisitor
    {
        private bool _found;

        public static bool Found(Expression expression, string name)
        {
            var search = new MethodSearch(name);
            search.Visit(expression);
            return search._found;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == name)
            {
                _found = true;
            }

            return base.VisitMethodCall(node);
        }
    }
}

/// <summary>A column of the page's order: the property, its column name, and the type mapping that creates its parameters.</summary>
internal sealed record SqlOSFgaPageColumn(IProperty Property, string Column, RelationalTypeMapping Mapping);
