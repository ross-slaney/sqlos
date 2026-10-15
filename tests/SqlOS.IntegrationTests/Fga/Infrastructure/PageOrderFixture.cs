using System.Collections.Concurrent;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Pagination;

namespace SqlOS.IntegrationTests.Fga.Infrastructure;

/// <summary>
/// Creates the page-order tables on the shared test database and seeds 30 documents under each
/// node of the <see cref="FgaTestDataSeeder"/> tree, plus a caller granted three single documents.
/// </summary>
internal static class PageOrderFixture
{
    public const string ViewPermission = "TEST_VIEW";
    public const string DirectSubjectId = "subj_page_direct";
    public const int DocumentsPerNode = 30;
    public const string FilterTerm = "an";
    public const int MaxPageSize = PageQueryableExtensions.MaxPageSize;

    public static readonly string[] Folders =
    [
        "root",
        FgaTestDataSeeder.TestAgencyResourceId,
        FgaTestDataSeeder.TestTeamResourceId,
        FgaTestDataSeeder.TestProjectResourceId,
        FgaTestDataSeeder.OtherAgencyResourceId,
    ];

    public static readonly string[] Callers =
    [
        FgaTestDataSeeder.SystemAdminSubjectId,
        FgaTestDataSeeder.AgencyAdminSubjectId,
        FgaTestDataSeeder.AgencyMemberSubjectId,
        FgaTestDataSeeder.GroupMemberSubjectId,
        FgaTestDataSeeder.UnauthorizedSubjectId,
        DirectSubjectId,
    ];

    private static readonly string[] Words = ["apple", "banana", "cherry", "date", "elder"];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;

    public static PageOrderTestDbContext CreateContext(
        ILoggerFactory? loggerFactory = null,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<PageOrderTestDbContext>()
            .UseTestProvider(AspireFixture.SqlConnectionString);
        if (loggerFactory != null)
        {
            builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
        }

        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new PageOrderTestDbContext(builder.Options);
    }

    public static SqlOSFgaAuthService AuthService(PageOrderTestDbContext context)
        => new(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);

