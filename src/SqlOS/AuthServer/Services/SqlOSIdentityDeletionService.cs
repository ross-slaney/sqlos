using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Database;
using SqlOS.Fga.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>Host-owned data and product-policy cleanup in the same database transaction.
/// Implementations must use tracked changes on the scoped context and must not perform external I/O.</summary>
public interface ISqlOSIdentityDeletionParticipant
{
    Task BeforeUserDeletionAsync(string userId, CancellationToken cancellationToken);
    Task BeforeOrganizationDeletionAsync(string organizationId, CancellationToken cancellationToken);
}

public sealed record SqlOSIdentityDeletionResult(string Id, DateTime DeletedAt, bool AlreadyDeleted);

/// <summary>Soft identity deletion. Identity data, credentials, memberships and audit remain;
/// deleted identities cannot sign in, and memberships and sessions are deactivated. Trusted hosts authorize their own callers
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
        foreach (var membership in await context.Set<SqlOSMembership>().Where(x => x.UserId == userId).ToListAsync(cancellationToken))
            membership.IsActive = false;
        user.IsActive = false;
        user.DeletedAt = now;
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
        foreach (var invitation in await context.Set<SqlOSInvitation>().Where(x => x.OrganizationId == organizationId && x.AcceptedAt == null && x.RevokedAt == null).ToListAsync(cancellationToken))
            invitation.RevokedAt = now;
        if (await HasServiceAccountTableAsync(cancellationToken))
        {
            var accounts = await context.Set<SqlOSFgaServiceAccount>().Where(x => x.Subject!.OrganizationId == organizationId).ToListAsync(cancellationToken);
            var clientIds = accounts.Select(x => x.ClientId).ToList();
            foreach (var account in accounts) { account.ExpiresAt = now; account.UpdatedAt = now; }
            foreach (var client in await context.Set<SqlOSClientApplication>().Where(x => clientIds.Contains(x.ClientId)).ToListAsync(cancellationToken))
            { client.IsActive = false; client.DisabledAt = now; client.DisabledReason = "organization_deleted"; }
        }
        foreach (var membership in await context.Set<SqlOSMembership>().Where(x => x.OrganizationId == organizationId).ToListAsync(cancellationToken))
            membership.IsActive = false;
        foreach (var connection in await context.Set<SqlOSSsoConnection>().Where(x => x.OrganizationId == organizationId).ToListAsync(cancellationToken))
            connection.IsEnabled = false;
        foreach (var connection in await context.Set<SqlOSScimConnection>().Where(x => x.OrganizationId == organizationId).ToListAsync(cancellationToken))
            connection.IsEnabled = false;
        organization.IsActive = false;
        organization.DeletedAt = now;
        Audit("identity.organization.deleted", actorId, null, organizationId, now);
        await context.SaveChangesAsync(cancellationToken);
        return new SqlOSIdentityDeletionResult(organizationId, now, false);
    }, cancellationToken);

    public Task RemoveMembershipAsync(string organizationId, string userId, string actorId, bool confirm,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task>? prepare = null) => RunAsync(async () =>
    {
        RequireConfirmation(confirm, actorId);
        await SqlOSSsoPortalOrganizationLock.AcquireAsync(context, organizationId, cancellationToken);
        var membership = await context.Set<SqlOSMembership>().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.UserId == userId, cancellationToken);
        if (membership is null || !membership.IsActive) return true;
        // Host ownership handoff runs under this organization lock and the same transaction.
        // Hosts may then acquire their own role lock without reversing the lock order.
        if (prepare != null) await prepare(cancellationToken);
        await SqlOSAuthLifecyclePolicy.RevokeAsync(context, userId, organizationId, "membership_removed", DateTime.UtcNow,
            SqlOSAuthLifecycleRevocationScope.Offboarding, cancellationToken);
        membership.IsActive = false;
        Audit("identity.membership.removed", actorId, userId, organizationId, DateTime.UtcNow);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }, cancellationToken);

    private async Task<bool> HasServiceAccountTableAsync(CancellationToken ct)
    {
        if (context is not DbContext db || db.Model.FindEntityType(typeof(SqlOSFgaServiceAccount)) is not { } entity) return false;
        if (!db.Database.IsRelational()) return true;
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = SqlOSDatabase.Resolve(db.Database).BuildTableExistsSql(entity.GetSchema(), entity.GetTableName()!);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private void Audit(string kind, string actor, string? user, string? organization, DateTime now) =>
        context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent { Id = Guid.NewGuid().ToString("N"), EventType = kind,
            ActorType = actor == "operator" ? "operator" : "user", ActorId = actor, UserId = user, OrganizationId = organization, OccurredAt = now });

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
