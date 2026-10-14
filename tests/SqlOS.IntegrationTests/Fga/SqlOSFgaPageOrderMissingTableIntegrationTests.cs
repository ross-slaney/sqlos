using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// A declared order and a node index whose tables do not exist yet (their migration has not been
/// applied): startup logs them, saves still succeed, and pages return the right rows by reading the table.
/// </summary>
[TestClass]
public class SqlOSFgaPageOrderMissingTableIntegrationTests
{
    private const string EntryTable = "SqlOSFgaPage_PagedNotes_Rank";
    private const string NodeIndexTable = "SqlOSFgaPage_PagedNotes_Id";

    [TestInitialize]
    public async Task CreateApplicationTableOnly()
    {
        await PageOrderFixture.EnsureSeededAsync();
        foreach (var table in new[] { "PagedNotes", EntryTable, NodeIndexTable })
        {
            await PageOrderFixture.ExecuteAsync(TestDatabase.IsPostgreSql
                ? $"DROP TABLE IF EXISTS {TestDatabase.Qualify(table)}"
                : $"IF OBJECT_ID(N'[dbo].[{table}]', N'U') IS NOT NULL DROP TABLE {TestDatabase.Qualify(table)}");
        }

        await PageOrderFixture.ExecuteAsync(TestDatabase.IsPostgreSql
            ? """CREATE TABLE "PagedNotes" ("Id" uuid NOT NULL PRIMARY KEY, "ResourceId" varchar(450) NOT NULL, "Rank" integer NOT NULL, "Title" varchar(50) NOT NULL)"""
            : "CREATE TABLE [dbo].[PagedNotes] ([Id] uniqueidentifier NOT NULL PRIMARY KEY, [ResourceId] nvarchar(450) NOT NULL, [Rank] int NOT NULL, [Title] nvarchar(50) NOT NULL)");
    }

    [TestMethod]
    public async Task MissingEntryTables_AreLoggedAtStartup_WritesSucceed_AndPagesReadTheTable()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = CreateContext(logs);

        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, logs.CreateLogger("startup"), CancellationToken.None);

        logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)
            .Should().HaveCount(2)
            .And.Contain(message => message.StartsWith($"Page order PagedNote(Rank, Id): table {EntryTable} does not exist.", StringComparison.Ordinal))
            .And.Contain(message => message.StartsWith($"Node index PagedNote(Id): table {NodeIndexTable} does not exist.", StringComparison.Ordinal));

        var notes = Enumerable.Range(0, 12)
            .Select(rank => new PagedNote
            {
                Id = Guid.NewGuid(),
                ResourceId = rank % 3 == 0 ? FgaTestDataSeeder.TestProjectResourceId : FgaTestDataSeeder.OtherAgencyResourceId,
                Rank = rank,
                Title = $"note {11 - rank:00}"
            })
            .ToList();
        context.Notes.AddRange(notes);
        await context.SaveChangesAsync();

        try
        {
            logs.Clear();

            var visible = notes.Where(note => note.Rank % 3 == 0).ToList();

            (await WalkAsync(context.Notes.AsNoTracking().OrderBy(note => note.Rank))).Should().Equal(visible.Select(note => note.Id));
            logs.Entries.Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
                .Should().NotBeEmpty()
                .And.OnlyContain(entry => entry.Message.Contains("(page order declared, table missing, node index missing)", StringComparison.Ordinal));

            logs.Clear();
            (await WalkAsync(context.Notes.AsNoTracking().OrderBy(note => note.Title)))
                .Should().Equal(visible.OrderBy(note => note.Title, StringComparer.Ordinal).Select(note => note.Id));
            logs.Entries.Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
                .Should().NotBeEmpty()
                .And.OnlyContain(entry => entry.Message.Contains("(page order not declared, node index missing)", StringComparison.Ordinal));
            logs.Commands.Should().NotContain(command => command.Contains("fn_PageNodes", StringComparison.Ordinal));
        }
        finally
        {
            context.Notes.RemoveRange(notes);
            await context.SaveChangesAsync();
        }
    }

    private static async Task<List<Guid>> WalkAsync(IOrderedQueryable<PagedNote> ordered)
    {
        var pages = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await ordered.ToPageAsync(FgaTestDataSeeder.AgencyMemberSubjectId, PageOrderFixture.ViewPermission, 2, cursor);
            pages.AddRange(page.Data.Select(note => note.Id));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        return pages;
    }

    private static MissingEntryTableTestDbContext CreateContext(ILoggerFactory loggerFactory)
        => new(new DbContextOptionsBuilder<MissingEntryTableTestDbContext>()
            .UseTestProvider(AspireFixture.SqlConnectionString)
            .UseLoggerFactory(loggerFactory)
            .EnableSensitiveDataLogging()
            .Options);

    private sealed class MissingEntryTableTestDbContext(DbContextOptions<MissingEntryTableTestDbContext> options)
        : SqlOSDbContext<MissingEntryTableTestDbContext>(options)
    {
        public DbSet<PagedNote> Notes => Set<PagedNote>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<PagedNote>(entity =>
            {
                entity.ToTable("PagedNotes");
                entity.HasKey(note => note.Id);
                entity.Property(note => note.ResourceId).HasMaxLength(450);
                entity.Property(note => note.Title).HasMaxLength(50);
                entity.HasPageOrder(note => note.Rank);
            });
    }

    private sealed class PagedNote : IHasResourceId
    {
        public Guid Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public int Rank { get; set; }
        public string Title { get; set; } = string.Empty;
    }
}
