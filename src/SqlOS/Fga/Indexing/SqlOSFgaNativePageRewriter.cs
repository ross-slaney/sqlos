using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Indexing;

/// <summary>
/// Replaces an eligible authorization-filtered page with one ordinary FromSql query. EF still executes and
/// materializes it, including an outer projection. Shapes that cannot be proven equivalent retain the TVF.
/// </summary>
internal static class SqlOSFgaNativePageRewriter
{
    internal static readonly AsyncLocal<bool> Suppressed = new();
    private static readonly MethodInfo BuildMethod = typeof(SqlOSFgaNativePageRewriter).GetMethod(nameof(BuildPage), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo PropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

    internal static Expression Rewrite(Expression query, DbContext context, ILogger logger)
    {
        if (Suppressed.Value)
            return query;
        var discovery = SqlOSFgaNativeOptions.Discovery(context);
        if (discovery == null || discovery.Entities.Count == 0)
            return query;
        // EF's IgnoreQueryFilters annotation can affect sibling query roots as well as its own source.
        // Translating only an inner page would otherwise bake filters back into an explicitly ignored query.
        var ignoredFilters = new IgnoredFilterFinder();
        ignoredFilters.Visit(query);
        if (ignoredFilters.Found)
        {
            if (HasAuthorizationCall(query))
                logger.LogWarning("SqlOS native authorization page was not used: IgnoreQueryFilters affects the complete query. The original TVF query is preserved.");
            return query;
        }
        return new Rewriter(context, logger, discovery).Visit(query);
    }

    private sealed class Rewriter(DbContext context, ILogger logger, SqlOSFgaScopeDiscovery discovery) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // These annotations affect the whole EF query, including navigation filters introduced outside
            // a Take. Do not optimize an inner page while losing an enclosing include/ignore boundary.
            if (node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && node.Method.Name is nameof(EntityFrameworkQueryableExtensions.Include)
                    or nameof(EntityFrameworkQueryableExtensions.ThenInclude)
                    or nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters))
                return node;
            if (node.Method.DeclaringType == typeof(Queryable))
            {
                if (node.Method.Name == nameof(Queryable.Take) && node.Arguments.Count == 2 && node.Arguments[1].Type == typeof(int))
                {
                    var rewritten = TryPage(node.Arguments[0], node.Arguments[1]);
                    if (rewritten != null)
                        return rewritten;
                }
                else if (node.Arguments.Count == 1 && node.Method.Name is nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault))
                {
                    var single = node.Method.Name is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault);
                    var rewritten = TryPage(node.Arguments[0], Expression.Constant(single ? 2 : 1));
                    if (rewritten != null)
                        return node.Update(node.Object, [rewritten]);
                }
            }
            return base.VisitMethodCall(node);
        }

        private Expression? TryPage(Expression source, Expression take)
        {
            if (!HasAuthorizationCall(source))
                return null;
            try
            {
                var shape = ReadShape(source, take, context);
                var entity = discovery.Entities.SingleOrDefault(e => e.EntityType.ClrType == shape.EntityType)
                    ?? throw Unsupported("the entity has no initialized native scope projection");
                return (Expression)BuildMethod.MakeGenericMethod(shape.EntityType).Invoke(null, [context, entity, shape])!;
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                logger.LogWarning("SqlOS native authorization page was not used: {Reason}. The original TVF query is preserved.", error.InnerException.Message);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                logger.LogWarning("SqlOS native authorization page was not used: {Reason}. The original TVF query is preserved.", error.Message);
            }
            return null;
        }
    }

    private sealed record Shape(Type EntityType, IReadOnlyList<LambdaExpression> Filters, IReadOnlyList<(string Property, bool Descending)> Order,
        IReadOnlyList<MethodCallExpression> Modifiers, string Subjects, string Permission, int Take, int Skip, bool IgnoreQueryFilters);

    private static Shape ReadShape(Expression source, Expression take, DbContext context)
    {
        if (!ReadCapturedValue(take, out var takeValue) || takeValue is not int count)
            throw Unsupported("the page size is not a captured integer");
        var calls = new List<MethodCallExpression>();
        Expression root = source;
        while (root is MethodCallExpression call)
        {
            if (call.Arguments.Count == 0)
                throw Unsupported("the source is not an ordinary entity query");
            calls.Add(call);
            root = call.Arguments[0];
        }
        if (root.GetType() != typeof(EntityQueryRootExpression) || root is not EntityQueryRootExpression entityRoot)
            throw Unsupported("the source is not a single ordinary mapped entity table");

        var filters = new List<LambdaExpression>();
        var order = new List<(string Property, bool Descending)>();
        var modifiers = new List<MethodCallExpression>();
        string? subjects = null, permission = null;
        var found = 0;
        var skip = 0;
        var sawSkip = false;
        var ignoreFilters = false;
        foreach (var call in calls.AsEnumerable().Reverse())
        {
            if (call.Method.DeclaringType == typeof(Queryable))
            {
                switch (call.Method.Name)
                {
                    case nameof(Queryable.Where):
                    {
                        if (sawSkip)
                            throw Unsupported("a condition after Skip cannot move before that limit");
                        var lambda = Lambda(call.Arguments[1]);
                        if (lambda.Parameters.Count != 1)
                            throw Unsupported("indexed predicates are not supported");
                        var remaining = new List<Expression>();
                        foreach (var term in Conjuncts(lambda.Body))
                        {
                            if (TryAuthorization(term, lambda.Parameters[0], context, out var ids, out var requestedPermission))
                            {
                                found++;
                                subjects = ids;
                                permission = requestedPermission;
                            }
                            else
                            {
                                if (HasAuthorizationCall(term))
                                    throw Unsupported("authorization must be one positive conjunct, without OR, NOT, or another authorization predicate");
                                remaining.Add(term);
                            }
                        }
                        if (remaining.Count > 0)
                            filters.Add(Expression.Lambda(remaining.Aggregate(Expression.AndAlso), lambda.Parameters));
                        break;
                    }
                    case nameof(Queryable.OrderBy):
                    case nameof(Queryable.OrderByDescending):
                    case nameof(Queryable.ThenBy):
                    case nameof(Queryable.ThenByDescending):
                    {
                        if (sawSkip)
                            throw Unsupported("an order after Skip cannot move before that limit");
                        if (call.Arguments.Count != 2)
                            throw Unsupported("custom ordering comparers are not supported");
                        var lambda = Lambda(call.Arguments[1]);
                        var property = ReadProperty(lambda.Body, lambda.Parameters[0]);
                        if (property == null)
                            throw Unsupported("the order must use mapped columns");
                        if (call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending))
                            order.Clear();
                        order.Add((property, call.Method.Name.EndsWith("Descending", StringComparison.Ordinal)));
                        break;
                    }
                    case nameof(Queryable.Skip):
                        if (sawSkip || !ReadCapturedValue(call.Arguments[1], out var skipValue) || skipValue is not int skipCount)
                            throw Unsupported("the page must have at most one captured integer Skip");
                        skip = Math.Max(0, skipCount);
                        sawSkip = true;
                        break;
                    default:
                        throw Unsupported($"{call.Method.Name} before the page is not supported");
                }
            }
            else if (IsModifier(call))
            {
                if (call.Method.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters) && call.Arguments.Count != 1)
                    throw Unsupported("selectively ignored named query filters require the original EF query");
                ignoreFilters |= call.Method.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters);
                modifiers.Add(call);
            }
            else
            {
                throw Unsupported($"{call.Method.Name} before the page is not supported");
            }
        }
        if (found != 1 || subjects == null || permission == null)
            throw Unsupported("the page must contain exactly one recognized authorization predicate");
        if ((long)Math.Max(0, count) + skip > int.MaxValue)
            throw Unsupported("the combined Skip and Take exceed the database page-count parameter");
        return new(entityRoot.EntityType.ClrType, filters, order, modifiers, subjects, permission, Math.Max(0, count), skip, ignoreFilters);
    }

    private static bool IsModifier(MethodCallExpression call)
    {
        if (call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            return call.Method.Name is nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
                or nameof(EntityFrameworkQueryableExtensions.AsTracking)
                or nameof(EntityFrameworkQueryableExtensions.AsNoTrackingWithIdentityResolution)
                or nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters)
                or nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes)
                or nameof(EntityFrameworkQueryableExtensions.TagWith)
                or nameof(EntityFrameworkQueryableExtensions.TagWithCallSite);
        return call.Method.DeclaringType == typeof(RelationalQueryableExtensions)
            && call.Method.Name is nameof(RelationalQueryableExtensions.AsSplitQuery) or nameof(RelationalQueryableExtensions.AsSingleQuery);
    }

    private static bool TryAuthorization(Expression expression, ParameterExpression entity, DbContext context, out string? subjects, out string? permission)
    {
        subjects = permission = null;
        if (expression is not MethodCallExpression { Method.Name: nameof(Queryable.Any), Arguments.Count: 1 } any
            || any.Method.DeclaringType != typeof(Queryable)
            || any.Arguments[0] is not MethodCallExpression tvf
            || tvf.Method.Name != nameof(ISqlOSFgaDbContext.IsResourceAccessible)
            || tvf.Arguments.Count != 3
            || tvf.Object is not ConstantExpression owner || !ReferenceEquals(owner.Value, context)
            || ReadProperty(tvf.Arguments[0], entity) != nameof(IHasResourceId.ResourceId))
            return false;
        var expected = context.GetType().GetMethod(nameof(ISqlOSFgaDbContext.IsResourceAccessible), [typeof(string), typeof(string), typeof(string)]);
        if (expected != tvf.Method || !ReadCapturedValue(tvf.Arguments[1], out var ids) || ids is not string idsText
            || !ReadCapturedValue(tvf.Arguments[2], out var requested) || requested is not string requestedText)
            return false;
        subjects = idsText;
        permission = requestedText;
        return true;
    }

    private static Expression BuildPage<T>(DbContext context, SqlOSFgaScopeEntity entity, Shape shape) where T : class
    {
        if (entity.EntityType.GetNavigations().Any(n => n.IsEagerLoaded)
            && !shape.Modifiers.Any(m => m.Method.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes)))
            throw Unsupported("automatic navigation includes require the original query's related-entity filters");
        var postgreSql = context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        var order = MatchOrder(entity, shape.Order);
        var previous = Suppressed.Value;
        Suppressed.Value = true;
        try
        {
            IQueryable<T> filtered = context.Set<T>();
            if (shape.IgnoreQueryFilters)
                filtered = filtered.IgnoreQueryFilters();
            foreach (var filter in shape.Filters)
                filtered = filtered.Where((Expression<Func<T, bool>>)filter);
            var row = Expression.Parameter(typeof(T), "entity");
            var property = entity.EntityType.FindProperty(entity.Key.PropertyName)!;
            var selector = Expression.Lambda(Expression.Call(PropertyMethod.MakeGenericMethod(property.ClrType), row, Expression.Constant(property.Name)), row);
            var select = QueryableMethod(nameof(Queryable.Select)).MakeGenericMethod(typeof(T), property.ClrType);
            var projected = filtered.Provider.CreateQuery(Expression.Call(select, filtered.Expression, Expression.Quote(selector)));
            using var command = projected.CreateDbCommand();
            var (alias, predicate) = ParseFilterSql(command.CommandText);
            var projectionAlias = Quote(postgreSql, "__sqlos_scope");
            if (Tokenize(predicate ?? "").Any(t => t.Identifier && t.Value == "__sqlos_scope"))
                throw Unsupported("the translated predicate uses the reserved native scope alias");
            var (rewrittenPredicate, applicationJoin) = RewritePredicate(predicate, alias, projectionAlias, entity.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal));
            var parameters = new List<object>();
            foreach (DbParameter parameter in command.Parameters)
            {
                if (new[] { SqlOSFgaNativePageSql.SubjectsParameter, SqlOSFgaNativePageSql.PermissionParameter, SqlOSFgaNativePageSql.TakeParameter }
                    .Any(name => string.Equals(name.TrimStart('@'), parameter.ParameterName.TrimStart('@'), StringComparison.OrdinalIgnoreCase)))
                    throw Unsupported("an application parameter uses a reserved native page parameter name");
                if (parameter is not ICloneable cloneable || cloneable.Clone() is not DbParameter clone)
                    throw Unsupported("the database provider cannot clone a translated predicate parameter");
                parameters.Add(clone);
            }
            AddParameter(command, parameters, SqlOSFgaNativePageSql.SubjectsParameter, shape.Subjects, DbType.String, postgreSql ? 0 : -1);
            AddParameter(command, parameters, SqlOSFgaNativePageSql.PermissionParameter, shape.Permission, DbType.String, 450);
            AddParameter(command, parameters, SqlOSFgaNativePageSql.TakeParameter, shape.Take == 0 ? 0 : shape.Take + shape.Skip, DbType.Int32);
            var options = SqlOSFgaNativeOptions.FromModel(context.Model);
            var sql = SqlOSFgaNativePageSql.Build(postgreSql, options, new()
            {
                ProjectionTableSql = Quote(postgreSql, options.Schema) + "." + Quote(postgreSql, entity.ProjectionTable),
                ApplicationTableSql = Quote(postgreSql, entity.SourceSchema) + "." + Quote(postgreSql, entity.SourceTable),
                KeyColumn = entity.Key.Name,
                ResourceIdColumn = entity.ResourceId.Name,
                Order = order.Select(o => new SqlOSFgaNativePageOrder(o.Column.Name, o.Descending)).ToArray(),
                ProjectionAliasSql = projectionAlias,
                ApplicationAliasSql = alias,
                PredicateSql = rewrittenPredicate,
                NeedsApplicationJoin = applicationJoin,
            });
            // Entity query filters are already inside the local streams. EF may apply them again outside,
            // but must remain free to filter related entities in outer projections and predicates. Blanket
            // IgnoreQueryFilters here would remove those protections. Only the application's own original
            // IgnoreQueryFilters modifier is replayed below.
            // FromSqlRaw treats its SQL as a .NET format string even when every argument is an already
            // named DbParameter. EF-translated literals/identifiers may legitimately contain braces; all
            // placeholders here are named SQL parameters, so preserve every brace as literal SQL text.
            var formatSql = sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
            IQueryable<T> page = context.Set<T>().FromSqlRaw(formatSql, parameters.ToArray());
            Expression result = page.Expression;
            foreach (var modifier in shape.Modifiers)
            {
                var arguments = modifier.Arguments.ToArray();
                arguments[0] = result;
                result = modifier.Update(modifier.Object, arguments);
            }
            for (var i = 0; i < order.Count; i++)
            {
                var term = order[i];
                var mapped = entity.EntityType.FindProperty(term.Column.PropertyName)!;
                var parameter = Expression.Parameter(typeof(T), "entity");
                var key = Expression.Lambda(Expression.Call(PropertyMethod.MakeGenericMethod(mapped.ClrType), parameter, Expression.Constant(mapped.Name)), parameter);
                var name = i == 0
                    ? term.Descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy)
                    : term.Descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy);
                result = Expression.Call(QueryableMethod(name).MakeGenericMethod(typeof(T), mapped.ClrType), result, Expression.Quote(key));
            }
            if (shape.Skip > 0)
                result = Expression.Call(QueryableMethod(nameof(Queryable.Skip)).MakeGenericMethod(typeof(T)), result, Expression.Constant(shape.Skip));
            // The SQL already selected at most Skip+Take. Keeping Take after Skip both expresses the original
            // boundary for EF composition and preserves Take(0), without loading any intermediate rows.
            result = Expression.Call(QueryableMethod(nameof(Queryable.Take)).MakeGenericMethod(typeof(T)), result, Expression.Constant(shape.Take));
            return result;
        }
        finally
        {
            Suppressed.Value = previous;
        }
    }

    private static IReadOnlyList<SqlOSFgaScopeOrderColumn> MatchOrder(SqlOSFgaScopeEntity entity, IReadOnlyList<(string Property, bool Descending)> requested)
    {
        var wanted = new List<(string Property, bool Descending)>();
        foreach (var item in requested)
        {
            wanted.Add(item);
            if (item.Property == entity.Key.PropertyName)
                break;
        }
        if (wanted.Count == 0)
            wanted.Add((entity.Key.PropertyName, false));
        foreach (var index in entity.Orders)
        {
            var columns = new List<SqlOSFgaScopeOrderColumn>();
            foreach (var column in index.Columns)
            {
                columns.Add(column);
                if (column.Column.Name == entity.Key.Name)
                    break;
            }
            foreach (var reverse in new[] { false, true })
            {
                if (columns.Count != wanted.Count && !(columns.Count == wanted.Count + 1 && columns[^1].Column.Name == entity.Key.Name))
                    continue;
                if (wanted.Select((term, i) => columns[i].Column.PropertyName == term.Property && (columns[i].Descending ^ reverse) == term.Descending).All(match => match))
                    return columns.Select(c => c with { Descending = c.Descending ^ reverse }).ToArray();
            }
        }
        throw Unsupported("no maintained declared index covers the requested order and its unique-key tie breaker");
    }

    private static void AddParameter(DbCommand command, List<object> parameters, string name, object value, DbType type, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size != 0)
            parameter.Size = size;
        parameters.Add(parameter);
    }

    private static MethodInfo QueryableMethod(string name) => typeof(Queryable).GetMethods().Single(m => m.Name == name && m.GetParameters().Length == 2
        && (name is not (nameof(Queryable.Skip) or nameof(Queryable.Take)) || m.GetParameters()[1].ParameterType == typeof(int))
        && (name != nameof(Queryable.Select) || m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2));

    private static LambdaExpression Lambda(Expression expression) => expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda } ? lambda
        : expression as LambdaExpression ?? throw Unsupported("a query operator has no ordinary expression lambda");

    private static IEnumerable<Expression> Conjuncts(Expression expression)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } binary)
        {
            foreach (var left in Conjuncts(binary.Left)) yield return left;
            foreach (var right in Conjuncts(binary.Right)) yield return right;
        }
        else yield return expression;
    }

    private static string? ReadProperty(Expression expression, ParameterExpression entity)
    {
        if (expression is MemberExpression { Expression: var owner, Member: PropertyInfo property } && owner == entity)
            return property.Name;
        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(EF) && call.Method.Name == nameof(EF.Property)
            && call.Arguments.Count == 2 && call.Arguments[0] == entity && call.Arguments[1] is ConstantExpression { Value: string name })
            return name;
        return null;
    }

    private static bool ReadCapturedValue(Expression expression, out object? value)
    {
        if (expression is ConstantExpression constant) { value = constant.Value; return true; }
        if (expression is MemberExpression { Member: FieldInfo field, Expression: { } owner } && ReadCapturedValue(owner, out var instance))
        {
            value = field.GetValue(instance);
            return true;
        }
        value = null;
        return false;
    }

    private sealed class AuthorizationFinder : ExpressionVisitor
    {
        internal bool Found { get; private set; }
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(ISqlOSFgaDbContext.IsResourceAccessible)) Found = true;
            return base.VisitMethodCall(node);
        }
    }

    private sealed class IgnoredFilterFinder : ExpressionVisitor
    {
        internal bool Found { get; private set; }
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && node.Method.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters)) Found = true;
            return base.VisitMethodCall(node);
        }
    }

    private static bool HasAuthorizationCall(Expression expression) { var finder = new AuthorizationFinder(); finder.Visit(expression); return finder.Found; }
    private static InvalidOperationException Unsupported(string reason) => new(reason);
    private static string Quote(bool postgreSql, string name) => postgreSql ? "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Accept only a single physical FROM table; joins and other top-level query clauses keep EF's original query.</summary>
    internal static (string AliasSql, string? PredicateSql) ParseFilterSql(string sql)
    {
        var tokens = Tokenize(sql);
        var from = tokens.FindIndex(t => t.Depth == 0 && t.Keyword("FROM"));
        var where = tokens.FindIndex(t => t.Depth == 0 && t.Keyword("WHERE"));
        if (from < 0 || (where >= 0 && where < from)) throw Unsupported("the translated filter is not a single-table SELECT");
        var source = tokens.Skip(from + 1).Take((where < 0 ? tokens.Count : where) - from - 1).ToArray();
        var asIndex = Array.FindIndex(source, t => t.Keyword("AS"));
        if (asIndex < 1 || asIndex + 2 != source.Length || !source[^1].Identifier)
            throw Unsupported("the translated filter has a joined or derived FROM source");
        for (var i = 0; i < asIndex; i++)
            if ((i % 2 == 0) != source[i].Identifier || (i % 2 == 1 && source[i].Value != "."))
                throw Unsupported("the translated filter has a joined or derived FROM source");
        if (where >= 0 && tokens.Skip(where + 1).Any(t => t.Depth == 0 && (t.Keyword("ORDER") || t.Keyword("GROUP") || t.Keyword("HAVING") || t.Keyword("LIMIT") || t.Keyword("OFFSET") || t.Keyword("UNION") || t.Keyword("EXCEPT") || t.Keyword("INTERSECT"))))
            throw Unsupported("the translated filter has an unsupported top-level query clause");
        var alias = source[^1];
        return (sql.Substring(alias.Start, alias.Length), where < 0 ? null : sql[(tokens[where].Start + tokens[where].Length)..].Trim());
    }

    /// <summary>Replace only identifier tokens, never text inside literals/comments or a subquery's aliases.</summary>
    internal static (string? Sql, bool NeedsApplicationJoin) RewritePredicate(string? sql, string originalAlias, string projectionAlias, IReadOnlySet<string> copiedColumns)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (sql, false);
        var tokens = Tokenize(sql);
        if (tokens.Any(t => t.Keyword("SELECT"))) return (sql, true);
        var aliasToken = Tokenize(originalAlias).Single();
        var edits = new List<(int Start, int Length)>();
        var needsJoin = false;
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (!tokens[i].Identifier || tokens[i].Value != aliasToken.Value || tokens[i + 1].Value != "." || !tokens[i + 2].Identifier) continue;
            if (copiedColumns.Contains(tokens[i + 2].Value)) edits.Add((tokens[i].Start, tokens[i].Length));
            else needsJoin = true;
            i += 2;
        }
        var builder = new StringBuilder(sql);
        foreach (var edit in edits.AsEnumerable().Reverse()) builder.Remove(edit.Start, edit.Length).Insert(edit.Start, projectionAlias);
        return (builder.ToString(), needsJoin);
    }

    private sealed record SqlToken(int Start, int Length, string Value, bool Identifier, bool Quoted, int Depth)
    {
        internal bool Keyword(string keyword) => Identifier && !Quoted && string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static List<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var depth = 0;
        for (var i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-') { var end = sql.IndexOf('\n', i + 2); i = end < 0 ? sql.Length : end + 1; continue; }
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2; var nested = 1;
                while (i < sql.Length && nested > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { nested++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { nested--; i += 2; }
                    else i++;
                }
                continue;
            }
            var start = i;
            if (sql[i] == '\'')
            {
                var escapes = i > 0 && (sql[i - 1] is 'E' or 'e') && (i < 2 || !char.IsLetterOrDigit(sql[i - 2]));
                i++;
                while (i < sql.Length)
                {
                    if (escapes && sql[i] == '\\') { i = Math.Min(sql.Length, i + 2); continue; }
                    if (sql[i++] != '\'') continue;
                    if (i < sql.Length && sql[i] == '\'') { i++; continue; }
                    break;
                }
                tokens.Add(new(start, i - start, sql[start..i], false, false, depth)); continue;
            }
            if (sql[i] is '"' or '[')
            {
                var close = sql[i] == '[' ? ']' : '"'; i++; var value = new StringBuilder();
                while (i < sql.Length)
                {
                    var c = sql[i++];
                    if (c != close) { value.Append(c); continue; }
                    if (i < sql.Length && sql[i] == close) { value.Append(close); i++; continue; }
                    break;
                }
                tokens.Add(new(start, i - start, value.ToString(), true, true, depth)); continue;
            }
            if (sql[i] == '$')
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                if (end < sql.Length && sql[end] == '$')
                {
                    var delimiter = sql[i..(end + 1)]; var close = sql.IndexOf(delimiter, end + 1, StringComparison.Ordinal);
                    if (close >= 0) { i = close + delimiter.Length; tokens.Add(new(start, i - start, sql[start..i], false, false, depth)); continue; }
                }
            }
            if (char.IsLetter(sql[i]) || sql[i] == '_')
            {
                i++; while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                tokens.Add(new(start, i - start, sql[start..i], true, false, depth)); continue;
            }
            if (sql[i] == ')') depth--;
            tokens.Add(new(i, 1, sql[i].ToString(), false, false, depth));
            if (sql[i] == '(') depth++;
            i++;
        }
        return tokens;
    }
}
