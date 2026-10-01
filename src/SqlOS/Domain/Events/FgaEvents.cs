namespace SqlOS.Domain.Events;

// The FGA write model. A change to who may access what is audited (#415): grants, group
// memberships, a subject's lifecycle, a role's permissions and the shape of the resource tree.
// Definitions and labels that change no access by themselves are not: resource types,
// permissions, names and descriptions, and a new resource, which only inherits what its parent
// already grants.

/// <summary>A role was granted to a subject on a resource, in effect during the window.</summary>
internal sealed record FgaGrantCreated(
    string GrantId,
    string SubjectId,
    string RoleId,
    string ResourceId,
    DateTime? EffectiveFrom,
    DateTime? EffectiveTo,
    string? OrganizationId,
    FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A grant was revoked; <paramref name="Reason"/> names why when another change caused it.</summary>
internal sealed record FgaGrantRevoked(
    string GrantId,
    string SubjectId,
    string RoleId,
    string ResourceId,
    string? Reason,
    FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A grant's description changed.</summary>
internal sealed record FgaGrantDescribed(string GrantId) : ISqlOSDomainEvent;

/// <summary>A subject was created with its kind.</summary>
internal sealed record FgaSubjectCreated(
    string SubjectId,
    string SubjectTypeId,
    string? OrganizationId,
    FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A subject's display name, organization, external reference or typed details changed.</summary>
internal sealed record FgaSubjectDescribed(string SubjectId) : ISqlOSDomainEvent;

/// <summary>A user or group subject was deactivated (<paramref name="IsActive"/> false) or reactivated.</summary>
internal sealed record FgaSubjectActivationChanged(string SubjectId, bool IsActive, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A service account's expiry changed; it stops authorizing at <paramref name="ExpiresAt"/>.</summary>
internal sealed record FgaServiceAccountExpiryChanged(string SubjectId, DateTime? ExpiresAt, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A subject that held no grants and belonged to no group was deleted.</summary>
internal sealed record FgaSubjectDeleted(string SubjectId, string SubjectTypeId, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A subject joined a group, gaining the group's grants.</summary>
internal sealed record FgaGroupMemberAdded(string GroupSubjectId, string GroupId, string MemberSubjectId, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A subject left a group.</summary>
internal sealed record FgaGroupMemberRemoved(string GroupSubjectId, string GroupId, string MemberSubjectId, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>
/// A directory's subject for a SqlOS user was merged into the user's own subject (#448): its group
/// memberships and grants moved, and grants the user's subject already held were dropped.
/// </summary>
internal sealed record FgaSubjectMerged(
    string SubjectId,
    string MergedSubjectId,
    IReadOnlyList<string> GroupIds,
    IReadOnlyList<string> MovedGrantIds,
    IReadOnlyList<string> DroppedGrantIds,
    FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A resource was created.</summary>
internal sealed record FgaResourceCreated(string ResourceId, string? ParentId, string ResourceTypeId) : ISqlOSDomainEvent;

/// <summary>A resource's name, description or type changed.</summary>
internal sealed record FgaResourceDescribed(string ResourceId) : ISqlOSDomainEvent;

/// <summary>A resource moved under another parent, so it inherits other grants.</summary>
internal sealed record FgaResourceMoved(string ResourceId, string? FromParentId, string? ToParentId, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A resource was deactivated (<paramref name="IsActive"/> false) or reactivated, with its subtree's inherited access.</summary>
internal sealed record FgaResourceActivationChanged(string ResourceId, bool IsActive, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A leaf resource was deleted with its grants.</summary>
internal sealed record FgaResourceDeleted(string ResourceId, string? ParentId, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A role was defined.</summary>
internal sealed record FgaRoleDefined(string RoleId, string Key, bool IsVirtual, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A role's key, name, description or virtual flag changed.</summary>
internal sealed record FgaRoleRedefined(string RoleId, string Key, bool IsVirtual, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A role gained a permission, for every subject it is granted to.</summary>
internal sealed record FgaRolePermissionAdded(string RoleId, string PermissionId, FgaActor Actor) : ISqlOSDomainEvent;

/// <summary>A resource type was defined or changed.</summary>
internal sealed record FgaResourceTypeDefined(string ResourceTypeId) : ISqlOSDomainEvent;

/// <summary>A permission was defined or changed.</summary>
internal sealed record FgaPermissionDefined(string PermissionId, string Key, string? ResourceTypeId) : ISqlOSDomainEvent;
