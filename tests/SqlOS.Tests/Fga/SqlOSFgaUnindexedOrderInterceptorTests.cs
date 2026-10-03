using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The warning for a filtered page sorted by an order no declared index covers: such a page returns the
/// right rows but reads the caller's whole scope, so the interceptor names the entity, the order, and the
/// index to declare, once per query shape. Orders an index covers (a declared index mirrored per level, or
/// the key itself), queries without the filter, and orders the pass cannot read stay silent.
/// </summary>
[TestClass]
public class SqlOSFgaUnindexedOrderInterceptorTests
{
    [TestMethod]
    public void AFilteredPage_SortedByAnUndeclaredOrder_WarnsWithTheIndexToDeclare()
    {
        var (context, warnings) = Create<AUndeclared>();
        using var _ = context;

        context.Set<Item>().Where(Filter()).OrderBy(i => i.Name).ThenBy(i => i.Id).ToQueryString();

        warnings.Should().ContainSingle()
            .Which.Should().Contain("Item").And.Contain("Name, Id").And.Contain("(Name)");
    }

    [TestMethod]
    public void TheWarning_FiresOncePerQueryShape()
    {
        var (context, warnings) = Create<BOnce>();
        using var _ = context;

        context.Set<Item>().Where(Filter()).OrderBy(i => i.Name).ToQueryString();
        context.Set<Item>().Where(Filter()).OrderBy(i => i.Name).ToQueryString();

        warnings.Should().ContainSingle();
    }

    [TestMethod]
    public void AFilteredPage_SortedByADeclaredOrder_StaysSilent()
    {
        var (context, warnings) = Create<CDeclared>();
        using var _ = context;

        // The declared PlacedAt index is mirrored per level as (ancestor, PlacedAt, Id): the page is a seek.
        context.Set<Item>().Where(Filter()).OrderBy(i => i.PlacedAt).ThenBy(i => i.Id).ToQueryString();

        warnings.Should().BeEmpty();
    }

    [TestMethod]
    public void AFilteredPage_SortedByTheKey_StaysSilent()
    {
        var (context, warnings) = Create<DKey>();
        using var _ = context;

        context.Set<Item>().Where(Filter()).OrderBy(i => i.Id).ToQueryString();

        warnings.Should().BeEmpty();
    }

    [TestMethod]
    public void AnUnfilteredQuery_StaysSilent()
    {
        var (context, warnings) = Create<EUnfiltered>();
        using var _ = context;

        context.Set<Item>().OrderBy(i => i.Name).ToQueryString();

        warnings.Should().BeEmpty();
    }

    [TestMethod]
    public void AnOrderThatIsNotAPlainProperty_IsNotJudged()
    {
        var (context, warnings) = Create<FOpaque>();
        using var _ = context;

        context.Set<Item>().Where(Filter()).OrderBy(i => i.Name.Length).ToQueryString();

        warnings.Should().BeEmpty();
    }

    private static System.Linq.Expressions.Expression<Func<Item, bool>> Filter()
        => SqlOSFgaFilterBuilder.Build<Item>([new SqlOSFgaAccessRoot { ResourceSeq = 5, Depth = 1 }], "[\"u\"]", typeSeq: 7, levels: 11);

    // One context type per test: EF Core caches compiled queries, and the warning fires at compilation.
    private static (InterceptedDbContext<TMarker> Context, List<string> Warnings) Create<TMarker>()
    {
        var warnings = new List<string>();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var context = new InterceptedDbContext<TMarker>(new DbContextOptionsBuilder<InterceptedDbContext<TMarker>>()
            .UseSqlite(connection)
            .AddInterceptors(new SqlOSFgaUnindexedOrderInterceptor(new CapturingLoggerFactory(warnings)))
            .Options);
        return (context, warnings);
    }

    private sealed class AUndeclared;

    private sealed class BOnce;

    private sealed class CDeclared;

    private sealed class DKey;

    private sealed class EUnfiltered;

    private sealed class FOpaque;

    private sealed class InterceptedDbContext<TMarker>(DbContextOptions<InterceptedDbContext<TMarker>> options) : DbContext(options), ISqlOSFgaDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>(item =>
            {
                item.ToTable("Items");
                item.HasKey(i => i.Id);
                item.HasIndex(i => i.PlacedAt);
            });
            modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName, new SqlOSFgaOptions());
        }
    }

    private sealed class Item : IHasResourceId
    {
        public byte[]? FgaScope { get; private set; }

        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime PlacedAt { get; set; }
    }

    private sealed class CapturingLoggerFactory(List<string> warnings) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(warnings);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
