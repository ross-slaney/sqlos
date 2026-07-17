using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;

namespace SqlOS.AuthServer.Services;

public sealed partial class SqlOSAdminService
{
    private async Task ApplySeededApplicationAccessModeAsync(
        SqlOSClientApplication client,
        string accessMode,
        bool seedIsActive,
        CancellationToken cancellationToken)
    {
        var previousMode = NormalizeAccessMode(client.AccessMode);
        client.AccessMode = accessMode;
        if (accessMode == SqlOSApplicationAccessModes.Disabled)
        {
            client.IsActive = false;
            client.DisabledAt ??= DateTime.UtcNow;
            client.DisabledReason ??= "application_access_disabled";
            if (previousMode != SqlOSApplicationAccessModes.Disabled)
            {
                await RevokeClientSessionsInternalAsync(client.Id, "application_access_disabled", cancellationToken);
            }
        }
        else if (previousMode == SqlOSApplicationAccessModes.Disabled
            && string.Equals(client.DisabledReason, "application_access_disabled", StringComparison.Ordinal))
        {
            client.DisabledAt = null;
            client.DisabledReason = null;
            client.IsActive = seedIsActive;
        }
    }

    private async Task ReconcileSeededApplicationAssignmentsAsync(
        IReadOnlyCollection<SqlOSClientSeedOptions> clientSeeds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var desired = new List<NormalizedSeedAssignment>();
        foreach (var clientSeed in clientSeeds)
        {
            var client = await _context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == clientSeed.ClientId, cancellationToken);
            var accessMode = NormalizeAccessMode(client.AccessMode);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);

