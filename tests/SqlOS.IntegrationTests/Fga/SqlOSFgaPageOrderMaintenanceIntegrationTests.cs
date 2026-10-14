using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// Every write path that ends in SqlOSDbContext.SaveChanges leaves the page-order entries, node
/// indexes included, equal to what a rebuild from the application tables and the resource graph
/// would write.
/// </summary>
[TestClass]
public class SqlOSFgaPageOrderMaintenanceIntegrationTests
{
    [TestInitialize]
    public Task Seed() => PageOrderFixture.EnsureSeededAsync();

    [TestMethod]
    public async Task InsertUpdatesAndDelete_KeepEntriesEqualToARebuild()
    {
        await using var context = PageOrderFixture.CreateContext();
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestTeamResourceId, 401);

        context.Documents.Add(document);
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();
        (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().BeEquivalentTo(
            document.ResourceId,
            FgaTestDataSeeder.TestTeamResourceId,
            FgaTestDataSeeder.TestAgencyResourceId,
            "root");
        (await NodesOf(context, PageOrderTestDbContext.DocumentNodeIndex, document.Id)).Should().BeEquivalentTo(
            await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id),
            "the node index lists the row under the same nodes as every order");

        document.Rank = 99;
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();

        document.Title = "zz retitled";
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();

        context.Documents.Remove(document);
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();
        (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().BeEmpty();
        (await NodesOf(context, PageOrderTestDbContext.DocumentNodeIndex, document.Id)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task UpdateOfAColumnNoOrderUses_IssuesNoEntryStatement()
    {
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext(logs);
        var document = await context.Documents.SingleAsync(candidate => candidate.ResourceId == "page_doc_010");
        logs.Clear();

        document.Body = $"edited {Guid.NewGuid():N}";
        await context.SaveChangesAsync();

        logs.Commands.Should().NotBeEmpty();
        logs.Commands.Should().NotContain(command => command.Contains("SqlOSFgaPage_", StringComparison.Ordinal));
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task IdentityKeyRows_GetEntries_AndFollowAResourceIdChange()
    {
        await using var context = PageOrderFixture.CreateContext();
        var counter = new PagedCounter { ResourceId = FgaTestDataSeeder.TestProjectResourceId, Rank = 5 };

        context.Counters.Add(counter);
        await context.SaveChangesAsync();

        counter.Id.Should().BePositive();
        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEquivalentTo(
            FgaTestDataSeeder.TestProjectResourceId,
            FgaTestDataSeeder.TestAgencyResourceId,
            "root");

        counter.ResourceId = FgaTestDataSeeder.OtherAgencyResourceId;
        await context.SaveChangesAsync();

        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEquivalentTo(
            FgaTestDataSeeder.OtherAgencyResourceId,
            "root");
        await AssertEntriesMatchRebuildAsync();

        context.Counters.Remove(counter);
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task ResourceMovedThroughTheEntity_RewritesItsEntries()
    {
        await using var context = PageOrderFixture.CreateContext();
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestTeamResourceId, 402);
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        document.FolderResourceId = FgaTestDataSeeder.OtherAgencyResourceId;
        await context.SaveChangesAsync();

        (await NodesOf(context, PageOrderTestDbContext.TitleEntries, document.Id)).Should().BeEquivalentTo(
            document.ResourceId,
            FgaTestDataSeeder.OtherAgencyResourceId,
            "root");
        await AssertEntriesMatchRebuildAsync();

        context.Documents.Remove(document);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ResourceMovedThroughTheErgonomicsHelpers_RewritesItsSubtree()
    {
        await using var context = PageOrderFixture.CreateContext();
        var folder = NewResourceId("page_folder");
        await context.CreateResourceWithIdAsync(folder, "team", "Movable folder", FgaTestDataSeeder.TestTeamResourceId);
        var first = PageOrderFixture.NewDocument(NewResourceId(), folder, 403);
        var second = PageOrderFixture.NewDocument(NewResourceId(), folder, 404);
        context.Documents.AddRange(first, second);
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();

        await context.ProvisionResourceWithIdAsync(folder, "team", "Movable folder", parentResourceId: FgaTestDataSeeder.OtherAgencyResourceId);
        await context.SaveChangesAsync();

        foreach (var document in new[] { first, second })
        {
            (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().BeEquivalentTo(
                document.ResourceId,
                folder,
                FgaTestDataSeeder.OtherAgencyResourceId,
                "root");
        }

        await AssertEntriesMatchRebuildAsync();
        context.Documents.RemoveRange(first, second);
        await context.SaveChangesAsync();
        await context.DeleteResourceAsync(folder);
        await context.SaveChangesAsync();
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task DeactivatedThenReactivatedFolder_PagesFollowThePointChecksThroughout()
    {
        await using var context = PageOrderFixture.CreateContext();
        var folder = NewResourceId("page_folder");
        await context.CreateResourceWithIdAsync(folder, "team", "Switchable folder", FgaTestDataSeeder.TestAgencyResourceId);
        var documents = Enumerable.Range(405, 5)
            .Select(number => PageOrderFixture.NewDocument(NewResourceId(), folder, number))
            .ToList();
        context.Documents.AddRange(documents);
        await context.SaveChangesAsync();
        var caller = FgaTestDataSeeder.AgencyAdminSubjectId;

        try
        {
            (await PagedIds(context, caller)).Should().Contain(documents.Select(document => document.Id));
            await AssertPagesMatchThePointChecksAsync(context, caller);

            await context.ProvisionResourceWithIdAsync(folder, "team", "Switchable folder", isActive: false);
            await context.SaveChangesAsync();

            (await PagedIds(context, caller)).Should().NotContain(documents.Select(document => document.Id));
            await AssertPagesMatchThePointChecksAsync(context, caller);
            await AssertEntriesMatchRebuildAsync();

            await context.ProvisionResourceWithIdAsync(folder, "team", "Switchable folder", isActive: true);
            await context.SaveChangesAsync();

            (await PagedIds(context, caller)).Should().Contain(documents.Select(document => document.Id));
            await AssertPagesMatchThePointChecksAsync(context, caller);
            await AssertEntriesMatchRebuildAsync();
        }
        finally
        {
            context.Documents.RemoveRange(documents);
            await context.SaveChangesAsync();
            await context.DeleteResourceAsync(folder);
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task DocumentWhoseOwnResourceIsInactive_HasNoEntries()
    {
        await using var context = PageOrderFixture.CreateContext();
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 410);
        document.Active = false;

        context.Documents.Add(document);
        await context.SaveChangesAsync();

        (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().BeEmpty();
        (await NodesOf(context, PageOrderTestDbContext.TitleEntries, document.Id)).Should().BeEmpty();
        await AssertEntriesMatchRebuildAsync();

        document.Active = true;
        await context.SaveChangesAsync();
        (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().HaveCount(4);

        context.Documents.Remove(document);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task DeletedResource_LeavesRowsThatPointAtItWithoutEntries()
    {
        await using var context = PageOrderFixture.CreateContext();
        var resource = NewResourceId("page_shared");
        await context.CreateResourceWithIdAsync(resource, "team", "Shared by counters", FgaTestDataSeeder.TestProjectResourceId);
        var counters = new[]
        {
            new PagedCounter { ResourceId = resource, Rank = 1 },
            new PagedCounter { ResourceId = resource, Rank = 2 },
        };
        context.Counters.AddRange(counters);
        await context.SaveChangesAsync();
        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counters[0].Id)).Should().HaveCount(4);

        await context.DeleteResourceAsync(resource);
        await context.SaveChangesAsync();

        foreach (var counter in counters)
        {
            (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEmpty();
        }

        await AssertEntriesMatchRebuildAsync();
        context.Counters.RemoveRange(counters);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ResourceAddedAfterRowsPointAtIt_GivesThemEntries()
    {
        await using var context = PageOrderFixture.CreateContext();
        var resource = NewResourceId("page_late");
        var counter = new PagedCounter { ResourceId = resource, Rank = 3 };
        context.Counters.Add(counter);
        await context.SaveChangesAsync();
        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEmpty();

        await context.CreateResourceWithIdAsync(resource, "team", "Created after its rows", FgaTestDataSeeder.TestProjectResourceId);
        await context.SaveChangesAsync();

        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEquivalentTo(
            resource,
            FgaTestDataSeeder.TestProjectResourceId,
            FgaTestDataSeeder.TestAgencyResourceId,
            "root");

        await context.DeleteResourceAsync(resource);
        await context.SaveChangesAsync();
        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEmpty();

        await context.CreateResourceWithIdAsync(resource, "team", "Created again", FgaTestDataSeeder.OtherAgencyResourceId);
        await context.SaveChangesAsync();

        (await NodesOf(context, PageOrderTestDbContext.CounterEntries, counter.Id)).Should().BeEquivalentTo(
            resource,
            FgaTestDataSeeder.OtherAgencyResourceId,
            "root");
        await AssertEntriesMatchRebuildAsync();

        context.Counters.Remove(counter);
        await context.DeleteResourceAsync(resource);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task ChangesMadeBySaveChangesInterceptors_AreKept()
    {
        await using var context = PageOrderFixture.CreateContext(null, new RankOnUpdate(6));
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestTeamResourceId, 417);
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        document.Rank.Should().NotBe(6);

        // The application changes a column no order uses; the interceptor changes the order column.
        document.Body = "edited";
        await context.SaveChangesAsync();

        document.Rank.Should().Be(6);
        (await context.Set<Dictionary<string, object>>(PageOrderTestDbContext.RankEntries)
                .Where(entry => EF.Property<Guid>(entry, "RowKey") == document.Id)
                .Select(entry => EF.Property<int>(entry, "Rank"))
                .ToListAsync())
            .Should().HaveCount(4).And.OnlyContain(rank => rank == 6);
        await AssertEntriesMatchRebuildAsync();

        context.Documents.Remove(document);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task OrderColumnTheDatabaseComputes_FollowsItsSource()
    {
        await using var context = PageOrderFixture.CreateContext();
        var score = new PagedScore { Id = Guid.NewGuid(), ResourceId = FgaTestDataSeeder.TestProjectResourceId, Points = 1 };
        context.Scores.Add(score);
        await context.SaveChangesAsync();
        score.Score.Should().Be(10);
        (await ScoresOf(context, score.Id)).Should().HaveCount(3).And.OnlyContain(value => value == 10);

        // Only the source column changes; the database computes the order column.
        score.Points = 2;
        await context.SaveChangesAsync();

        score.Score.Should().Be(20);
        (await ScoresOf(context, score.Id)).Should().HaveCount(3).And.OnlyContain(value => value == 20);
        var (actual, expected) = await PageOrderFixture.EntriesAndRebuildAsync(PageOrderTestDbContext.ScoreEntries, "PagedScores", "Score");
        actual.Should().Equal(expected);

        context.Scores.Remove(score);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task SaveOfANewDocument_WritesItsEntriesInOneCommand()
    {
        var commands = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(null, commands);
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestTeamResourceId, 418);
        context.Documents.Add(document);

        await context.SaveChangesAsync();

        // The new resource and the new row, for every declared order.
        commands.Commands.Where(command => command.Text.Contains("SqlOSFgaPage_", StringComparison.Ordinal))
            .Should().ContainSingle();
        (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().HaveCount(4);
        await AssertEntriesMatchRebuildAsync();

        context.Documents.Remove(document);
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task FailingSave_LeavesNoEntries()
    {
        Guid existingId;
        await using (var reader = PageOrderFixture.CreateContext())
        {
            existingId = await reader.Documents.Where(document => document.ResourceId == "page_doc_020").Select(document => document.Id).SingleAsync();
        }

        await using var context = PageOrderFixture.CreateContext();
        var fresh = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestTeamResourceId, 411);
        var duplicate = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestTeamResourceId, 412);
        duplicate.Id = existingId;
        context.Documents.AddRange(fresh, duplicate);

        await FluentActions.Awaiting(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        await using var verifier = PageOrderFixture.CreateContext();
        (await verifier.Documents.AnyAsync(document => document.Id == fresh.Id)).Should().BeFalse();
        (await NodesOf(verifier, PageOrderTestDbContext.RankEntries, fresh.Id)).Should().BeEmpty();
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task OuterTransaction_IsUsed_AndEntriesCommitOrRollBackWithIt()
    {
        await using var context = PageOrderFixture.CreateContext();
        var rolledBack = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 413);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Documents.Add(rolledBack);
            await context.SaveChangesAsync();

            context.Database.CurrentTransaction.Should().BeSameAs(transaction);
            (await NodesOf(context, PageOrderTestDbContext.RankEntries, rolledBack.Id)).Should().HaveCount(4);
            await transaction.RollbackAsync();
        }

        (await context.Documents.AsNoTracking().AnyAsync(document => document.Id == rolledBack.Id)).Should().BeFalse();
        (await NodesOf(context, PageOrderTestDbContext.RankEntries, rolledBack.Id)).Should().BeEmpty();

        await using var committing = PageOrderFixture.CreateContext();
        var committed = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 414);
        await using (var transaction = await committing.Database.BeginTransactionAsync())
        {
            committing.Documents.Add(committed);
            await committing.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verifier = PageOrderFixture.CreateContext();
        (await NodesOf(verifier, PageOrderTestDbContext.RankEntries, committed.Id)).Should().HaveCount(4);
        await AssertEntriesMatchRebuildAsync();
        verifier.Documents.Remove(await verifier.Documents.SingleAsync(document => document.Id == committed.Id));
        await verifier.SaveChangesAsync();
    }

    [TestMethod]
    [DataRow("on")]
    [DataRow("off")]
    public async Task SaveInTheCallersTransaction_RunsItsEntryStatementsWithoutJit_AndLeavesTheCallersSettingAsItWas(string callersJit)
    {
        await using var context = PageOrderFixture.CreateContext();
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 421);
        await using var transaction = await context.Database.BeginTransactionAsync();
        if (TestDatabase.IsPostgreSql)
        {
            await context.Database.ExecuteSqlRawAsync($"SET LOCAL jit = {callersJit}");
        }

        context.Documents.Add(document);
        await context.SaveChangesAsync();

        (await NodesOf(context, PageOrderTestDbContext.DocumentNodeIndex, document.Id)).Should().HaveCount(4);
        if (TestDatabase.IsPostgreSql)
        {
            // The entry statements turn JIT off for themselves (PostgreSqlDatabaseProvider.WrapPageMaintenanceSql)
            // and put the caller's setting back.
            (await context.Database.SqlQueryRaw<string>("SELECT current_setting('jit') AS \"Value\"").ToListAsync())
                .Should().Equal(callersJit);
        }

        await transaction.RollbackAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedEntryStatements_LeaveTheCallersTransactionAsBeforeTheSave(bool synchronous)
    {
        await using var context = PageOrderFixture.CreateContext(null, new FailingEntryStatements());
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 419);
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Documents.Add(document);

            var save = synchronous
                ? FluentActions.Invoking(() => context.SaveChanges()).Should().Throw<InvalidOperationException>()
                : (await FluentActions.Awaiting(() => context.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>());
            save.WithMessage(FailingEntryStatements.Message);

            // A caller that recovers and commits does not commit the failed save.
            context.ChangeTracker.Clear();
            await transaction.CommitAsync();
        }

        await using var verifier = PageOrderFixture.CreateContext();
        (await verifier.Documents.AnyAsync(candidate => candidate.Id == document.Id)).Should().BeFalse();
        (await verifier.Set<SqlOSFgaResource>().AnyAsync(resource => resource.Id == document.ResourceId)).Should().BeFalse();
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task EnlistedTransaction_IsUsed()
    {
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 420);
        using (var transaction = new System.Transactions.CommittableTransaction())
        {
            await using (var context = PageOrderFixture.CreateContext())
            {
                await context.Database.OpenConnectionAsync();
                System.Transactions.TransactionsDatabaseFacadeExtensions.EnlistTransaction(context.Database, transaction);
                context.Documents.Add(document);

                await context.SaveChangesAsync();

                (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().HaveCount(4);
                await context.Database.CloseConnectionAsync();
            }

            transaction.Rollback();
        }

        await using var verifier = PageOrderFixture.CreateContext();
        (await verifier.Documents.AnyAsync(candidate => candidate.Id == document.Id)).Should().BeFalse();
        (await NodesOf(verifier, PageOrderTestDbContext.RankEntries, document.Id)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task SynchronousSaveChanges_KeepsEntriesEqualToARebuild()
    {
        using var context = PageOrderFixture.CreateContext();
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 415);

        context.Documents.Add(document);
        context.SaveChanges();
        (await NodesOf(context, PageOrderTestDbContext.TitleEntries, document.Id)).Should().HaveCount(4);

        document.Title = "zz synchronous";
        context.SaveChanges();
        await AssertEntriesMatchRebuildAsync();

        context.Documents.Remove(document);
        context.SaveChanges();
        (await NodesOf(context, PageOrderTestDbContext.TitleEntries, document.Id)).Should().BeEmpty();
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task AmbientTransactionScope_IsUsed()
    {
        var document = PageOrderFixture.NewDocument(NewResourceId(), FgaTestDataSeeder.TestProjectResourceId, 416);
        using (new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var context = PageOrderFixture.CreateContext();
            context.Documents.Add(document);
            await context.SaveChangesAsync();
            (await NodesOf(context, PageOrderTestDbContext.RankEntries, document.Id)).Should().HaveCount(4);
        }

        await using var verifier = PageOrderFixture.CreateContext();
        (await verifier.Documents.AnyAsync(candidate => candidate.Id == document.Id)).Should().BeFalse();
        (await NodesOf(verifier, PageOrderTestDbContext.RankEntries, document.Id)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task Reconcile_FillsAnEmptyEntryTable_AndLeavesAFilledOneAlone()
    {
        await PageOrderFixture.ExecuteAsync($"DELETE FROM {TestDatabase.Qualify(PageOrderTestDbContext.TitleEntries)}");
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext();

        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, logs.CreateLogger("reconcile"), CancellationToken.None);

        var (actual, expected) = await PageOrderFixture.EntriesAndRebuildAsync(PageOrderTestDbContext.TitleEntries, "PagedDocuments", "Title");
        actual.Should().Equal(expected).And.NotBeEmpty();
        logs.Entries.Where(entry => entry.Level == LogLevel.Information)
            .Should().ContainSingle()
            .Which.Message.Should().Be($"Page order PagedDocument(Title, Id): filled {PageOrderTestDbContext.TitleEntries} with {expected.Count} entries for the rows already in PagedDocuments.");

        logs.Clear();
        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, logs.CreateLogger("reconcile"), CancellationToken.None);

        logs.Entries.Should().BeEmpty();
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task Reconcile_FillsAnEmptyNodeIndex()
    {
        await PageOrderFixture.ExecuteAsync($"DELETE FROM {TestDatabase.Qualify(PageOrderTestDbContext.DocumentNodeIndex)}");
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext();

        await SqlOSFgaPageOrderMaintenance.ReconcileAsync(context, logs.CreateLogger("reconcile"), CancellationToken.None);

        var (actual, expected) = await PageOrderFixture.EntriesAndRebuildAsync(PageOrderTestDbContext.DocumentNodeIndex, "PagedDocuments", sortColumn: null);
        actual.Should().Equal(expected).And.NotBeEmpty();
        logs.Entries.Where(entry => entry.Level == LogLevel.Information)
            .Should().ContainSingle()
            .Which.Message.Should().Be($"Node index PagedDocument(Id): filled {PageOrderTestDbContext.DocumentNodeIndex} with {expected.Count} entries for the rows already in PagedDocuments.");
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task RebuildPageOrdersAsync_RewritesEveryOrder()
    {
        await PageOrderFixture.ExecuteAsync($"DELETE FROM {TestDatabase.Qualify(PageOrderTestDbContext.RankEntries)}");
        await PageOrderFixture.ExecuteAsync($"DELETE FROM {TestDatabase.Qualify(PageOrderTestDbContext.CounterNodeIndex)}");
        var logs = new CapturingLoggerFactory();
        await using var context = PageOrderFixture.CreateContext();

        await new SqlOS.Fga.Services.SqlOSFgaAuthService(
                context,
                Microsoft.Extensions.Options.Options.Create(new SqlOS.Fga.Configuration.SqlOSFgaOptions()),
                logs.CreateLogger<SqlOS.Fga.Services.SqlOSFgaAuthService>())
            .RebuildPageOrdersAsync();

        await AssertEntriesMatchRebuildAsync();
        logs.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message)
            .Should().Contain(message => message.StartsWith($"Rebuilt page order PagedDocument(Rank, Id): ", StringComparison.Ordinal))
            .And.Contain(message => message.StartsWith($"Rebuilt node index PagedCounter(Id): ", StringComparison.Ordinal)
                && message.EndsWith($" entries in {PageOrderTestDbContext.CounterNodeIndex}.", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Rebuild_RunsWithoutACommandTimeout_AndRestoresTheContexts()
    {
        var commands = new CommandCapture();
        await using var context = PageOrderFixture.CreateContext(null, commands);
        context.Database.SetCommandTimeout(17);

        await PageOrderFixture.AuthService(context).RebuildPageOrdersAsync();

        commands.Commands.Should().Contain(command => command.Text.Contains("SqlOSFgaPage_", StringComparison.Ordinal))
            .And.OnlyContain(command => command.Timeout == 0);
        context.Database.GetCommandTimeout().Should().Be(17);
        await AssertEntriesMatchRebuildAsync();
    }

    [TestMethod]
    public async Task GrantWindows_AreExcludedFromTheCallersNodes()
    {
        await using var context = PageOrderFixture.CreateContext();
        var subject = await NewUserAsync(context, active: true);
        context.Set<SqlOSFgaGrant>().AddRange(
            Grant(subject, FgaTestDataSeeder.TestProjectResourceId, effectiveFrom: DateTime.UtcNow.AddDays(1)),
            Grant(subject, FgaTestDataSeeder.TestTeamResourceId, effectiveTo: DateTime.UtcNow.AddDays(-1)),
            Grant(subject, FgaTestDataSeeder.OtherAgencyResourceId));
        await context.SaveChangesAsync();

        (await NodesFor(context, subject)).Should().BeEquivalentTo(FgaTestDataSeeder.OtherAgencyResourceId);
    }

    [TestMethod]
    public async Task InactiveUserAndInactiveGroup_AreExcludedFromTheCallersNodes()
    {
        await using var context = PageOrderFixture.CreateContext();
        var inactiveUser = await NewUserAsync(context, active: false);
        var activeUser = await NewUserAsync(context, active: true);
        var group = $"subj_page_group_{Guid.NewGuid():N}";
        context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = group, SubjectTypeId = "group", DisplayName = "Inactive group" });
        context.Set<SqlOSFgaUserGroup>().Add(new SqlOSFgaUserGroup { Id = $"grp_{group}", SubjectId = group, Name = "Inactive group", IsActive = false });
        await context.SaveChangesAsync();
        context.Set<SqlOSFgaGrant>().AddRange(
            Grant(inactiveUser, FgaTestDataSeeder.TestProjectResourceId),
            Grant(group, FgaTestDataSeeder.TestTeamResourceId),
            Grant(activeUser, FgaTestDataSeeder.TestProjectResourceId));
        await context.SaveChangesAsync();

        (await NodesFor(context, inactiveUser)).Should().BeEmpty();
        (await NodesFor(context, activeUser, group)).Should().BeEquivalentTo(FgaTestDataSeeder.TestProjectResourceId);
    }

    [TestMethod]
    public async Task OverlappingGrants_AreReducedToMinimalNodes_AndEachDocumentAppearsOnce()
    {
        await using var context = PageOrderFixture.CreateContext();
        var subject = await NewUserAsync(context, active: true);
        context.Set<SqlOSFgaGrant>().AddRange(
            Grant(subject, FgaTestDataSeeder.TestAgencyResourceId),
            Grant(subject, FgaTestDataSeeder.TestTeamResourceId));
        await context.SaveChangesAsync();

        (await NodesFor(context, subject)).Should().BeEquivalentTo(FgaTestDataSeeder.TestAgencyResourceId);

        var expected = await PageOrderFixture.ExpectedAsync(context.Documents, "Rank", false, subject);
        var paged = (await PageOrderFixture.WalkAsync(context.Documents.OrderBy(document => document.Rank), subject, 7))
            .SelectMany(page => page.Data).Select(document => document.Id).ToList();

        paged.Should().OnlyHaveUniqueItems().And.Equal(expected);
    }

    private static async Task AssertEntriesMatchRebuildAsync()
    {
        foreach (var (table, app, column) in new (string, string, string?)[]
                 {
                     (PageOrderTestDbContext.RankEntries, "PagedDocuments", "Rank"),
                     (PageOrderTestDbContext.TitleEntries, "PagedDocuments", "Title"),
                     (PageOrderTestDbContext.CounterEntries, "PagedCounters", "Rank"),
                     (PageOrderTestDbContext.DocumentNodeIndex, "PagedDocuments", null),
                     (PageOrderTestDbContext.CounterNodeIndex, "PagedCounters", null),
                 })
        {
            var (actual, expected) = await PageOrderFixture.EntriesAndRebuildAsync(table, app, column);
            actual.Should().Equal(expected, $"the entries of {table} must equal a rebuild");
        }
    }

    private static async Task AssertPagesMatchThePointChecksAsync(PageOrderTestDbContext context, string caller)
    {
        var expected = await PageOrderFixture.ExpectedAsync(context.Documents, "Rank", false, caller);
        (await PagedIds(context, caller)).Should().Equal(expected);
    }

    private static async Task<List<Guid>> PagedIds(PageOrderTestDbContext context, string caller)
        => (await PageOrderFixture.WalkAsync(context.Documents.AsNoTracking().OrderBy(document => document.Rank), caller, 6))
            .SelectMany(page => page.Data).Select(document => document.Id).ToList();

    private static Task<List<string>> NodesOf(PageOrderTestDbContext context, string entryTable, Guid rowKey)
        => context.Set<Dictionary<string, object>>(entryTable)
            .Where(entry => EF.Property<Guid>(entry, "RowKey") == rowKey)
            .Select(entry => EF.Property<string>(entry, "NodeId"))
            .ToListAsync();

    private static Task<List<string>> NodesOf(PageOrderTestDbContext context, string entryTable, int rowKey)
        => context.Set<Dictionary<string, object>>(entryTable)
            .Where(entry => EF.Property<int>(entry, "RowKey") == rowKey)
            .Select(entry => EF.Property<string>(entry, "NodeId"))
            .ToListAsync();

    private static Task<List<int>> ScoresOf(PageOrderTestDbContext context, Guid rowKey)
        => context.Set<Dictionary<string, object>>(PageOrderTestDbContext.ScoreEntries)
            .Where(entry => EF.Property<Guid>(entry, "RowKey") == rowKey)
            .Select(entry => EF.Property<int>(entry, "Score"))
            .ToListAsync();

    private static Task<List<string>> NodesFor(PageOrderTestDbContext context, params string[] subjectIds)
        => context.PageNodes(JsonSerializer.Serialize(subjectIds), FgaTestDataSeeder.ViewPermissionId)
            .Select(node => node.NodeId)
            .ToListAsync();

    private static async Task<string> NewUserAsync(PageOrderTestDbContext context, bool active)
    {
        var subject = $"subj_page_{Guid.NewGuid():N}";
        context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = subject, SubjectTypeId = "user", DisplayName = subject });
        context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = $"usr_{subject}", SubjectId = subject, IsActive = active });
        await context.SaveChangesAsync();
        return subject;
    }

    private static SqlOSFgaGrant Grant(string subject, string resource, DateTime? effectiveFrom = null, DateTime? effectiveTo = null)
        => new()
        {
            Id = $"grant_page_{Guid.NewGuid():N}",
            SubjectId = subject,
            ResourceId = resource,
            RoleId = FgaTestDataSeeder.AgencyMemberRoleId,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo
        };

    private static string NewResourceId(string prefix = "page_doc")
        => $"{prefix}_{Guid.NewGuid():N}";

    /// <summary>Sets every updated document's rank during the save, as an audit interceptor sets UpdatedAt.</summary>
    private sealed class RankOnUpdate(int rank) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Apply(eventData.Context!);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Apply(eventData.Context!);
            return ValueTask.FromResult(result);
        }

        private void Apply(DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries<PagedDocument>().Where(entry => entry.State == EntityState.Modified))
            {
                entry.Property(document => document.Rank).CurrentValue = rank;
            }
        }
    }
}
