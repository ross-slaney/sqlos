using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Finds the context behind a query. Each <c>SqlOSDbContext&lt;TContext&gt;</c> registers itself
/// under its query provider when it is constructed, and the queries of its DbSets carry that
/// provider. A pooled context keeps its provider across leases.
/// </summary>
internal static class SqlOSFgaQueryContexts
{
    private static readonly ConditionalWeakTable<IAsyncQueryProvider, DbContext> Contexts = new();

    public static void Register(DbContext context)
        => Contexts.AddOrUpdate(context.GetService<IAsyncQueryProvider>(), context);

    public static DbContext? Find(IQueryProvider provider)
        => provider is IAsyncQueryProvider queryProvider && Contexts.TryGetValue(queryProvider, out var context)
            ? context
            : null;
}
