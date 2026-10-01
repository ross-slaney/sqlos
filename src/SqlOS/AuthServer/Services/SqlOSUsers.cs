using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Loads the <see cref="SqlOSUser"/> aggregate for a change: the account, tracked, with the parts
/// the change needs (<see cref="SqlOSUserParts"/>), each part with all of its live members.
/// </summary>
/// <remarks>
/// The aggregate refuses to decide with a part that was not loaded, so a change that forgets to
/// load what it touches fails instead of silently skipping members (a claim that saw no
/// credentials would evict none). A part is loaded once per unit of work: loading an account that
/// is already tracked loads only the parts it is missing, into the same instance.
/// </remarks>
internal static class SqlOSUsers
{
    /// <summary>The tracked account <paramref name="userId"/> with <paramref name="parts"/> loaded, or null.</summary>
    public static async Task<SqlOSUser?> FindUserAsync(
        this ISqlOSAuthServerDbContext context,
        string? userId,
        SqlOSUserParts parts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var user = await context.Set<SqlOSUser>().FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user != null)
        {
            await context.LoadUserPartsAsync(user, parts, cancellationToken);
        }

        return user;
    }

    /// <summary>
    /// The tracked account <paramref name="userId"/> with <paramref name="parts"/> loaded. A
    /// missing account fails as <c>FirstAsync</c> does.
    /// </summary>
    public static async Task<SqlOSUser> GetUserAsync(
        this ISqlOSAuthServerDbContext context,
        string userId,
        SqlOSUserParts parts,
        CancellationToken cancellationToken = default)
    {
        var user = await context.Set<SqlOSUser>().FirstAsync(x => x.Id == userId, cancellationToken);
        await context.LoadUserPartsAsync(user, parts, cancellationToken);
        return user;
    }

    /// <summary>Loads the parts of the tracked <paramref name="user"/> in <paramref name="parts"/> that are not loaded yet.</summary>
    public static async Task LoadUserPartsAsync(
        this ISqlOSAuthServerDbContext context,
        SqlOSUser user,
        SqlOSUserParts parts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var missing = parts & ~user.LoadedParts;
        if (missing == SqlOSUserParts.None)
        {
            return;
        }

        var dbContext = context as DbContext
            ?? throw new InvalidOperationException("The SqlOS unit of work must be an EF Core DbContext.");
        var entry = dbContext.Entry(user);
        if (entry.State == EntityState.Detached)
        {
            throw new InvalidOperationException("Load the parts of a user the unit of work tracks; changes to a detached user are never saved.");
        }

        if (missing.HasFlag(SqlOSUserParts.Emails))
        {
            await entry.Collection(x => x.Emails).LoadAsync(cancellationToken);
        }

        if (missing.HasFlag(SqlOSUserParts.PhoneNumbers))
        {
            await entry.Collection(x => x.PhoneNumbers).Query()
                .Where(x => x.RemovedAt == null)
                .LoadAsync(cancellationToken);
        }

        if (missing.HasFlag(SqlOSUserParts.Credentials))
        {
            await entry.Collection(x => x.Credentials).Query()
                .Where(x => x.RevokedAt == null)
                .LoadAsync(cancellationToken);
        }

        if (missing.HasFlag(SqlOSUserParts.Authenticators))
        {
            await entry.Collection(x => x.Authenticators).Query()
                .Where(x => x.RevokedAt == null)
                .LoadAsync(cancellationToken);
        }

        if (missing.HasFlag(SqlOSUserParts.RecoveryCodes))
        {
            await entry.Collection(x => x.RecoveryCodes).Query()
                .Where(x => x.ConsumedAt == null && x.RevokedAt == null)
                .LoadAsync(cancellationToken);
        }

        if (missing.HasFlag(SqlOSUserParts.ExternalIdentities))
        {
            await entry.Collection(x => x.ExternalIdentities).LoadAsync(cancellationToken);
        }

        if (missing.HasFlag(SqlOSUserParts.MfaPolicyOverride))
        {
            await entry.Reference(x => x.MfaPolicyOverride).LoadAsync(cancellationToken);
        }

        user.MarkLoaded(missing);
    }
}