            foreach (var seed in clientSeed.Assignments)
            {
                var key = RequireBounded(seed.Key, "Application assignment seed key", 160);
                if (!keys.Add(key))
                {
                    throw new InvalidOperationException($"Client '{client.ClientId}' contains duplicate application assignment seed key '{key}'.");
                }

                var organizationId = await ResolveSeedOrganizationIdAsync(seed.OrganizationIdOrSlug, cancellationToken);
                var request = NormalizeAssignmentRequest(new SqlOSCreateApplicationAssignmentRequest(
                    seed.PrincipalType,
                    seed.PrincipalId,
                    organizationId,
                    seed.RoleKey,
                    seed.Access,
                    seed.Description));
                ValidateAssignmentMode(accessMode, request);
                await ValidateAssignmentPrincipalAsync(request, cancellationToken);

                var target = $"{request.PrincipalType}\u001f{request.PrincipalId}\u001f{request.OrganizationId}\u001f{request.RoleKey}\u001f{request.Access}";
                if (!targets.Add(target))
                {
                    throw new InvalidOperationException($"Client '{client.ClientId}' contains duplicate seeded assignment targets. Give each effective assignment only one stable key.");
                }

                var fingerprint = SqlOSConfigurationOwnershipPolicy.Fingerprint(new
                {
                    request.PrincipalType,
                    request.PrincipalId,
                    request.OrganizationId,
                    request.RoleKey,
                    request.Access,
                    request.Reason
                });
                desired.Add(new NormalizedSeedAssignment(client, key, request, fingerprint));
            }
        }

        var desiredKeys = desired.Select(x => $"{x.Client.Id}\u001f{x.Key}").ToHashSet(StringComparer.Ordinal);
        var codeOwned = await _context.Set<SqlOSApplicationAssignment>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code && x.ConfigurationSourceKey != null)
            .ToListAsync(cancellationToken);
        var audit = new List<(SqlOSApplicationAssignment Assignment, string Outcome)>();

        foreach (var orphan in codeOwned.Where(x => !desiredKeys.Contains($"{x.ClientApplicationId}\u001f{x.ConfigurationSourceKey}")))
        {
            if (orphan.RevokedAt == null || orphan.ConfigurationOrphanedAt == null)
            {
                orphan.RevokedAt ??= now;
                orphan.RevokedByActorType = "system";
                orphan.RevokedByActorId = "startup";
                orphan.ConfigurationOrphanedAt = now;
                orphan.LastReconciledAt = now;
                audit.Add((orphan, "revoked"));
            }
        }

        foreach (var item in desired)
        {
            var assignment = codeOwned.SingleOrDefault(x =>
                x.ClientApplicationId == item.Client.Id
                && string.Equals(x.ConfigurationSourceKey, item.Key, StringComparison.Ordinal));
            var outcome = assignment == null ? "created" : assignment.ConfigurationFingerprint == item.Fingerprint && assignment.RevokedAt == null ? null : "updated";
            if (assignment == null)
            {
                assignment = new SqlOSApplicationAssignment
                {
                    Id = _cryptoService.GenerateId("asa"),
                    ClientApplicationId = item.Client.Id,
                    ConfigurationOwner = SqlOSConfigurationOwners.Code,
                    ConfigurationSourceKey = item.Key,
                    CreatedAt = now,
                    CreatedByActorType = "system",
                    CreatedByActorId = "startup"
                };
                _context.Set<SqlOSApplicationAssignment>().Add(assignment);
            }

            assignment.OrganizationId = item.Request.OrganizationId;
            assignment.PrincipalType = item.Request.PrincipalType;
            assignment.PrincipalId = item.Request.PrincipalId;
            assignment.RoleKey = item.Request.RoleKey;
            assignment.Access = item.Request.Access;
            assignment.Reason = item.Request.Reason;
            assignment.ConfigurationFingerprint = item.Fingerprint;
            assignment.LastReconciledAt = now;
            assignment.ConfigurationOrphanedAt = null;
            assignment.RevokedAt = null;
            assignment.RevokedByActorType = null;
            assignment.RevokedByActorId = null;
            if (outcome != null) audit.Add((assignment, outcome));
        }

        await _context.SaveChangesAsync(cancellationToken);
        foreach (var (assignment, outcome) in audit)
        {
            await RecordAuditAsync(
                "configuration.reconciled",
                "system",
                "startup",
                organizationId: assignment.OrganizationId,
                data: new
                {
                    resourceType = "application_assignment",
                    resourceId = assignment.Id,
                    owner = SqlOSConfigurationOwners.Code,
                    sourceKey = assignment.ConfigurationSourceKey,
                    outcome,
                    fingerprint = assignment.ConfigurationFingerprint,
                    clientApplicationId = assignment.ClientApplicationId
                },
                cancellationToken: cancellationToken);
        }
    }

    private async Task<string?> ResolveSeedOrganizationIdAsync(string? idOrSlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug)) return null;
        var value = idOrSlug.Trim();
        var organization = await _context.Set<SqlOSOrganization>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == value || x.Slug == value, cancellationToken);
        if (organization == null || !organization.IsActive)
        {
            throw new InvalidOperationException($"Seeded application assignment organization '{value}' was not found or is inactive.");
        }
        return organization.Id;
    }

    private async Task ValidateAssignmentPrincipalAsync(NormalizedAssignmentRequest request, CancellationToken cancellationToken)
    {
        if (request.OrganizationId != null)
        {
            var organizationActive = await _context.Set<SqlOSOrganization>()
                .AnyAsync(x => x.Id == request.OrganizationId && x.IsActive, cancellationToken);
            if (!organizationActive) throw new InvalidOperationException("Application assignments require an active organization.");
        }

        switch (request.PrincipalType)
        {
            case SqlOSApplicationAssignmentPrincipalTypes.User:
                var userActive = await _context.Set<SqlOSUser>().AnyAsync(x => x.Id == request.PrincipalId && x.IsActive, cancellationToken);
                if (!userActive) throw new InvalidOperationException($"Application assignment user '{request.PrincipalId}' was not found or is inactive.");
                if (request.OrganizationId != null)
                {
                    var membershipActive = await _context.Set<SqlOSMembership>().AnyAsync(x => x.UserId == request.PrincipalId && x.OrganizationId == request.OrganizationId && x.IsActive, cancellationToken);
                    if (!membershipActive) throw new InvalidOperationException("Application assignment user is not active in the selected organization.");
                }
                break;
            case SqlOSApplicationAssignmentPrincipalTypes.Group:
                var group = await _context.Set<SqlOSFgaUserGroup>().AsNoTracking().Include(x => x.Subject).SingleOrDefaultAsync(x => x.Id == request.PrincipalId, cancellationToken);
                if (group == null || !group.IsActive) throw new InvalidOperationException($"Application assignment group '{request.PrincipalId}' was not found or is inactive.");
                EnsureSubjectOrganization(group.Subject?.OrganizationId, request.OrganizationId, "group");
                break;
            case SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount:
                var serviceAccount = await _context.Set<SqlOSFgaServiceAccount>().AsNoTracking().Include(x => x.Subject).SingleOrDefaultAsync(x => x.Id == request.PrincipalId, cancellationToken);
                if (serviceAccount == null || serviceAccount.ExpiresAt <= DateTime.UtcNow) throw new InvalidOperationException($"Application assignment service account '{request.PrincipalId}' was not found or is expired.");
                EnsureSubjectOrganization(serviceAccount.Subject?.OrganizationId, request.OrganizationId, "service account");
                break;
            case SqlOSApplicationAssignmentPrincipalTypes.Agent:
                var agent = await _context.Set<SqlOSFgaAgent>().AsNoTracking().Include(x => x.Subject).SingleOrDefaultAsync(x => x.Id == request.PrincipalId, cancellationToken);
                if (agent == null) throw new InvalidOperationException($"Application assignment agent '{request.PrincipalId}' was not found.");
                EnsureSubjectOrganization(agent.Subject?.OrganizationId, request.OrganizationId, "agent");
                break;
        }
    }

    private static void EnsureSubjectOrganization(string? subjectOrganizationId, string? requestedOrganizationId, string principal)
    {
        if (requestedOrganizationId != null && !string.Equals(subjectOrganizationId, requestedOrganizationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Application assignment {principal} belongs to a different organization.");
        }
    }

    private static void ValidateAssignmentMode(string accessMode, NormalizedAssignmentRequest request)
    {
        if (accessMode == SqlOSApplicationAccessModes.Disabled)
        {
            throw new InvalidOperationException("Disabled applications cannot declare active access assignments.");
        }
        if (accessMode == SqlOSApplicationAccessModes.SelectedOrganizations
            && request.PrincipalType != SqlOSApplicationAssignmentPrincipalTypes.Organization)
        {
            throw new InvalidOperationException("selected_organizations applications may only declare organization assignments.");
        }
    }

    private static string RequireBounded(string? value, string name, int maxLength)
    {
        var normalized = RequireText(value, name);
        if (normalized.Length > maxLength) throw new InvalidOperationException($"{name} must be {maxLength} characters or fewer.");
        return normalized;
    }

    private sealed record NormalizedSeedAssignment(
        SqlOSClientApplication Client,
        string Key,
        NormalizedAssignmentRequest Request,
        string Fingerprint);
}
