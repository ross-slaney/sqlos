using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.AuditLogs.Projections;

/// <summary>
/// The audit rows of the FGA write model: source <c>fga</c>, the actor that made the change, the
/// FGA records it touched as targets, and the request's context.
/// </summary>
internal static class FgaAuditProjections
{
    public static SqlOSAuditProjectionBuilder AddFgaEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Unaudited<FgaGrantDescribed>()
            .Unaudited<FgaSubjectDescribed>()
            .Unaudited<FgaResourceCreated>()
            .Unaudited<FgaResourceDescribed>()
            .Unaudited<FgaResourceTypeDefined>()
            .Unaudited<FgaPermissionDefined>()
            .AddGrantEvents()
            .AddSubjectEvents()
            .AddResourceAndRoleEvents();

    private static SqlOSAuditProjectionBuilder AddGrantEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Audit<FgaGrantCreated>(static (created, context) => Row(
                "fga.grant.created",
                created.Actor,
                context,
                [Grant(created.GrantId), Subject(created.SubjectId), Resource(created.ResourceId), Role(created.RoleId)],
                new
                {
                    grantId = created.GrantId,
                    subjectId = created.SubjectId,
                    roleId = created.RoleId,
                    resourceId = created.ResourceId,
                    effectiveFrom = created.EffectiveFrom,
                    effectiveTo = created.EffectiveTo
                },
                created.OrganizationId))
            .Audit<FgaGrantRevoked>(static (revoked, context) => Row(
                "fga.grant.revoked",
                revoked.Actor,
                context,
                [Grant(revoked.GrantId), Subject(revoked.SubjectId), Resource(revoked.ResourceId), Role(revoked.RoleId)],
                new
                {
                    grantId = revoked.GrantId,
                    subjectId = revoked.SubjectId,
                    roleId = revoked.RoleId,
                    resourceId = revoked.ResourceId,
                    reason = revoked.Reason
                }));

    private static SqlOSAuditProjectionBuilder AddSubjectEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Audit<FgaSubjectCreated>(static (created, context) => Row(
                "fga.subject.created",
                created.Actor,
                context,
                [Subject(created.SubjectId)],
                new { subjectId = created.SubjectId, subjectType = created.SubjectTypeId },
                created.OrganizationId))
            .Audit<FgaSubjectActivationChanged>(static (changed, context) => Row(
                changed.IsActive ? "fga.subject.reactivated" : "fga.subject.deactivated",
                changed.Actor,
                context,
                [Subject(changed.SubjectId)],
                new { subjectId = changed.SubjectId }))
            .Audit<FgaServiceAccountExpiryChanged>(static (changed, context) => Row(
                "fga.subject.expiry_changed",
                changed.Actor,
                context,
                [Subject(changed.SubjectId)],
                new { subjectId = changed.SubjectId, expiresAt = changed.ExpiresAt }))
            .Audit<FgaSubjectDeleted>(static (deleted, context) => Row(
                "fga.subject.deleted",
                deleted.Actor,
                context,
                [Subject(deleted.SubjectId)],
                new { subjectId = deleted.SubjectId, subjectType = deleted.SubjectTypeId }))
            .Audit<FgaGroupMemberAdded>(static (added, context) => Row(
                "fga.group.member_added",
                added.Actor,
                context,
                [Subject(added.GroupSubjectId), Subject(added.MemberSubjectId)],
                new { groupId = added.GroupId, groupSubjectId = added.GroupSubjectId, memberSubjectId = added.MemberSubjectId }))
            .Audit<FgaGroupMemberRemoved>(static (removed, context) => Row(
                "fga.group.member_removed",
                removed.Actor,
                context,
                [Subject(removed.GroupSubjectId), Subject(removed.MemberSubjectId)],
                new { groupId = removed.GroupId, groupSubjectId = removed.GroupSubjectId, memberSubjectId = removed.MemberSubjectId }))
            .Audit<FgaSubjectMerged>(static (merged, context) => Row(
                "fga.subject.merged",
                merged.Actor,
                context,
                [Subject(merged.SubjectId)],
                new
                {
                    subjectId = merged.SubjectId,
                    mergedSubjectId = merged.MergedSubjectId,
                    groupIds = merged.GroupIds,
                    movedGrantIds = merged.MovedGrantIds,
                    droppedGrantIds = merged.DroppedGrantIds
                }));

    private static SqlOSAuditProjectionBuilder AddResourceAndRoleEvents(this SqlOSAuditProjectionBuilder builder)
        => builder
            .Audit<FgaResourceMoved>(static (moved, context) => Row(
                "fga.resource.moved",
                moved.Actor,
                context,
                [Resource(moved.ResourceId)],
                new { resourceId = moved.ResourceId, fromParentId = moved.FromParentId, toParentId = moved.ToParentId }))
            .Audit<FgaResourceActivationChanged>(static (changed, context) => Row(
                changed.IsActive ? "fga.resource.reactivated" : "fga.resource.deactivated",
                changed.Actor,
                context,
                [Resource(changed.ResourceId)],
                new { resourceId = changed.ResourceId }))
            .Audit<FgaResourceDeleted>(static (deleted, context) => Row(
                "fga.resource.deleted",
                deleted.Actor,
                context,
                [Resource(deleted.ResourceId)],
                new { resourceId = deleted.ResourceId, parentId = deleted.ParentId }))
            .Audit<FgaRoleDefined>(static (defined, context) => Row(
                "fga.role.created",
                defined.Actor,
                context,
                [Role(defined.RoleId)],
                new { roleId = defined.RoleId, key = defined.Key, isVirtual = defined.IsVirtual }))
            .Audit<FgaRoleRedefined>(static (redefined, context) => Row(
                "fga.role.updated",
                redefined.Actor,
                context,
                [Role(redefined.RoleId)],
                new { roleId = redefined.RoleId, key = redefined.Key, isVirtual = redefined.IsVirtual }))
            .Audit<FgaRolePermissionAdded>(static (added, context) => Row(
                "fga.role.permission_added",
                added.Actor,
                context,
                [Role(added.RoleId)],
                new { roleId = added.RoleId, permissionId = added.PermissionId }));

    private static SqlOSAuditEvent Row(
        string action,
        FgaActor actor,
        SqlOSAuditProjectionContext context,
        IReadOnlyList<SqlOSAuditTarget> targets,
        object metadata,
        string? organizationId = null)
        => SqlOSAuditRows.Create(
            new SqlOSAuditLogRecordRequest(
                Action: action,
                OrganizationId: organizationId,
                Source: "fga",
                Actor: new SqlOSAuditActor(actor.Type, actor.Id),
                Targets: targets,
                Context: Context(context.Request),
                Metadata: SqlOSAuditRows.Metadata(metadata)),
            SqlOSIds.New("evt"),
            context.Now);

    /// <summary>The request that made the change; none for a write outside one (startup, host code, a directory).</summary>
    private static SqlOSAuditContext? Context(SqlOSRequestContext request)
        => request.IpAddress == null && request.UserAgent == null && request.RequestId == null && request.CorrelationId == null
            ? null
            : new SqlOSAuditContext(
                IpAddress: request.IpAddress,
                UserAgent: request.UserAgent,
                RequestId: request.RequestId,
                CorrelationId: request.CorrelationId);

    private static SqlOSAuditTarget Grant(string id) => new("fga_grant", id);

    private static SqlOSAuditTarget Subject(string id) => new("fga_subject", id);

    private static SqlOSAuditTarget Resource(string id) => new("fga_resource", id);

    private static SqlOSAuditTarget Role(string id) => new("fga_role", id);
}
