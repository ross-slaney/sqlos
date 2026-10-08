using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga;

/// <summary>
/// Gives a context SqlOS's query execution, which is what evaluates a filter from <c>BuildFilterAsync</c>. A
/// query of the form <c>Where(filter)</c> with an order and a <c>Take</c> is a page: SqlOS walks the caller's
/// granted branches of the resource tree and reads about a page's worth of rows, however many rows the caller
/// may see and whatever the table's size, and reads no grant up front. Every other query over the filter (a
/// count, a join, a list without <c>Take</c>) runs as one statement with the predicate over the caller's
/// access roots, read when the query runs. <c>AddSqlOS</c> applies this to the registered context; a context
/// built by hand calls it on its options builder, and without it a query over the filter cannot run. The
/// filter itself carries the FGA options it was built under, so nothing here depends on them.
/// </summary>
public static class SqlOSFgaDbContextOptionsExtensions
{
    /// <summary>
    /// Applies SqlOS's query execution to the context: the evaluation of filters from <c>BuildFilterAsync</c>
    /// (the walk for pages, the predicate over the caller's roots for everything else), and the warning for
    /// filtered queries sorted by an order no declared index covers.
    /// </summary>
    /// <param name="builder">The context's options builder.</param>
    /// <param name="loggerFactory">Where the warnings go; the context's own logger factory when null.</param>
    public static DbContextOptionsBuilder<TContext> UseSqlOSFga<TContext>(this DbContextOptionsBuilder<TContext> builder, ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseSqlOSFga((DbContextOptionsBuilder)builder, loggerFactory);

    /// <inheritdoc cref="UseSqlOSFga{TContext}(DbContextOptionsBuilder{TContext}, ILoggerFactory?)"/>
    public static DbContextOptionsBuilder UseSqlOSFga(this DbContextOptionsBuilder builder, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new SqlOSFgaOptionsExtension(loggerFactory));
#pragma warning disable EF1001 // The query compiler is the one place a library can take over how a query runs.
        builder.ReplaceService<IQueryCompiler, SqlOSFgaQueryCompiler>();
#pragma warning restore EF1001
        if (loggerFactory is not null)
        {
            builder.AddInterceptors(new SqlOSFgaUnindexedOrderInterceptor(loggerFactory));
        }

        return builder;
    }
}

/// <summary>Marks a context as running SqlOS's query execution, and carries the logger factory for its warnings.</summary>
internal sealed class SqlOSFgaOptionsExtension(ILoggerFactory? loggerFactory) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public ILoggerFactory? LoggerFactory { get; } = loggerFactory;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(SqlOSFgaOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using SqlOS FGA query execution ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["SqlOS:Fga"] = "1";
    }
}
