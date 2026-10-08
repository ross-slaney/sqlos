using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga;

/// <summary>
/// Turns a context into one whose authorized pages SqlOS finds itself. A query of the form
/// <c>Where(filter)</c> (the filter from <c>BuildFilterAsync</c>) with an order and a <c>Take</c> no longer
/// runs as one SQL statement the optimizer plans: SqlOS walks the caller's granted branches of the resource
/// tree and reads about a page's worth of rows, however many rows the caller may see and whatever the table's
/// size. Every other query (a count, a join, a filter without <c>Take</c>) runs the filter as a predicate, as
/// before. <c>AddSqlOS</c> applies this to the registered context; a context built by hand calls it on its
/// options builder.
/// </summary>
public static class SqlOSFgaDbContextOptionsExtensions
{
    /// <summary>
    /// Applies SqlOS's query execution to the context: the walk for authorized pages, and the warning for
    /// filtered queries sorted by an order no declared index covers.
    /// </summary>
    /// <param name="builder">The context's options builder.</param>
    /// <param name="options">The FGA options the application registered (the schema, table names and root resource).</param>
    /// <param name="loggerFactory">Where the warnings go; the context's own logger factory when null.</param>
    public static DbContextOptionsBuilder UseSqlOSFga(this DbContextOptionsBuilder builder, SqlOSFgaOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new SqlOSFgaOptionsExtension(options, loggerFactory));
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

/// <summary>The FGA options and logger factory, carried on the context's options for SqlOS's query compiler.</summary>
internal sealed class SqlOSFgaOptionsExtension(SqlOSFgaOptions options, ILoggerFactory? loggerFactory) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public SqlOSFgaOptions Options { get; } = options;

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

        public override string LogFragment => "using SqlOS FGA pages ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["SqlOS:FgaPages"] = "1";
    }
}
