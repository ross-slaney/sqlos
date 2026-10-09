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
/// In SQL Server fallback queries, expose the private scope projection through a composable SQL join.
/// EF still translates the whole application query: Where, joins, aggregates and ordering stay in SQL.
/// Ordinary queries without an authorization filter do not pass through this visitor.
/// </summary>
internal sealed class SqlOSFgaScopeIndexQueries(DbContext context, SqlOSFgaOptions options) : ExpressionVisitor
{
    private readonly IReadOnlyList<SqlOSFgaScopeTable> _tables = SqlOSFgaScopeColumns.Tables(context.Model);

    public static Expression Rewrite(Expression query, DbContext context, SqlOSFgaOptions options)
        => context.Database.IsSqlServer() ? new SqlOSFgaScopeIndexQueries(context, options).Visit(query) : query;

    protected override Expression VisitExtension(Expression node)
    {
        // A user-supplied FromSql query has its own semantics and must not be replaced with a table read.
        if (node is EntityQueryRootExpression root && root.GetType() == typeof(EntityQueryRootExpression))
        {
            var table = _tables.FirstOrDefault(t => t.Table == root.EntityType.GetTableName()
                && t.Schema == (root.EntityType.GetSchema() ?? context.Model.GetDefaultSchema()));
            if (table is not null)
            {
                var sql = SqlServerDatabaseProvider.ScopeIndexQuery(options, table);
                var arguments = Expression.Constant(Array.Empty<object>());
                return root.QueryProvider is { } provider
                    ? new FromSqlQueryRootExpression(provider, root.EntityType, sql, arguments)
                    : new FromSqlQueryRootExpression(root.EntityType, sql, arguments);
            }
        }
        return base.VisitExtension(node);
    }
}

#pragma warning restore EF1001
