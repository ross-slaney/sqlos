using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Finds and spends the temporary tokens a person presents to an identity process: a reset or
/// verification link, an MFA challenge, an authenticator enrollment. A token is found only by its
/// own kind and its raw value's hash, and only while it can still be used.
/// </summary>
internal static class PresentedTokens
{
    /// <summary>
    /// The tracked token of <paramref name="kind"/> whose raw value is <paramref name="rawToken"/>,
    /// unspent and unexpired at <paramref name="now"/>, or null.
    /// </summary>
    public static Task<SqlOSTemporaryToken?> FindAsync(
        ISqlOSAuthServerDbContext context,
        TemporaryTokenKind kind,
        string rawToken,
        DateTime now,
        CancellationToken cancellationToken)
        => context.Set<SqlOSTemporaryToken>()
            .Where(SqlOSTemporaryToken.OfKind(kind))
            .Where(SqlOSTemporaryToken.Presented(rawToken))
            .Where(SqlOSTemporaryToken.UsableAt(now))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Spends the single-use token of <paramref name="kind"/> whose raw value is
    /// <paramref name="rawToken"/> and saves it on its own, so a refusal that follows still finds it
    /// spent. Returns null when there is none, or when a concurrent request spent it first (its
    /// <c>ConsumedAt</c> is a concurrency token): the losing request's change and its events are
    /// discarded.
    /// </summary>
    public static async Task<SqlOSTemporaryToken?> SpendAsync(
        ISqlOSAuthServerDbContext context,
        TemporaryTokenKind kind,
        string rawToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var token = await FindAsync(context, kind, rawToken, now, cancellationToken);
        if (token == null)
        {
            return null;
        }

        token.Consume(kind, now);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            foreach (var entry in exception.Entries)
            {
                entry.State = EntityState.Detached;
            }

            return null;
        }

        return token;
    }
}
