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

    /// <summary>Drops the events <paramref name="aggregate"/> raised while the fixture built it.</summary>
    public static T Existing<T>(T aggregate)
        where T : ISqlOSAggregate
    {
        aggregate.Events.Drain();
        return aggregate;
    }
}
