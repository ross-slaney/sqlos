using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Indexing;

namespace SqlOS.Fga;

/// <summary>SqlOS query/model integration for a DbContext constructed outside AddSqlOS or SqlOSDbContext.</summary>
public static class SqlOSFgaDbContextOptionsExtensions
{
    /// <summary>
    /// Enables database-native authorized page translation and automatic SQL Server trigger compatibility.
    /// AddSqlOS and SqlOSDbContext apply this automatically. Standalone FGA contexts apply it when building
    /// their options, before running the normal SqlOS FGA function initializer.
    /// </summary>
    public static DbContextOptionsBuilder UseSqlOSFga(this DbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        SqlOSFgaNativeOptions.Configure(builder);
        return builder;
    }

    /// <inheritdoc cref="UseSqlOSFga(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseSqlOSFga<TContext>(this DbContextOptionsBuilder<TContext> builder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseSqlOSFga((DbContextOptionsBuilder)builder);
}
