using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Pagination;

namespace SqlOS.Fga.Services;

public sealed record SqlOSCustomRoleWrite(string Name, string? Description, IReadOnlyList<string> PermissionKeys);
public sealed record SqlOSCustomRoleView(string Id, string OwnerResourceId, string Name, string? Description,
    long Revision, DateTime? ArchivedAt, IReadOnlyList<string> PermissionKeys);

public sealed record SqlOSCustomRoleGrantView(string Id, string SubjectId, string ResourceId,
    DateTime? EffectiveFrom, DateTime? EffectiveTo);

/// <summary>Stable failure for all role management adapters.</summary>
public sealed class SqlOSCustomRoleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// One management path for applications and the operator dashboard. Definitions use the existing role,
/// role-permission and grant entities; audit and the mutation commit in one database transaction.
/// </summary>
public sealed class SqlOSCustomRoleService(
    ISqlOSFgaDbContext db,
    ISqlOSFgaAuthService auth,
    ISqlOSAuditLogService audit,
    IOptions<SqlOSFgaOptions> options)
{
    private bool operatorRequest;
    // Only the already authenticated dashboard adapter can enter this path. Never supplied in request JSON.
    internal async Task<T> AsOperatorAsync<T>(Func<Task<T>> operation)
    {
        operatorRequest = true;
        try { return await operation(); }
        finally { operatorRequest = false; }
    }

    private SqlOSCustomRoleOptions Policy => options.Value.CustomRoles;

    public Task<SqlOSCustomRoleView> CreateAsync(string actor, string owner, SqlOSCustomRoleWrite request, CancellationToken ct = default)
        => MutateAsync(actor, owner, null, null, "created", async () =>
        {
            var permissions = await PermissionsAsync(request, ct);
            var role = new SqlOSFgaRole
            {
                Id = $"role_{Guid.NewGuid():N}", Key = $"custom_{Guid.NewGuid():N}",
                DefinitionSource = SqlOSFgaRoleSources.Customer, OwnerResourceId = owner,
                Name = request.Name.Trim(), Description = request.Description?.Trim()
            };
            db.Set<SqlOSFgaRole>().Add(role);
            foreach (var permission in permissions)
                db.Set<SqlOSFgaRolePermission>().Add(new() { RoleId = role.Id, PermissionId = permission.Id });
            return role;
        }, ct);

    public Task<SqlOSCustomRoleView> UpdateAsync(string actor, string owner, string id, long revision, SqlOSCustomRoleWrite request, CancellationToken ct = default)
        => MutateAsync(actor, owner, id, revision, "updated", async () =>
        {
            var role = await RoleAsync(owner, id, revision, ct);
            var permissions = await PermissionsAsync(request, ct);
            var links = await db.Set<SqlOSFgaRolePermission>().Where(x => x.RoleId == id).ToListAsync(ct);
            db.Set<SqlOSFgaRolePermission>().RemoveRange(links.Where(x => !permissions.Any(p => p.Id == x.PermissionId)));
            foreach (var permission in permissions.Where(p => !links.Any(x => x.PermissionId == p.Id)))
                db.Set<SqlOSFgaRolePermission>().Add(new() { RoleId = id, PermissionId = permission.Id });
            role.Name = request.Name.Trim();
            role.Description = request.Description?.Trim();
            role.Revision++;
            return role;
        }, ct);

    public Task<SqlOSCustomRoleView> RetireAsync(string actor, string owner, string id, long revision, CancellationToken ct = default)
        => MutateAsync(actor, owner, id, revision, "retired", async () =>
        {
            var role = await RoleAsync(owner, id, revision, ct);
            // Keep the historical definition. The normal evaluator also rejects archived roles,
            // so an unsupported/raw concurrent insert can never resurrect retired authority.
            var grants = db.Set<SqlOSFgaGrant>().Where(x => x.RoleId == id);
            if (db.Database.IsRelational()) await grants.ExecuteDeleteAsync(ct);
            else db.Set<SqlOSFgaGrant>().RemoveRange(await grants.ToListAsync(ct));
            role.ArchivedAt = DateTime.UtcNow;
            role.Revision++;
            return role;
        }, ct);

    public Task<SqlOSCustomRoleView> GrantAsync(string actor, string owner, string id, long revision, string subject, string resource, CancellationToken ct = default)
        => MutateAsync(actor, owner, id, revision, "granted", async () =>
        {
            var role = await RoleAsync(owner, id, revision, ct);
            if (!await Policy.IsEligibleRecipientAsync!(db, owner, subject, ct))
                throw Error("recipient_denied", "This principal cannot receive roles in this organization.");
            if (await new SqlOSFgaSubtreeResolver(db.Set<SqlOSFgaResource>(), options.Value.MaxResourceHierarchyDepth)
                .CheckAsync(resource, owner, ct) != SqlOSFgaSubtreeMembership.Within)
                throw Error("outside_owner", "The grant must stay inside the role owner's resource subtree.");
            if (!await db.Set<SqlOSFgaGrant>().AnyAsync(g => g.RoleId == id && g.SubjectId == subject && g.ResourceId == resource, ct))
                db.Set<SqlOSFgaGrant>().Add(new() { Id = $"grant_{Guid.NewGuid():N}", SubjectId = subject, ResourceId = resource, RoleId = id });
            role.Revision++;
            return role;
        }, ct, subject, resource);

    public Task<SqlOSCustomRoleView> RevokeAsync(string actor, string owner, string id, long revision, string subject, string resource, CancellationToken ct = default)
        => MutateAsync(actor, owner, id, revision, "revoked", async () =>
        {
            var role = await RoleAsync(owner, id, revision, ct);
            var grants = await db.Set<SqlOSFgaGrant>().Where(g => g.SubjectId == subject && g.ResourceId == resource && g.RoleId == id).ToListAsync(ct);
            db.Set<SqlOSFgaGrant>().RemoveRange(grants);
            role.Revision++;
            return role;
        }, ct, subject, resource);

    public async Task<SqlOSCustomRoleView> GetAsync(string actor, string owner, string id, CancellationToken ct = default)
    {
        await AuthorizeAsync(actor, owner, ct);
        var role = await db.Set<SqlOSFgaRole>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerResourceId == owner, ct)
            ?? throw Error("not_found", "Role not found in this organization.");
        return await ViewAsync(role, ct);
    }

    public async Task<SqlOSCursorPage<SqlOSCustomRoleView>> ListAsync(string actor, string owner,
        string? cursor = null, int pageSize = 25, CancellationToken ct = default)
    {
        await AuthorizeAsync(actor, owner, ct);
        var page = await SqlOSCursorPagination.ToPageAsync(
            db.Set<SqlOSFgaRole>().AsNoTracking().Where(x => x.OwnerResourceId == owner && x.DefinitionSource == SqlOSFgaRoleSources.Customer),
            SqlOSKeyset<SqlOSFgaRole>.Create().Ascending(x => x.Id), "custom_roles:id", SqlOSCursorCodec.Fingerprint(owner),
            cursor, SqlOSCursorPagination.NormalizePageSize(pageSize), ct);
        var ids = page.Data.Select(x => x.Id).ToArray();
        var links = await db.Set<SqlOSFgaRolePermission>().Where(x => ids.Contains(x.RoleId))
            .Select(x => new { x.RoleId, x.Permission!.Key }).ToListAsync(ct);
        return new(page.Data.Select(role => new SqlOSCustomRoleView(role.Id, owner, role.Name,
            role.Description, role.Revision, role.ArchivedAt,
            links.Where(x => x.RoleId == role.Id).Select(x => x.Key).OrderBy(x => x).ToArray())).ToArray(),
            page.PageSize, page.NextCursor);
    }

    public async Task<SqlOSCursorPage<SqlOSCustomRoleGrantView>> ListGrantsAsync(string actor, string owner,
        string id, string? cursor = null, int pageSize = 25, CancellationToken ct = default)
    {
        await GetAsync(actor, owner, id, ct);
        var page = await SqlOSCursorPagination.ToPageAsync(
            db.Set<SqlOSFgaGrant>().AsNoTracking().Where(x => x.RoleId == id),
            SqlOSKeyset<SqlOSFgaGrant>.Create().Ascending(x => x.Id), "custom_role_grants:id",
            SqlOSCursorCodec.Fingerprint(owner, id), cursor, SqlOSCursorPagination.NormalizePageSize(pageSize), ct);
        return new(page.Data.Select(grant => new SqlOSCustomRoleGrantView(grant.Id, grant.SubjectId,
            grant.ResourceId, grant.EffectiveFrom, grant.EffectiveTo)).ToArray(), page.PageSize, page.NextCursor);
    }

    /// <summary>
    /// Serialize host membership/offboarding changes with role administration for this owner.
    /// Use the same scoped DbContext, and save staged host changes before calling a nested role operation.
    /// </summary>
    public Task<T> WithOwnerTransactionAsync<T>(string actor, string owner, Func<Task<T>> operation, CancellationToken ct = default)
        => InTransactionAsync(actor, owner, operation, ct);

    private Task<SqlOSCustomRoleView> MutateAsync(string actor, string owner, string? id, long? revision,
        string action, Func<Task<SqlOSFgaRole>> change, CancellationToken ct, string? subject = null, string? resource = null)
        => InTransactionAsync(actor, owner, async () =>
        {
            var before = id == null ? null : await ViewAsync(await RoleAsync(owner, id, revision!.Value, ct), ct);
            var role = await change();
            await db.SaveChangesAsync(ct);
            var after = await ViewAsync(role, ct);
            await audit.RecordAsync(new SqlOSAuditLogRecordRequest(
                Action: $"fga.custom_role.{action}", Source: "fga",
                Actor: new SqlOSAuditActor(operatorRequest ? "operator" : "principal", actor),
                Targets: [new("resource", owner), new("role", role.Id, role.Name)],
                Metadata: new Dictionary<string, object?> { ["before"] = before, ["after"] = after, ["subjectId"] = subject, ["resourceId"] = resource }), ct);
            return after;
        }, ct);

    private async Task<T> InTransactionAsync<T>(string actor, string owner, Func<Task<T>> operation, CancellationToken ct)
    {
        if (db is DbContext pending && pending.ChangeTracker.HasChanges())
            throw Error("pending_changes", "Save or discard staged changes before administering roles.");
        var outer = db.Database.CurrentTransaction;
        if (outer != null && !outer.SupportsSavepoints)
            throw Error("savepoint_required", "An enclosing role-administration transaction must support savepoints.");
        await using var owned = db.Database.IsRelational() && outer == null ? await db.Database.BeginTransactionAsync(ct) : null;
        var transaction = owned ?? outer;
        var savepoint = outer == null ? null : Guid.NewGuid().ToString("N");
        if (transaction != null)
            await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(db.Database, $"SqlOS:CustomRoles:{owner}",
                TimeSpan.FromSeconds(30), "Custom role administration is busy; retry.", ct);
        if (savepoint != null) await outer!.CreateSavepointAsync(savepoint, ct);
        try
        {
            await AuthorizeAsync(actor, owner, ct);
            var result = await operation();
            await db.SaveChangesAsync(ct);
            if (owned != null) await owned.CommitAsync(ct);
            if (savepoint != null) await outer!.ReleaseSavepointAsync(savepoint, ct);
            return result;
        }
        catch
        {
            if (owned != null) await owned.RollbackAsync(CancellationToken.None);
            if (savepoint != null) await outer!.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            if (db is DbContext context) context.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task AuthorizeAsync(string actor, string owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Policy.ManagePermission) || Policy.IsEligibleRecipientAsync == null || Policy.OwnerResourceTypes.Count == 0)
            throw Error("not_configured", "Customer role administration is not configured by the application.");
        var boundary = await db.Set<SqlOSFgaResource>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == owner && x.IsActive, ct);
        if (boundary == null || !Policy.OwnerResourceTypes.Contains(boundary.ResourceTypeId) || owner == options.Value.RootResourceId)
            throw Error("owner_invalid", "The owner must be an eligible organization resource.");
        var permission = await db.Set<SqlOSFgaPermission>().AsNoTracking().SingleOrDefaultAsync(x => x.Key == Policy.ManagePermission, ct);
        if (permission == null || permission.AllowInCustomRoles)
            throw Error("policy_invalid", "The management permission must exist and must not be selectable in customer roles.");
        if (!operatorRequest && !(await auth.CheckAccessAsync(actor, Policy.ManagePermission, owner)).Allowed)
            throw Error("forbidden", "You cannot manage roles in this organization.");
    }

    private async Task<SqlOSFgaRole> RoleAsync(string owner, string id, long revision, CancellationToken ct)
    {
        var role = await db.Set<SqlOSFgaRole>().SingleOrDefaultAsync(x => x.Id == id && x.OwnerResourceId == owner, ct)
            ?? throw Error("not_found", "Role not found in this organization.");
        // A long-lived caller may have tracked an older revision before this transaction.
        if (db is DbContext context) await context.Entry(role).ReloadAsync(ct);
        if (role.OwnerResourceId != owner) throw Error("not_found", "Role not found in this organization.");
        if (role.DefinitionSource != SqlOSFgaRoleSources.Customer) throw Error("code_owned", "Change application roles in source control.");
        if (role.ArchivedAt != null) throw Error("archived", "This role has been retired.");
        if (role.Revision != revision) throw Error("stale_revision", "The role changed. Reload before saving.");
        return role;
    }

    private async Task<List<SqlOSFgaPermission>> PermissionsAsync(SqlOSCustomRoleWrite request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120 || request.Description?.Length > 2000)
            throw Error("invalid_definition", "Give the role a name of at most 120 characters and a description of at most 2000.");
        if (request.PermissionKeys == null || request.PermissionKeys.Count > 100)
            throw Error("invalid_permissions", "Choose at most 100 permissions.");
        var keys = request.PermissionKeys.Distinct(StringComparer.Ordinal).ToArray();
        var permissions = await db.Set<SqlOSFgaPermission>().Where(x => keys.Contains(x.Key) && x.AllowInCustomRoles).ToListAsync(ct);
        if (permissions.Count != keys.Length || keys.Contains(Policy.ManagePermission, StringComparer.Ordinal))
            throw Error("permission_denied", "A selected permission is not available for customer roles.");
        return permissions;
    }

    private async Task<SqlOSCustomRoleView> ViewAsync(SqlOSFgaRole role, CancellationToken ct)
        => new(role.Id, role.OwnerResourceId!, role.Name, role.Description, role.Revision, role.ArchivedAt,
            await db.Set<SqlOSFgaRolePermission>().Where(x => x.RoleId == role.Id).OrderBy(x => x.Permission!.Key).Select(x => x.Permission!.Key).ToListAsync(ct));

    private static SqlOSCustomRoleException Error(string code, string message) => new(code, message);
}
