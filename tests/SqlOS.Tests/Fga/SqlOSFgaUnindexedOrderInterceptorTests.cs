using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The warning for a filtered page sorted by an order no index covers: the page returns the right rows, but a
/// caller who sees most of the table has every row checked and sorted, so the interceptor names the entity,
/// the order and the index to declare, once per query shape. Orders an index covers (a declared index, or the
/// key), queries without the filter, and orders the pass cannot read stay silent.
/// </summary>
[TestClass]
public class SqlOSFgaUnindexedOrderInterceptorTests
{
    [TestMethod]
    public void AFilteredPage_SortedByAnUndeclaredOrder_WarnsWithTheIndexToDeclare()
    {
        var (context, warnings) = Create<AUndeclared>();
        using var _ = context;

        context.Set<Item>().Where(ListFilter()).OrderBy(i => i.Name).ThenBy(i => i.Id).ToQueryString();

        warnings.Should().ContainSingle()
            .Which.Should().Contain("Item").And.Contain("(Name, Id)").And.Contain("HasIndex(Name)");
    }

    [TestMethod]
    public void TheRowCheckFilter_IsRecognizedToo_AndTheWarningFiresOncePerQueryShape()
    {
        var (context, warnings) = Create<BOnce>();
        using var _ = context;

        context.Set<Item>().Where(RowCheckFilter()).OrderBy(i => i.Name).ToQueryString();
        context.Set<Item>().Where(RowCheckFilter()).OrderBy(i => i.Name).ToQueryString();

        warnings.Should().ContainSingle();
    }

    [TestMethod]
    public void AFilteredPage_SortedByADeclaredOrder_StaysSilent()
    {
        var (context, warnings) = Create<CDeclared>();
        using var _ = context;

        // The declared PlacedAt index serves the page; the trailing key is read from the index's row locator.
        context.Set<Item>().Where(ListFilter()).OrderBy(i => i.PlacedAt).ThenBy(i => i.Id).ToQueryString();

        warnings.Should().BeEmpty();
    }

    [TestMethod]
    public void AFilteredPage_SortedByTheKey_StaysSilent()
    {
        var (context, warnings) = Create<DKey>();
        using var _ = context;

        context.Set<Item>().Where(ListFilter()).OrderBy(i => i.Id).ToQueryString();

        warnings.Should().BeEmpty();
    }

    [TestMethod]
    public void AnUnfilteredQuery_StaysSilent()
    {
        var (context, warnings) = Create<EUnfiltered>();
        using var _ = context;

        context.Set<Item>().Where(i => i.Name != "").OrderBy(i => i.Name).ToQueryString();

        warnings.Should().BeEmpty();
    }

    [TestMethod]
    public void AnOrderThatIsNotAPlainProperty_IsNotJudged()
    {
        var (context, warnings) = Create<FOpaque>();
        using var _ = context;

        context.Set<Item>().Where(ListFilter()).OrderBy(i => i.Name.Length).ToQueryString();

        warnings.Should().BeEmpty();
    }

    // The filters as BuildFilterAsync builds them.
    private static Expression<Func<Item, bool>> ListFilter()
        => item => SqlOSFgaFunctions.VisibleSet("[\"u\"]", "perm", null).Any(v => v.ResourceId == item.ResourceId);

    private static Expression<Func<Item, bool>> RowCheckFilter()
        => item => SqlOSFgaFunctions.CheckRow(item.ResourceId, "[\"u\"]", "perm").Any();

    // One context type per test: EF Core caches compiled queries, and the warning fires at compilation.
    private static (InterceptedDbContext<TMarker> Context, List<string> Warnings) Create<TMarker>()
    {
        var warnings = new List<string>();
        var context = new InterceptedDbContext<TMarker>(new DbContextOptionsBuilder<InterceptedDbContext<TMarker>>()
            .UseSqlServer("Server=.;Database=SqlOS_Orders;Trusted_Connection=True;TrustServerCertificate=True")
            .UseLoggerFactory(new CapturingLoggerFactory(warnings))
            .UseSqlOS()
            .Options);
        return (context, warnings);
    }

    private sealed class AUndeclared;

    private sealed class BOnce;

    private sealed class CDeclared;

    private sealed class DKey;

    private sealed class EUnfiltered;

    private sealed class FOpaque;

    private sealed class InterceptedDbContext<TMarker>(DbContextOptions<InterceptedDbContext<TMarker>> options) : DbContext(options)
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
