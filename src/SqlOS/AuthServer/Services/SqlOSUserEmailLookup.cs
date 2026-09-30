using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Security-relevant lookups of a <see cref="SqlOSUserEmail"/> by address. SQL narrows the
/// candidates by key; the match is then confirmed in C# by ordinal equality of canonical keys, so
/// a database collation that treats two different strings as equal (case, width, <c>ß</c>/<c>ss</c>,
/// ignorable characters) never selects another person's account.
/// </summary>
internal static class SqlOSUserEmailLookup
{
    /// <summary>
    /// Finds the account email for a typed or asserted address. Returns null when the address is
    /// not valid or no stored address is the same mailbox.
    /// </summary>
    public static async Task<SqlOSUserEmail?> FindByEmailAsync(
        this IQueryable<SqlOSUserEmail> source,
        string? email,
        CancellationToken cancellationToken = default)
        => SqlOSEmailAddress.TryNormalize(email, out var normalizedEmail)
            ? await source.FindByNormalizedEmailAsync(normalizedEmail, email, cancellationToken)
            : null;

    /// <summary>Finds the account email for an already canonical key.</summary>
    public static async Task<SqlOSUserEmail?> FindByNormalizedEmailAsync(
        this IQueryable<SqlOSUserEmail> source,
        string normalizedEmail,
        string? rawEmail,
        CancellationToken cancellationToken = default)
    {
        var keys = SqlOSEmailAddress.LookupKeys(normalizedEmail, rawEmail);
        var candidates = keys.Count == 1
            ? await source.Where(x => x.NormalizedEmail == normalizedEmail).ToListAsync(cancellationToken)
            : await source.Where(x => keys.Contains(x.NormalizedEmail)).ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(x => SqlOSEmailAddress.MatchesStoredEmail(x, normalizedEmail));
    }
}
