using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.Database;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// The single implementation of remembered OAuth consent grants and consent-screen scope
/// display names. Every surface (authorization completion, hosted/headless endpoints, admin
/// APIs, account self-service, and the CIMD tripwire) routes grant policy through this type
/// so coverage and revocation semantics cannot drift between control planes.
/// </summary>
public sealed class SqlOSConsentService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;

    public SqlOSConsentService(ISqlOSAuthServerDbContext context, SqlOSCryptoService cryptoService)
    {
        _context = context;
        _cryptoService = cryptoService;
    }

    /// <summary>
    /// True when the user's active grant for the client covers every currently granted scope
    /// (ordinal set comparison). An empty granted set is covered by any active grant.
    /// The grant must additionally have been approved against the client's current
    /// security-sensitive metadata: a stored fingerprint that no longer matches
    /// <paramref name="currentClientMetadataFingerprint"/> means the approval raced a CIMD
    /// metadata refresh, so the grant is not reused and the user re-consents. A null stored
    /// fingerprint is legacy (pre-fingerprint) data and stays accepted.
    /// </summary>
    public async Task<bool> HasCoveringGrantAsync(
        string userId,
        string clientApplicationId,
        IReadOnlyCollection<string> grantedScopes,
        string currentClientMetadataFingerprint,
        CancellationToken cancellationToken = default)
    {
        var grant = await FindActiveGrantAsync(userId, clientApplicationId, tracking: false, cancellationToken);
        if (grant == null)
        {
            return false;
        }

        if (grant.ClientMetadataFingerprint != null
            && !string.Equals(grant.ClientMetadataFingerprint, currentClientMetadataFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var stored = new HashSet<string>(SqlOSScopePolicy.Split(grant.Scope), StringComparer.Ordinal);
        return grantedScopes.All(stored.Contains);
    }

    /// <summary>
    /// Records an approval. An existing active grant absorbs the newly granted scopes (union);
    /// a previously revoked grant is reactivated with exactly the newly granted set so old,
    /// revoked approvals do not silently widen a fresh consent.
    /// Concurrent approvals for the same (user, client) can race: two first approvals both
    /// insert (the filtered unique active index rejects one) and two escalations can lose a
    /// scope union (the UpdatedAt concurrency token fails the stale save). Both conflicts
    /// detach the losing entries, reload the winning row, and merge again — bounded to
    /// <see cref="MaxUpsertAttempts"/> attempts, then the conflict is rethrown.
    /// </summary>
    public async Task<SqlOSConsentGrant> UpsertGrantAsync(
        string userId,
        string clientApplicationId,
        IReadOnlyCollection<string> grantedScopes,
        string? clientMetadataFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await UpsertGrantOnceAsync(userId, clientApplicationId, grantedScopes, clientMetadataFingerprint, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxUpsertAttempts)
            {
                DetachConflictedGrantEntries(ex, userId, clientApplicationId);
            }
            catch (DbUpdateException ex) when (attempt < MaxUpsertAttempts && IsUniqueConstraintViolation(ex))
            {
                DetachConflictedGrantEntries(ex, userId, clientApplicationId);
            }
        }
    }

    private const int MaxUpsertAttempts = 3;

    private async Task<SqlOSConsentGrant> UpsertGrantOnceAsync(
        string userId,
        string clientApplicationId,
        IReadOnlyCollection<string> grantedScopes,
        string? clientMetadataFingerprint,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var active = await FindActiveGrantAsync(userId, clientApplicationId, tracking: true, cancellationToken);
        if (active != null)
        {
            active.Scope = ComputeUnionScopeWithinLimit(active.Scope, grantedScopes);
            active.UpdatedAt = now;
            // The user just approved against the caller's current view of the client's
            // metadata, so the stored fingerprint moves forward with the approval.
            active.ClientMetadataFingerprint = clientMetadataFingerprint;
            await _context.SaveChangesAsync(cancellationToken);
            return active;
        }

        var revoked = await _context.Set<SqlOSConsentGrant>()
            .Where(x => x.UserId == userId && x.ClientApplicationId == clientApplicationId && x.RevokedAt != null)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (revoked != null)
        {
            revoked.Scope = ComputeUnionScopeWithinLimit(existingScope: null, grantedScopes);
            revoked.GrantedAt = now;
            revoked.UpdatedAt = now;
            revoked.RevokedAt = null;
            revoked.RevocationReason = null;
            revoked.ClientMetadataFingerprint = clientMetadataFingerprint;
            await _context.SaveChangesAsync(cancellationToken);
            return revoked;
        }

        var grant = new SqlOSConsentGrant
        {
            Id = _cryptoService.GenerateId("cgr"),
            UserId = userId,
            ClientApplicationId = clientApplicationId,
            Scope = ComputeUnionScopeWithinLimit(existingScope: null, grantedScopes),
            GrantedAt = now,
            UpdatedAt = now,
            ClientMetadataFingerprint = clientMetadataFingerprint
        };
        _context.Set<SqlOSConsentGrant>().Add(grant);
        await _context.SaveChangesAsync(cancellationToken);
        return grant;
    }

    // Matches the SqlOSConsentGrants.Scope column length (NVARCHAR(4000)). Approvals union
    // scopes across requests, so a stored grant can outgrow any single request's scope.
    internal const int MaxGrantScopeLength = 4000;

    /// <summary>
    /// Deterministic guard for the grant scope union: throws the stable overflow failure when
    /// the union of the stored scope and the newly granted scopes would exceed the
    /// SqlOSConsentGrants.Scope column. Touches no state, so
    /// SqlOSAuthorizationServerService.ApproveConsentAsync calls it as a precheck before the
    /// one-time consent token is consumed.
    /// </summary>
    internal static void EnsureUnionWithinLimits(string? existingScope, IReadOnlyCollection<string> grantedScopes)
        => ComputeUnionScopeWithinLimit(existingScope, grantedScopes);

    /// <summary>
    /// Cheap read of the active grant's stored scope for a (user, client) pair — null when no
    /// active grant exists. Used by the approval path's overflow precheck.
    /// </summary>
    public async Task<string?> GetActiveGrantScopeAsync(
        string userId,
        string clientApplicationId,
        CancellationToken cancellationToken = default)
        => (await FindActiveGrantAsync(userId, clientApplicationId, tracking: false, cancellationToken))?.Scope;

    private static string ComputeUnionScopeWithinLimit(string? existingScope, IReadOnlyCollection<string> grantedScopes)
    {
        var union = SqlOSScopePolicy.Split(existingScope);
        foreach (var scope in grantedScopes)
        {
            if (!union.Contains(scope, StringComparer.Ordinal))
            {
                union.Add(scope);
            }
        }

        var joined = SqlOSScopePolicy.Join(union);
        if (joined.Length > MaxGrantScopeLength)
        {
            throw new InvalidOperationException($"The combined consent grant scope cannot exceed {MaxGrantScopeLength} characters.");
        }

        return joined;
    }

    public Task<IReadOnlyList<SqlOSConsentScopeDisplay>> BuildScopeDisplaysAsync(
        IReadOnlyCollection<string> grantedScopes,
        CancellationToken cancellationToken = default)
        => BuildScopeDisplaysAsync(_context, grantedScopes, cancellationToken);

    /// <summary>
    /// Resolves consent-screen display entries for the granted scopes: the catalog display
    /// name when an entry exists, otherwise the raw scope string. Orphaned code-owned
    /// entries (seed removed from source control) are excluded so a retired display name
    /// never keeps rendering stale text; those scopes fall back to the raw scope string.
    /// </summary>
    public static async Task<IReadOnlyList<SqlOSConsentScopeDisplay>> BuildScopeDisplaysAsync(
        ISqlOSAuthServerDbContext context,
        IReadOnlyCollection<string> grantedScopes,
        CancellationToken cancellationToken = default)
    {
        if (grantedScopes.Count == 0)
        {
            return Array.Empty<SqlOSConsentScopeDisplay>();
        }

        var scopes = grantedScopes.ToList();
        var catalog = await context.Set<SqlOSScopeDisplayName>()
            .AsNoTracking()
            .Where(x => scopes.Contains(x.Scope) && x.ConfigurationOrphanedAt == null)
            .ToListAsync(cancellationToken);

        return scopes
            .Select(scope =>
            {
                var entry = catalog.FirstOrDefault(x => string.Equals(x.Scope, scope, StringComparison.Ordinal));
                return entry == null || string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? new SqlOSConsentScopeDisplay(scope, scope)
                    : new SqlOSConsentScopeDisplay(scope, entry.DisplayName, entry.Description);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<SqlOSConsentGrantSummary>> ListActiveGrantsForUserAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var grants = await _context.Set<SqlOSConsentGrant>()
            .AsNoTracking()
            .Include(x => x.ClientApplication)
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .OrderBy(x => x.GrantedAt)
            .ToListAsync(cancellationToken);

        return grants
            .Select(x => new SqlOSConsentGrantSummary(
                x.Id,
                x.UserId,
                x.ClientApplicationId,
                x.ClientApplication?.ClientId ?? x.ClientApplicationId,
                x.ClientApplication?.Name ?? x.ClientApplicationId,
                SqlOSScopePolicy.Split(x.Scope),
                x.GrantedAt,
                x.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// Revokes one active grant that belongs to <paramref name="userId"/>. Callers record the
    /// <c>oauth.consent.revoked</c> audit with their own actor semantics.
    /// Concurrent revocations (or a racing scope-union save) fail the UpdatedAt concurrency
    /// token for the loser; the conflicted state is dropped and the row re-read so an
    /// already-revoked grant surfaces the same typed already-revoked failure as the
    /// sequential path instead of a provider concurrency error.
    /// </summary>
    public async Task<SqlOSConsentGrant> RevokeGrantAsync(
        string userId,
        string grantId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RevokeGrantOnceAsync(userId, grantId, reason, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxUpsertAttempts)
            {
                // Detach the stale entries so the retry reloads the winning row: a reload
                // that is already revoked throws the stable already-revoked failure; a
                // reload that is still active (a concurrent scope union won) retries.
                foreach (var entry in ex.Entries)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    private async Task<SqlOSConsentGrant> RevokeGrantOnceAsync(
        string userId,
        string grantId,
        string reason,
        CancellationToken cancellationToken)
    {
        var grant = await _context.Set<SqlOSConsentGrant>()
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.Id == grantId && x.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Consent grant was not found for this user.");
        if (grant.RevokedAt != null)
        {
            throw new InvalidOperationException("Consent grant is already revoked.");
        }

        var now = DateTime.UtcNow;
        grant.RevokedAt = now;
        grant.UpdatedAt = now;
        grant.RevocationReason = reason;
        await _context.SaveChangesAsync(cancellationToken);
        return grant;
    }

    /// <summary>
    /// Marks every active grant for the client as revoked without saving, so callers (the CIMD
    /// security-sensitive-metadata tripwire) can commit the revocation together with their own
    /// changes in a single save.
    /// </summary>
    public static async Task<IReadOnlyList<SqlOSConsentGrant>> MarkGrantsRevokedForClientAsync(
        ISqlOSAuthServerDbContext context,
        string clientApplicationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var grants = await context.Set<SqlOSConsentGrant>()
            .Where(x => x.ClientApplicationId == clientApplicationId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RevokedAt = now;
            grant.UpdatedAt = now;
            grant.RevocationReason = reason;
        }

        return grants;
    }

    public async Task<int> RevokeGrantsForClientAsync(
        string clientApplicationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var grants = await MarkGrantsRevokedForClientAsync(_context, clientApplicationId, reason, cancellationToken);
        if (grants.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return grants.Count;
    }

    private async Task<SqlOSConsentGrant?> FindActiveGrantAsync(
        string userId,
        string clientApplicationId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = _context.Set<SqlOSConsentGrant>()
            .Where(x => x.UserId == userId && x.ClientApplicationId == clientApplicationId && x.RevokedAt == null);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Drops the losing attempt's tracked grant state so the retry reloads the winning row
    /// from the database instead of replaying the stale insert or update. Only this
    /// (user, client) pair's grant entries are detached — callers may have other tracked
    /// entities (for example the authorization request) mid-flow.
    /// </summary>
    private void DetachConflictedGrantEntries(DbUpdateException exception, string userId, string clientApplicationId)
    {
        foreach (var entry in exception.Entries)
        {
            entry.State = EntityState.Detached;
        }

        if (_context is not DbContext db)
        {
            return;
        }

        foreach (var entry in db.ChangeTracker.Entries<SqlOSConsentGrant>()
            .Where(x => x.Entity.UserId == userId
                && x.Entity.ClientApplicationId == clientApplicationId
                && x.State is EntityState.Added or EntityState.Modified)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // Same pattern as SqlOSAuthorizationServerService.IsUniqueConstraintViolation: SQL Server
    // raises 2601 (unique index) or 2627 (unique constraint) for duplicate keys.
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => SqlOSDatabaseErrors.IsUniqueConstraintViolation(exception);
}
