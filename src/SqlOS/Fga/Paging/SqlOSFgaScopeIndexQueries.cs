using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Paging;

/// <summary>Non-page filters read scope from the private index, never from the application model.</summary>
internal static class SqlOSFgaScopeIndexQueries
{
    private static readonly MethodInfo BuildMethod = typeof(SqlOSFgaScopeIndexQueries).GetMethod(nameof(Build), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static LambdaExpression Predicate(DbContext context, Type entityType, SqlOSFgaAccessToken token, IReadOnlyList<SqlOSFgaAccessRoot> roots)
        => (LambdaExpression)BuildMethod.MakeGenericMethod(entityType).Invoke(null, [context, token, roots])!;

    private static Expression<Func<T, bool>> Build<T>(DbContext context, SqlOSFgaAccessToken token, IReadOnlyList<SqlOSFgaAccessRoot> roots)
    {
        var entity = context.Model.FindEntityType(typeof(T))!;
        var table = SqlOSFgaScopeColumns.Tables(context.Model).Single(t => t.Table == entity.GetTableName()
            && t.Schema == (entity.GetSchema() ?? context.Model.GetDefaultSchema()));
        var provider = SqlOSDatabase.Resolve(context.Database);
        var scopeFilter = SqlOSFgaFilterBuilder.Build<SqlOSFgaScopeRow>(roots, token.SubjectIdsJson, token.TypeSeq, SqlOSFgaLineage.Levels(token.Options));
        var scopes = context.Set<SqlOSFgaScopeRow>(SqlOSFgaScopeRow.ModelName(entity.GetRootType().Name)).FromSqlRaw(provider.BuildScopeIndexQuerySql(token.Options, table)).Where(scopeFilter);
        var row = Expression.Parameter(typeof(T), "row");
        var scope = Expression.Parameter(typeof(SqlOSFgaScopeRow), "scope");
        Expression? matches = null;
        var keys = entity.FindPrimaryKey()!.Properties;
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var applicationKey = Expression.Call(typeof(EF), nameof(EF.Property), [key.ClrType], row, Expression.Constant(key.Name));
            var privateKey = Expression.Call(typeof(EF), nameof(EF.Property), [key.ClrType], scope, Expression.Constant(SqlOSFgaScopeRow.KeyName(i)));
            var equal = Expression.Equal(privateKey, applicationKey);
            matches = matches is null ? equal : Expression.AndAlso(matches, equal);
        }
        var sameRow = Expression.Lambda<Func<SqlOSFgaScopeRow, bool>>(matches!, scope);
        return Expression.Lambda<Func<T, bool>>(
            Expression.Call(typeof(Queryable), nameof(Queryable.Any), [typeof(SqlOSFgaScopeRow)], scopes.Expression, Expression.Quote(sameRow)), row);
    }
}
