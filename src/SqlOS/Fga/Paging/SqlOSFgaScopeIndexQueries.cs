using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using SqlOS.Database;
using SqlOS.Fga.Configuration;

namespace SqlOS.Fga.Paging;

#pragma warning disable EF1001 // Same EF query-root integration boundary as SqlOSFgaQueryCompiler.

/// <summary>
/// In a planned statement (a filtered query that is not a page), read each protected table through its
/// projection: the scope from the projection, whose per-level indexes serve the authorization predicate,
/// every other column from the row. The same on both engines. EF Core still translates the whole query:
/// Where, joins, aggregates and ordering stay in SQL. Queries without an authorization filter never pass
/// through this visitor.
/// </summary>
internal sealed class SqlOSFgaScopeIndexQueries(DbContext context, SqlOSFgaOptions options, ISqlOSDatabaseProvider provider) : ExpressionVisitor
{
    private readonly IReadOnlyList<SqlOSFgaScopeTable> _tables = SqlOSFgaScopeColumns.Tables(context.Model);

    public static Expression Rewrite(Expression query, DbContext context, SqlOSFgaOptions options)
    {
        var providerName = context.Database.ProviderName;
        return SqlOSDatabase.IsSqlServer(providerName) || SqlOSDatabase.IsPostgreSql(providerName)
            ? new SqlOSFgaScopeIndexQueries(context, options, SqlOSDatabase.Resolve(providerName)).Visit(query)
            : query;
    }

    protected override Expression VisitExtension(Expression node)
    {
        // A user-supplied FromSql query has its own semantics and must not be replaced with a table read.
        if (node is EntityQueryRootExpression root && root.GetType() == typeof(EntityQueryRootExpression))
        {
            var table = _tables.FirstOrDefault(t => t.Table == root.EntityType.GetTableName()
                && t.Schema == (root.EntityType.GetSchema() ?? context.Model.GetDefaultSchema()));
            if (table is not null)
            {
                var sql = provider.BuildScopeIndexQuerySql(options, table);
                var arguments = Expression.Constant(Array.Empty<object>());
                return root.QueryProvider is { } queryProvider
                    ? new FromSqlQueryRootExpression(queryProvider, root.EntityType, sql, arguments)
                    : new FromSqlQueryRootExpression(root.EntityType, sql, arguments);
            }
        }
        return base.VisitExtension(node);
    }
}

#pragma warning restore EF1001
