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
            new SqlOSFgaSubject { Id = "pin-user", SubjectTypeId = "user", DisplayName = "Pin user" },
            new SqlOSFgaSubject { Id = "pin-group-subject", SubjectTypeId = "group", DisplayName = "Pin group" },
            new SqlOSFgaSubject { Id = "pin-inactive-group-subject", SubjectTypeId = "group", DisplayName = "Inactive pin group" });
        context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser { Id = "pin-user-row", SubjectId = "pin-user", IsActive = true });
        context.Set<SqlOSFgaUserGroup>().AddRange(
            new SqlOSFgaUserGroup { Id = "pin-group", SubjectId = "pin-group-subject", Name = "Pin group", IsActive = true },
            new SqlOSFgaUserGroup { Id = "pin-inactive-group", SubjectId = "pin-inactive-group-subject", Name = "Inactive pin group", IsActive = false });
        context.Set<SqlOSFgaUserGroupMembership>().AddRange(
            new SqlOSFgaUserGroupMembership { SubjectId = "pin-user", UserGroupId = "pin-group" },
            new SqlOSFgaUserGroupMembership { SubjectId = "pin-user", UserGroupId = "pin-inactive-group" });
        context.Set<SqlOSFgaPermission>().Add(FgaTestModel.Permission("perm-pin-read", "pin.read", name: "Read"));
        await context.SaveChangesAsync();
    }
}