    public static async Task EnsureSeededAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_seeded)
            {
                return;
            }

            await using var context = CreateContext();
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

            context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType
            {
                Id = PagedDocument.ResourceType,
                Name = "Page document"
            });
            context.Categories.AddRange(new PagedCategory { Name = "Alpha" }, new PagedCategory { Name = "Beta" });
            await context.SaveChangesAsync();

            var categories = await context.Categories.OrderBy(category => category.Id).Select(category => category.Id).ToListAsync();
            var number = 0;
            foreach (var folder in Folders)
            {
                for (var index = 0; index < DocumentsPerNode; index++, number++)
                {
                    context.Documents.Add(NewDocument($"page_doc_{number:000}", folder, number, categories[number % 2]));
                }
            }

            await context.SaveChangesAsync();

            context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject { Id = DirectSubjectId, SubjectTypeId = "user", DisplayName = "Direct grants" });
            context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = "usr_page_direct", SubjectId = DirectSubjectId, IsActive = true });
            await context.SaveChangesAsync();
            foreach (var documentNumber in new[] { 0, 1, 2 })
            {
                context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
                {
                    Id = $"grant_page_direct_{documentNumber}",
                    SubjectId = DirectSubjectId,
                    ResourceId = $"page_doc_{documentNumber:000}",
                    RoleId = FgaTestDataSeeder.AgencyMemberRoleId
                });
            }

            await context.SaveChangesAsync();
            _seeded = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static PagedDocument NewDocument(string resourceId, string folder, int number, int? categoryId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ResourceId = resourceId,
            FolderResourceId = folder,
            Title = $"{Words[number % Words.Length]} {number:000}",
            Rank = number % 7,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(number * 37 % 151),
            Body = "seed",
            CategoryId = categoryId
        };

    public static IOrderedQueryable<PagedDocument> Order(IQueryable<PagedDocument> query, string order, bool descending)
        => (order, descending) switch
        {
            ("Rank", false) => query.OrderBy(document => document.Rank),
            ("Rank", true) => query.OrderByDescending(document => document.Rank),
            ("Title", false) => query.OrderBy(document => document.Title),
            ("Title", true) => query.OrderByDescending(document => document.Title),
            ("CreatedAt", false) => query.OrderBy(document => document.CreatedAt),
            ("CreatedAt", true) => query.OrderByDescending(document => document.CreatedAt),
            ("Id", false) => query.OrderBy(document => document.Id),
            ("Id", true) => query.OrderByDescending(document => document.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };

    /// <summary>How the engine orders a <see cref="Guid"/> key: SQL Server's uniqueidentifier order, PostgreSQL's uuid byte order.</summary>
    public static IComparer<Guid> KeyOrder { get; } = Comparer<Guid>.Create((left, right) => TestDatabase.IsPostgreSql
        ? string.CompareOrdinal(left.ToString("N"), right.ToString("N"))
        : new SqlGuid(left).CompareTo(new SqlGuid(right)));

    /// <summary>
    /// The resources among <paramref name="resourceIds"/> that <c>CheckAccessAsync</c> lets the subject
    /// view, each checked on its own on a fresh context: an oracle independent of the page's SQL.
    /// </summary>
    public static async Task<HashSet<string>> AllowedResourcesAsync(string subjectId, IEnumerable<string> resourceIds)
    {
        await using var context = CreateContext();
        var authService = AuthService(context);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resourceId in resourceIds.Distinct(StringComparer.Ordinal))
        {
            if ((await authService.CheckAccessAsync(subjectId, ViewPermission, resourceId)).Allowed)
            {
                allowed.Add(resourceId);
            }
        }

        return allowed;
    }

    /// <summary>
    /// The ground truth for a walk: the rows of <paramref name="query"/> on an allowed resource, sorted in
    /// memory by the order and then the key, as the database sorts them.
    /// </summary>
    public static async Task<List<Guid>> ExpectedAsync(
        IQueryable<PagedDocument> query,
        string order,
        bool descending,
        string subjectId,
        IReadOnlySet<string>? allowed = null)
    {
        var rows = await query.AsNoTracking().ToListAsync();
        allowed ??= await AllowedResourcesAsync(subjectId, rows.Select(row => row.ResourceId));
        return Sort(rows.Where(row => allowed.Contains(row.ResourceId)), order, descending);
    }

    public static List<Guid> Sort(IEnumerable<PagedDocument> rows, string order, bool descending)
    {
        var sorted = (order, descending) switch
        {
            ("Rank", false) => rows.OrderBy(document => document.Rank),
            ("Rank", true) => rows.OrderByDescending(document => document.Rank),
            ("Title", false) => rows.OrderBy(document => document.Title, StringComparer.Ordinal),
            ("Title", true) => rows.OrderByDescending(document => document.Title, StringComparer.Ordinal),
            ("CreatedAt", false) => rows.OrderBy(document => document.CreatedAt),
            ("CreatedAt", true) => rows.OrderByDescending(document => document.CreatedAt),
            ("Id", false) => rows.OrderBy(document => document.Id, KeyOrder),
            ("Id", true) => rows.OrderByDescending(document => document.Id, KeyOrder),
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };

        return (descending ? sorted.ThenByDescending(document => document.Id, KeyOrder) : sorted.ThenBy(document => document.Id, KeyOrder))
            .Select(document => document.Id)
            .ToList();
    }

    /// <summary>Walks ToPageAsync from <paramref name="cursor"/> to the last page.</summary>
    public static async Task<List<SqlOSCursorPage<PagedDocument>>> WalkAsync(
        IQueryable<PagedDocument> ordered,
        string subjectId,
        int pageSize,
        string? cursor = null)
    {
        var pages = new List<SqlOSCursorPage<PagedDocument>>();
        do
        {
            var page = await ordered.ToPageAsync(subjectId, ViewPermission, pageSize, cursor);
            pages.Add(page);
            cursor = page.NextCursor;
            if (pages.Count > 2_000)
            {
                throw new InvalidOperationException("The walk did not end.");
            }
        }
        while (cursor != null);

        return pages;
    }

    /// <summary>
    /// The entries of an entry table, and what a rebuild would write, as comparable rows. The key
    /// order has no sort column.
    /// </summary>
    public static async Task<(List<string> Actual, List<string> Expected)> EntriesAndRebuildAsync(
        string entryTable,
        string appTable,
        string? sortColumn)
    {
        var entries = TestDatabase.Qualify(entryTable);
        var app = TestDatabase.Quote(appTable);
        var ancestors = TestDatabase.IsPostgreSql
            ? $"CROSS JOIN LATERAL {TestDatabase.Qualify("fn_ResourceAncestors")}(t.{TestDatabase.Quote("ResourceId")}) AS a"
            : $"CROSS APPLY {TestDatabase.Qualify("fn_ResourceAncestors")}(t.{TestDatabase.Quote("ResourceId")}) AS a";
        var entrySort = sortColumn == null ? string.Empty : $"{TestDatabase.Quote(sortColumn)}, ";
        var appSort = sortColumn == null ? string.Empty : $"t.{TestDatabase.Quote(sortColumn)}, ";

        var actual = await ReadRowsAsync(
            $"SELECT {TestDatabase.Quote("NodeId")}, {entrySort}{TestDatabase.Quote("RowKey")} FROM {entries}");
        var expected = await ReadRowsAsync(
            $"SELECT a.{TestDatabase.Quote("NodeId")}, {appSort}t.{TestDatabase.Quote("Id")} FROM {app} AS t {ancestors}");
        actual.Sort(StringComparer.Ordinal);
        expected.Sort(StringComparer.Ordinal);
        return (actual, expected);
    }

    public static async Task<List<string>> ReadRowsAsync(string sql)
    {
        await using var connection = TestDatabase.CreateConnection(AspireFixture.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    public static async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = TestDatabase.CreateConnection(AspireFixture.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Captures log messages, including EF Core's executed commands.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, EventId EventId, string Message)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, EventId EventId, string Message)> Entries => _entries.ToList();

    public IEnumerable<string> Commands
        => Entries
            .Where(entry => entry.Category == DbLoggerCategory.Database.Command.Name
                && entry.EventId.Id == RelationalEventId.CommandExecuted.Id)
            .Select(entry => entry.Message);

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(
        string category,
        ConcurrentQueue<(string Category, LogLevel Level, EventId EventId, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue((category, logLevel, eventId, formatter(state, exception)));
    }
}

/// <summary>Keeps a copy of every command a context runs: text and parameter values.</summary>
internal sealed class CommandCapture : DbCommandInterceptor
{
    private readonly ConcurrentQueue<CapturedCommand> _commands = new();

    public IReadOnlyList<CapturedCommand> Commands => _commands.ToList();

    public void Clear() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbCommand command)
        => _commands.Enqueue(new CapturedCommand(
            command.CommandText,
            command.Parameters.Cast<DbParameter>()
                .Select(parameter => new CapturedParameter(parameter.ParameterName, parameter.DbType, parameter.Size, parameter.Value, parameter))
                .ToList(),
            command.CommandTimeout));
}

/// <summary>Fails every command that writes page-order entries, after the rows it follows were saved.</summary>
internal sealed class FailingEntryStatements : DbCommandInterceptor
{
    public const string Message = "Entry statements failed for the test.";

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
        => Writes(command) ? throw new InvalidOperationException(Message) : result;

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
        => Writes(command) ? throw new InvalidOperationException(Message) : ValueTask.FromResult(result);

    private static bool Writes(DbCommand command)
        => command.CommandText.Contains("SqlOSFgaPage_", StringComparison.Ordinal);
}

internal sealed record CapturedCommand(string Text, IReadOnlyList<CapturedParameter> Parameters, int Timeout);

internal sealed record CapturedParameter(string Name, System.Data.DbType DbType, int Size, object? Value, DbParameter Original);
