using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The data behind the pinned <c>BuildFilterAsync</c> expressions: a user in an active and an
/// inactive group, and one permission.
/// </summary>
internal static class ReadPathPinData
{
    public static async Task SeedAsync(ReadPathPinContext context)
    {
        context.Set<SqlOSFgaSubjectType>().AddRange(
            FgaTestModel.SubjectType("user", "User"),
            FgaTestModel.SubjectType("group", "Group"));
        context.Set<SqlOSFgaSubject>().AddRange(
            FgaTestModel.Subject("pin-user", "user", displayName: "Pin user"),
            FgaTestModel.Subject("pin-group-subject", "group", displayName: "Pin group"),
            FgaTestModel.Subject("pin-inactive-group-subject", "group", displayName: "Inactive pin group"));
        context.Set<SqlOSFgaUser>().Add(FgaTestModel.User("pin-user-row", "pin-user"));
        context.Set<SqlOSFgaUserGroup>().AddRange(
            FgaTestModel.Group("pin-group", "pin-group-subject", "Pin group"),
            FgaTestModel.Group("pin-inactive-group", "pin-inactive-group-subject", "Inactive pin group", isActive: false));
        context.Set<SqlOSFgaUserGroupMembership>().AddRange(
            FgaTestModel.Membership("pin-user", "pin-group"),
            FgaTestModel.Membership("pin-user", "pin-inactive-group"));
        context.Set<SqlOSFgaPermission>().Add(FgaTestModel.Permission("perm-pin-read", "pin.read", name: "Read"));
        await context.SaveChangesAsync();
    }
}
