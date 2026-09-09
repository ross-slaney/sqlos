using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using SqlOS.AuthServer.Extensions;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;

namespace SqlOS.OneCall.Api;

/// <summary>FGA vocabulary seeded through <c>app.Authorization(...)</c> and checked by <see cref="NotesService"/>.</summary>
public static class NotesAuthorization
{
    public const string NotebookType = "notebook";
    public const string OwnerRole = "notebook_owner";
    public const string ReadPermission = "NOTES_READ";
    public const string WritePermission = "NOTES_WRITE";

    public static string NotebookId(string userId) => $"notebook::{userId}";
}

public sealed class Note
{
    public Guid Id { get; set; }
    public string NotebookId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed record NoteRequest(string Text);

public sealed class Notebook
{
    public string UserId { get; set; } = string.Empty;
}

public sealed class NotesDbContext(DbContextOptions<NotesDbContext> options)
    : SqlOSDbContext<NotesDbContext>(options)
{
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Notebook> Notebooks => Set<Notebook>();

    protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notebook>(entity =>
        {
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).HasMaxLength(100);
        });
        modelBuilder.Entity<Note>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NotebookId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.Text).HasMaxLength(2000).IsRequired();
            entity.HasIndex(x => x.NotebookId);
        });
    }
}

/// <summary>
/// The single domain service behind both surfaces. The API handlers and the MCP tools call it with
/// the user SqlOS already authenticated, and it enforces FGA on the user's notebook.
/// </summary>
public sealed class NotesService(NotesDbContext db, ISqlOSFgaAuthService fga)
{
    public async Task<IReadOnlyList<Note>> ListAsync(string userId, CancellationToken ct)
    {
        var notebookId = await EnsureNotebookAsync(userId, NotesAuthorization.ReadPermission, ct);
        return await db.Notes.Where(n => n.NotebookId == notebookId).OrderBy(n => n.CreatedAt).ToListAsync(ct);
    }

    public async Task<Note> AddAsync(string userId, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 2000)
        {
            throw new ArgumentException("A note must contain between 1 and 2000 characters.", nameof(text));
        }

        var notebookId = await EnsureNotebookAsync(userId, NotesAuthorization.WritePermission, ct);
        var note = new Note { Id = Guid.NewGuid(), NotebookId = notebookId, Text = text.Trim(), CreatedAt = DateTime.UtcNow };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return note;
    }

    private async Task<string> EnsureNotebookAsync(string userId, string permission, CancellationToken ct)
    {
        var notebookId = NotesAuthorization.NotebookId(userId);

        await CreateNotebookIfMissingAsync(userId, notebookId, ct);

        var access = await fga.CheckAccessAsync(userId, permission, notebookId);
        return access.Allowed
            ? notebookId
            : throw new UnauthorizedAccessException($"{userId} lacks {permission} on {notebookId}.");
    }

    private async Task CreateNotebookIfMissingAsync(string userId, string notebookId, CancellationToken ct)
    {
        if (await db.Notebooks.AnyAsync(x => x.UserId == userId, ct)) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Notebooks.Add(new Notebook { UserId = userId });
        try
        {
            // The unique user key reserves creation across requests and replicas. Another creator
            // waits for this transaction; it can only observe a notebook whose grant also committed.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            if (!await db.Notebooks.AnyAsync(x => x.UserId == userId, ct)) throw;
            return;
        }

        await db.ProvisionUserSubjectAsync(userId, userId, cancellationToken: ct);
        await db.ProvisionResourceWithIdAsync(notebookId, NotesAuthorization.NotebookType, $"Notebook of {userId}", cancellationToken: ct);
        await db.GrantRoleAsync(userId, notebookId, NotesAuthorization.OwnerRole, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

/// <summary>
/// MCP tools exposed on /mcp. SqlOS already validated the token for the MCP audience; the tools
/// act as the connecting user through <see cref="HttpContext.GetSqlOSValidatedToken"/>.
/// </summary>
public sealed class NotesMcpTools
{
    [McpServerTool(Name = "list_notes"), Description("Lists the connecting user's notes.")]
    public static async Task<IReadOnlyList<string>> ListNotes(IHttpContextAccessor http, NotesService notes, CancellationToken ct)
        => (await notes.ListAsync(RequireUser(http), ct)).Select(n => n.Text).ToArray();

    [McpServerTool(Name = "add_note"), Description("Adds a note to the connecting user's notebook.")]
    public static async Task<string> AddNote(
        IHttpContextAccessor http,
        NotesService notes,
        [Description("The note text.")] string text,
        CancellationToken ct)
        => (await notes.AddAsync(RequireUser(http), text, ct)).Id.ToString();

    private static string RequireUser(IHttpContextAccessor http)
        => http.HttpContext?.GetSqlOSValidatedToken()?.UserId
           ?? throw new InvalidOperationException("This tool requires a user token.");
}
