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
/// A declared order whose entry table does not exist yet (its migration has not been applied):
/// startup logs it, saves still succeed, and pages return the right rows by reading the table.
/// </summary>
[TestClass]
public class SqlOSFgaPageOrderMissingTableIntegrationTests
{
    private const string EntryTable = "SqlOSFgaPage_PagedNotes_Rank";

    [TestInitialize]
    public async Task CreateApplicationTableOnly()
    {
        await PageOrderFixture.EnsureSeededAsync();
        await PageOrderFixture.ExecuteAsync(TestDatabase.IsPostgreSql
            ? """CREATE TABLE IF NOT EXISTS "PagedNotes" ("Id" uuid NOT NULL PRIMARY KEY, "ResourceId" varchar(450) NOT NULL, "Rank" integer NOT NULL)"""
            : "IF OBJECT_ID(N'[dbo].[PagedNotes]', N'U') IS NULL CREATE TABLE [dbo].[PagedNotes] ([Id] uniqueidentifier NOT NULL PRIMARY KEY, [ResourceId] nvarchar(450) NOT NULL, [Rank] int NOT NULL)");
        await PageOrderFixture.ExecuteAsync(TestDatabase.IsPostgreSql
            ? $"DROP TABLE IF EXISTS {TestDatabase.Qualify(EntryTable)}"
            : $"IF OBJECT_ID(N'[dbo].[{EntryTable}]', N'U') IS NOT NULL DROP TABLE {TestDatabase.Qualify(EntryTable)}");
    }

    [TestMethod]
    public async Task MissingEntryTable_IsLoggedAtStartup_WritesSucceed_AndPagesReadTheTable()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = CreateContext(logs);

        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, logs.CreateLogger("startup"), CancellationToken.None);

        logs.Entries.Where(entry => entry.Level == LogLevel.Warning)
            .Should().ContainSingle()
            .Which.Message.Should().StartWith($"Page order PagedNote(Rank, Id): table {EntryTable} does not exist.");

        var notes = Enumerable.Range(0, 12)
            .Select(rank => new PagedNote
            {
                Id = Guid.NewGuid(),
                ResourceId = rank % 3 == 0 ? FgaTestDataSeeder.TestProjectResourceId : FgaTestDataSeeder.OtherAgencyResourceId,
                Rank = rank
            })
            .ToList();
        context.Notes.AddRange(notes);
        await context.SaveChangesAsync();

        try
        {
            var filter = await new SqlOSFgaAuthService(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance)
                .BuildFilterAsync<PagedNote>(FgaTestDataSeeder.AgencyMemberSubjectId, PageOrderFixture.ViewPermission);
            logs.Clear();

            var pages = new List<Guid>();
            string? cursor = null;
            do
            {
                var page = await context.Notes.AsNoTracking().OrderBy(note => note.Rank).ToPageAsync(filter, 2, cursor);
                pages.AddRange(page.Data.Select(note => note.Id));
                cursor = page.NextCursor;
            }
            while (cursor != null);

            pages.Should().Equal(notes.Where(note => note.Rank % 3 == 0).Select(note => note.Id));
            logs.Commands.Should().NotContain(command => command.Contains("fn_PageNodes", StringComparison.Ordinal));
            logs.Entries.Where(entry => entry.Category == "SqlOS.Fga" && entry.Level == LogLevel.Debug)
                .Should().OnlyContain(entry => entry.Message.Contains("(page order declared, table missing)", StringComparison.Ordinal));
        }
        finally
        {
            context.Notes.RemoveRange(notes);
            await context.SaveChangesAsync();
        }
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
                entity.HasPageOrder(note => note.Rank);
            });
    }

    private sealed class PagedNote : IHasResourceId
    {
        public Guid Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public int Rank { get; set; }
    }
}
