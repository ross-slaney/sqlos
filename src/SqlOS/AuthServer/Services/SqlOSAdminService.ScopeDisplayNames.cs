using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed partial class SqlOSAdminService
{
    /// <summary>
    /// Reconciles code-seeded consent-screen scope display names. The scope string is the
    /// stable configuration source key; dashboard-owned rows are never overwritten, and
    /// code-owned rows missing from the seed set are marked orphaned rather than deleted.
    /// </summary>
    public async Task UpsertSeededScopeDisplayNamesAsync(CancellationToken cancellationToken = default)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            await UpsertSeededScopeDisplayNamesCoreAsync(cancellationToken);
            return;
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && _context is DbContext retryContext)
            {
                retryContext.ChangeTracker.Clear();
            }
            await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            if (string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
            {
                await _context.Database.ExecuteSqlRawAsync("""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock
                        @Resource = N'SqlOS:ScopeDisplayNameSeedReconciliation',
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction',
                        @LockTimeout = 30000;
                    IF @result < 0 THROW 51000, 'Could not acquire the SqlOS scope display name seed reconciliation lock.', 1;
                    """, cancellationToken);
            }
            await UpsertSeededScopeDisplayNamesCoreAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task UpsertSeededScopeDisplayNamesCoreAsync(CancellationToken cancellationToken)
    {
        var seeds = _options.ScopeDisplaySeeds;
        var sourceKeys = seeds
            .Select(x => x.Scope.Trim())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var auditOutcomes = new List<(string ResourceId, string SourceKey, string Outcome, string? Fingerprint)>();
        var orphans = await _context.Set<SqlOSScopeDisplayName>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code
                && x.ConfigurationSourceKey != null
                && !sourceKeys.Contains(x.ConfigurationSourceKey))
            .ToListAsync(cancellationToken);
        foreach (var orphan in orphans)
        {
            if (orphan.ConfigurationOrphanedAt == null)
            {
                orphan.ConfigurationOrphanedAt = now;
                orphan.UpdatedAt = now;
                auditOutcomes.Add((orphan.Id, orphan.ConfigurationSourceKey!, "orphaned", orphan.ConfigurationFingerprint));
            }
        }

        if (seeds.Count == 0 && orphans.Count == 0)
        {
            return;
        }

        // SqlOSOptionsValidator rejects duplicate seed scopes at configuration time; this is
        // a defensive batch de-dup (first seed wins) so a duplicate that slips through can
        // never queue two inserts for the same unique Scope key in one SaveChanges.
        var reconciledScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            var scope = seed.Scope.Trim();
            if (scope.Length == 0)
            {
                throw new InvalidOperationException("Seeded scope display names require a scope.");
            }

            if (string.IsNullOrWhiteSpace(seed.DisplayName))
            {
                throw new InvalidOperationException($"Seeded scope display name for '{scope}' requires a display name.");
            }

            if (!reconciledScopes.Add(scope))
            {
                continue;
            }

            var displayName = seed.DisplayName.Trim();
            var description = string.IsNullOrWhiteSpace(seed.Description) ? null : seed.Description.Trim();
            var fingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new { Scope = scope, DisplayName = displayName, Description = description });
            var existing = await _context.Set<SqlOSScopeDisplayName>()
                .FirstOrDefaultAsync(x => x.Scope == scope, cancellationToken);
            if (existing == null)
            {
                _context.Set<SqlOSScopeDisplayName>().Add(new SqlOSScopeDisplayName
                {
                    Id = _cryptoService.GenerateId("sdn"),
                    Scope = scope,
                    DisplayName = displayName,
                    Description = description,
                    ConfigurationOwner = SqlOSConfigurationOwners.Code,
                    ConfigurationSourceKey = scope,
                    ConfigurationFingerprint = fingerprint,
                    LastReconciledAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                auditOutcomes.Add((scope, scope, "created", fingerprint));
                continue;
            }

            SqlOSConfigurationOwnershipPolicy.EnsureCodeOwnership(
                existing.ConfigurationOwner,
                existing.ConfigurationSourceKey,
                scope,
                $"Scope display name '{scope}'");
            var outcome = existing.ConfigurationFingerprint == fingerprint && existing.ConfigurationOrphanedAt == null
                ? null
                : "updated";
            existing.DisplayName = displayName;
            existing.Description = description;
            existing.ConfigurationFingerprint = fingerprint;
            existing.LastReconciledAt = now;
            existing.ConfigurationOrphanedAt = null;
            existing.UpdatedAt = now;
            if (outcome != null)
            {
                auditOutcomes.Add((existing.Id, scope, outcome, fingerprint));
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        foreach (var audit in auditOutcomes)
        {
            await RecordAuditAsync("configuration.reconciled", "system", "startup", data: new { resourceType = "scope_display_name", resourceId = audit.ResourceId, owner = SqlOSConfigurationOwners.Code, sourceKey = audit.SourceKey, outcome = audit.Outcome, fingerprint = audit.Fingerprint }, cancellationToken: cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SqlOSScopeDisplayName>> ListScopeDisplayNamesAsync(CancellationToken cancellationToken = default)
        => await _context.Set<SqlOSScopeDisplayName>()
            .AsNoTracking()
            .OrderBy(x => x.Scope)
            .ToListAsync(cancellationToken);

    public async Task<SqlOSScopeDisplayName> CreateScopeDisplayNameAsync(
        SqlOSCreateScopeDisplayNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = NormalizeRequiredScopeDisplayField(request.Scope, "Scope", ScopeDisplayScopeMaxLength);
        var displayName = NormalizeRequiredScopeDisplayField(request.DisplayName, "Display name", ScopeDisplayNameMaxLength);
        var description = NormalizeOptionalScopeDisplayDescription(request.Description);
        if (await _context.Set<SqlOSScopeDisplayName>().AnyAsync(x => x.Scope == scope, cancellationToken))
        {
            throw new InvalidOperationException($"A display name for scope '{scope}' already exists.");
        }

        var now = DateTime.UtcNow;
        var entry = new SqlOSScopeDisplayName
        {
            Id = _cryptoService.GenerateId("sdn"),
            Scope = scope,
            DisplayName = displayName,
            Description = description,
            ConfigurationOwner = SqlOSConfigurationOwners.Dashboard,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.Set<SqlOSScopeDisplayName>().Add(entry);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlOSSignupOrchestration.IsUniqueConstraintViolation(ex))
        {
            // Two concurrent creators can both pass the AnyAsync pre-check; the unique Scope
            // index rejects the loser. Surface the same typed duplicate failure as the
            // sequential path instead of a provider DbUpdateException.
            throw new InvalidOperationException($"A display name for scope '{scope}' already exists.");
        }
        await RecordAuditAsync(
            "scope_display_name.created",
            "admin",
            "dashboard",
            data: new { scope = entry.Scope, display_name = entry.DisplayName },
            cancellationToken: cancellationToken);
        return entry;
    }

    public async Task<SqlOSScopeDisplayName> UpdateScopeDisplayNameAsync(
        string id,
        SqlOSUpdateScopeDisplayNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetRequiredScopeDisplayNameAsync(id, cancellationToken);
        SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(
            entry.ConfigurationOwner,
            $"Scope display name '{entry.Scope}'");
        entry.DisplayName = NormalizeRequiredScopeDisplayField(request.DisplayName, "Display name", ScopeDisplayNameMaxLength);
        entry.Description = NormalizeOptionalScopeDisplayDescription(request.Description);
        entry.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "scope_display_name.updated",
            "admin",
            "dashboard",
            data: new { scope = entry.Scope, display_name = entry.DisplayName },
            cancellationToken: cancellationToken);
        return entry;
    }

    public async Task DeleteScopeDisplayNameAsync(string id, CancellationToken cancellationToken = default)
    {
        var entry = await GetRequiredScopeDisplayNameAsync(id, cancellationToken);
        // A live code-owned row must be changed in source control, but an orphaned one
        // (its seed was removed) has no source-control home left: deleting it here is the
        // supported cleanup, matching the dashboard's orphan guidance. Runtime resolution
        // already ignores orphaned rows, so the delete only retires the stale record.
        if (entry.ConfigurationOrphanedAt == null)
        {
            SqlOSConfigurationOwnershipPolicy.EnsureDashboardEditable(
                entry.ConfigurationOwner,
                $"Scope display name '{entry.Scope}'");
        }
        _context.Set<SqlOSScopeDisplayName>().Remove(entry);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync(
            "scope_display_name.deleted",
            "admin",
            "dashboard",
            data: new { scope = entry.Scope },
            cancellationToken: cancellationToken);
    }

    private async Task<SqlOSScopeDisplayName> GetRequiredScopeDisplayNameAsync(string id, CancellationToken cancellationToken)
        => await _context.Set<SqlOSScopeDisplayName>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Scope display name was not found.");

    // Match the SqlOSScopeDisplayNames column lengths so oversized input surfaces as the
    // documented validation failure instead of a provider truncation error.
    private const int ScopeDisplayScopeMaxLength = 200;
    private const int ScopeDisplayNameMaxLength = 200;
    private const int ScopeDisplayDescriptionMaxLength = 1000;

    private static string NormalizeRequiredScopeDisplayField(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{field} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new InvalidOperationException($"{field} cannot exceed {maxLength} characters.");
        }

        return trimmed;
    }

    private static string? NormalizeOptionalScopeDisplayDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > ScopeDisplayDescriptionMaxLength)
        {
            throw new InvalidOperationException($"Description cannot exceed {ScopeDisplayDescriptionMaxLength} characters.");
        }

        return trimmed;
    }
}
