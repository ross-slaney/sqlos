using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Database;

namespace SqlOS.AuthServer.Services;

/// <summary>Host-owned data and product-policy cleanup in the same database transaction.
/// Implementations must use tracked changes on the scoped context and must not perform external I/O.</summary>
public interface ISqlOSIdentityDeletionParticipant
{
    Task BeforeUserDeletionAsync(string userId, CancellationToken cancellationToken);
    Task BeforeOrganizationDeletionAsync(string organizationId, CancellationToken cancellationToken);
}

public sealed record SqlOSIdentityDeletionResult(string Id, DateTime DeletedAt, bool AlreadyDeleted);

/// <summary>Terminal identity deletion. Anonymous identifiers and security audit remain;
/// credentials, sign-in identities and memberships are removed. Trusted hosts authorize their own callers
/// before invoking this service. Operator HTTP routes use the same service and host participants.</summary>
public sealed class SqlOSIdentityDeletionService(ISqlOSAuthServerDbContext context,
    IEnumerable<ISqlOSIdentityDeletionParticipant> participants)
{
    public Task<SqlOSIdentityDeletionResult> DeleteUserAsync(string userId, string actorId, bool confirm,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        RequireConfirmation(confirm, actorId);
        var user = await context.Set<SqlOSUser>().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        if (user.DeletedAt is { } deleted) return new(userId, deleted, true);
        foreach (var participant in participants) await participant.BeforeUserDeletionAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        await SqlOSAuthLifecyclePolicy.RevokeAsync(context, userId, null, "account_deleted", now,
            SqlOSAuthLifecycleRevocationScope.Offboarding, cancellationToken);
        await RemoveAsync<SqlOSEmailOtpChallenge>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSPhoneOtpChallenge>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSUserEmail>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSUserPhoneNumber>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSCredential>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSExternalIdentity>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSUserAuthenticator>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSRecoveryCode>(x => x.UserId == userId, cancellationToken);
        await RemoveAsync<SqlOSMembership>(x => x.UserId == userId, cancellationToken);
        user.IsActive = false;
        user.DeletedAt = now;
        user.DisplayName = "Deleted account";
        user.DefaultEmail = null;
        user.UpdatedAt = now;
        Audit("identity.user.deleted", actorId, userId, null, now);
        await context.SaveChangesAsync(cancellationToken);
        return new SqlOSIdentityDeletionResult(userId, now, false);
    }, cancellationToken);

    public Task<SqlOSIdentityDeletionResult> DeleteOrganizationAsync(string organizationId, string actorId, bool confirm,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        RequireConfirmation(confirm, actorId);
        await SqlOSSsoPortalOrganizationLock.AcquireAsync(context, organizationId, cancellationToken);
        var organization = await context.Set<SqlOSOrganization>().SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found.");
        if (organization.DeletedAt is { } deleted) return new(organizationId, deleted, true);
        foreach (var participant in participants) await participant.BeforeOrganizationDeletionAsync(organizationId, cancellationToken);
        var now = DateTime.UtcNow;
        await SqlOSAuthLifecyclePolicy.RevokeAsync(context, null, organizationId, "organization_deleted", now,
            SqlOSAuthLifecycleRevocationScope.Offboarding, cancellationToken);
        await RemoveAsync<SqlOSMembership>(x => x.OrganizationId == organizationId, cancellationToken);
        await RemoveAsync<SqlOSOrganizationDomain>(x => x.OrganizationId == organizationId, cancellationToken);
        foreach (var connection in await context.Set<SqlOSSsoConnection>().Where(x => x.OrganizationId == organizationId).ToListAsync(cancellationToken))
            connection.IsEnabled = false;
        foreach (var connection in await context.Set<SqlOSScimConnection>().Where(x => x.OrganizationId == organizationId).ToListAsync(cancellationToken))
            connection.IsEnabled = false;
        organization.IsActive = false;
        organization.DeletedAt = now;
        organization.Name = "Deleted organization";
        organization.PrimaryDomain = null;
        organization.Slug = "deleted-" + Guid.NewGuid().ToString("N");
        Audit("identity.organization.deleted", actorId, null, organizationId, now);
        await context.SaveChangesAsync(cancellationToken);
        return new SqlOSIdentityDeletionResult(organizationId, now, false);
    }, cancellationToken);

    public Task RemoveMembershipAsync(string organizationId, string userId, string actorId, bool confirm,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        RequireConfirmation(confirm, actorId);
        await SqlOSSsoPortalOrganizationLock.AcquireAsync(context, organizationId, cancellationToken);
        var membership = await context.Set<SqlOSMembership>().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.UserId == userId, cancellationToken);
        if (membership is null || !membership.IsActive) return true;
        await SqlOSAuthLifecyclePolicy.RevokeAsync(context, userId, organizationId, "membership_removed", DateTime.UtcNow,
            SqlOSAuthLifecycleRevocationScope.Offboarding, cancellationToken);
        membership.IsActive = false;
        Audit("identity.membership.removed", actorId, userId, organizationId, DateTime.UtcNow);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    private async Task RemoveAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class
        => context.Set<T>().RemoveRange(await context.Set<T>().Where(predicate).ToListAsync(ct));

    private void Audit(string kind, string actor, string? user, string? organization, DateTime now) =>
        context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent { Id = Guid.NewGuid().ToString("N"), EventType = kind,
            ActorType = "user", ActorId = actor, UserId = user, OrganizationId = organization, OccurredAt = now });

    private static void RequireConfirmation(bool confirm, string actor)
    {
        if (!confirm) throw new ArgumentException("Explicit confirmation is required.");
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An authenticated actor is required.");
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (!context.Database.IsRelational() || context.Database.CurrentTransaction != null) return await operation();
        var attempt = 0;
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0 && context is DbContext retryContext) retryContext.ChangeTracker.Clear();
            return await AttemptAsync();
        });
        async Task<T> AttemptAsync()
        {
            await using var transaction = await context.Database.BeginTransactionAsync(SqlOSDatabase.ExclusiveWorkIsolationLevel(context.Database), ct);
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        }
    }
}
