using SqlOS.Domain;
using SqlOS.Fga.Models;

namespace SqlOS.Tests.Infrastructure;

/// <summary>
/// Builds FGA records for test fixtures through the write model's own factories, so a fixture
/// holds exactly what SqlOS would store. A fixture is data that already exists, so its records
/// carry no pending domain events (and write no audit rows). Shared by the unit and integration
/// test projects.
/// </summary>
internal static class FgaTestModel
{
    public static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public static SqlOSFgaSubjectType SubjectType(string id, string? name = null)
        => SqlOSFgaSubjectType.Define(id, name ?? id);

    /// <summary>The four subject types SqlOS seeds.</summary>
    public static SqlOSFgaSubjectType[] BuiltInSubjectTypes()
        => [SubjectType("user", "User"), SubjectType("group", "Group"), SubjectType("service_account", "Service Account"), SubjectType("agent", "Agent")];

    public static SqlOSFgaResourceType ResourceType(string id, string? name = null, string? description = null)
        => Existing(SqlOSFgaResourceType.Define(id, name ?? id, description));

    public static SqlOSFgaPermission Permission(string id, string key, string? name = null, string? resourceTypeId = null, string? description = null)
        => Existing(SqlOSFgaPermission.Define(id, key, name ?? key, description, resourceTypeId));

    public static SqlOSFgaRole Role(string id, string? key = null, string? name = null, params SqlOSFgaPermission[] permissions)
    {
        var role = SqlOSFgaRole.Define(id, key ?? id, name ?? key ?? id, description: null, isVirtual: false, FgaActor.Host);
        foreach (var permission in permissions)
        {
            role.Allow(permission, FgaActor.Host);
        }

        return Existing(role);
    }

    /// <summary>
    /// A stored resource. Its parent is set as given, unchecked, so a fixture can also hold a tree
    /// SqlOS would refuse to build (a cycle, a dangling parent, one too deep).
    /// </summary>
    public static SqlOSFgaResource Resource(
        string id,
        string name,
        string resourceTypeId,
        string? parentId = null,
        bool isActive = true,
        DateTime? createdAt = null)
    {
        var resource = SqlOSFgaResource.Create(id, name, resourceTypeId, description: null, SqlOSFgaAncestry.None, createdAt ?? Now, isActive);
        Set(resource, nameof(SqlOSFgaResource.ParentId), parentId);
        return Existing(resource);
    }

    /// <summary>A stored subject without a typed record; add the typed record as its own row.</summary>
    public static SqlOSFgaSubject Subject(string id, string subjectTypeId, string? displayName = null, string? organizationId = null, string? externalRef = null)
        => Existing(SqlOSFgaSubject.Create(id, subjectTypeId, displayName ?? id, organizationId, externalRef, FgaActor.Host, Now));

    public static SqlOSFgaUser User(string id, string subjectId, string? email = null, bool isActive = true)
        => new(id, subjectId, email, isActive, Now);

    public static SqlOSFgaAgent Agent(string id, string subjectId, string? agentType = null, string? description = null)
        => new(id, subjectId, agentType, description, Now);

    public static SqlOSFgaServiceAccount ServiceAccount(
        string id,
        string subjectId,
        string clientId,
        string clientSecretHash = "hash",
        DateTime? expiresAt = null,
        string? description = null,
        string configurationOwner = "dashboard",
        string? configurationSourceKey = null)
        => new(id, subjectId, clientId, clientSecretHash, description, expiresAt, configurationOwner, configurationSourceKey, Now);

    public static SqlOSFgaUserGroup Group(string id, string subjectId, string name, bool isActive = true, string? description = null, string? groupType = null)
    {
        var group = new SqlOSFgaUserGroup(id, subjectId, name, description, groupType, Now);
        group.ChangeActivity(isActive, Now);
        return group;
    }

    public static SqlOSFgaUserGroupMembership Membership(string subjectId, string userGroupId)
        => new(subjectId, userGroupId, Now);

    /// <summary>Sets a persisted property directly, for state the write model would not produce.</summary>
    public static T Set<T>(T entity, string property, object? value)
        where T : class
    {
        typeof(T).GetProperty(property)!.SetValue(entity, value);
        return entity;
    }

    /// <summary>Drops the events <paramref name="aggregate"/> raised while the fixture built it.</summary>
    public static T Existing<T>(T aggregate)
        where T : ISqlOSAggregate
    {
        aggregate.Events.Drain();
        return aggregate;
    }
}
