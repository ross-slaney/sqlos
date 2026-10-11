using Microsoft.EntityFrameworkCore;
using SqlOS.Fga;

namespace SqlOS.Extensions;

public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Attaches SqlOS to the statements the context runs. On SQL Server, a statement that reads a caller's
    /// visible resources (the list filter <c>BuildFilterAsync</c> returns for a caller who sees few rows) is
    /// planned for that caller, with <c>OPTION (RECOMPILE)</c>; see <see cref="SqlOSFgaListStatementInterceptor"/>.
    /// <c>AddSqlOS</c> calls this for the context it registers, and <c>SqlOSDbContext</c> for itself. Call it on
    /// a context you configure by hand; a filter built from a context without it throws on SQL Server.
    /// </summary>
    public static DbContextOptionsBuilder UseSqlOS(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.AddInterceptors(SqlOSFgaListStatementInterceptor.Instance);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseSqlOS(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseSqlOS<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        UseSqlOS((DbContextOptionsBuilder)optionsBuilder);
        return optionsBuilder;
    }
}
