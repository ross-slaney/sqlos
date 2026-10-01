using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga.Infrastructure;

public static class FgaTestDataSeeder
{
    // Test subject IDs
    public const string SystemAdminSubjectId = "subj_test_sysadmin";
    public const string AgencyAdminSubjectId = "subj_test_agencyadmin";
    public const string AgencyMemberSubjectId = "subj_test_member";
    public const string GroupMemberSubjectId = "subj_test_groupmember";
    public const string UnauthorizedSubjectId = "subj_test_unauth";

    // Test resource IDs
    public const string TestAgencyResourceId = "res_test_agency";
    public const string TestTeamResourceId = "res_test_team";
    public const string TestProjectResourceId = "res_test_project";
    public const string OtherAgencyResourceId = "res_other_agency";

    // Test role IDs
    public const string SystemAdminRoleId = "role_test_sysadmin";
    public const string AgencyAdminRoleId = "role_test_agencyadmin";
    public const string AgencyMemberRoleId = "role_test_member";

    // Test permission IDs
    public const string ViewPermissionId = "perm_test_view";
    public const string EditPermissionId = "perm_test_edit";
    public const string AdminPermissionId = "perm_test_admin";

    // Test group IDs
    public const string TestGroupId = "grp_test_group";
    public const string TestGroupSubjectId = "subj_test_group";

    // Test user (extension table)
    public const string TestUserSubjectId = "subj_test_user";
    public const string TestUserId = "usr_test_user";

    // Test agent
    public const string TestAgentSubjectId = "subj_test_agent";
    public const string TestAgentId = "agt_test_agent";

    // Test service account
    public const string TestServiceAccountSubjectId = "subj_test_sa";
    public const string TestServiceAccountId = "sa_test_sa";

    public static async Task SeedAsync(TestSqlOSDbContext context)
    {
        // Resource types
        context.Set<SqlOSFgaResourceType>().AddRange(
            FgaTestModel.ResourceType("agency", "Agency"),
            FgaTestModel.ResourceType("team", "Team"),
            FgaTestModel.ResourceType("project", "Project")
        );

        // Permissions
        context.Set<SqlOSFgaPermission>().AddRange(
            FgaTestModel.Permission(ViewPermissionId, "TEST_VIEW", name: "View"),
            FgaTestModel.Permission(EditPermissionId, "TEST_EDIT", name: "Edit"),
            FgaTestModel.Permission(AdminPermissionId, "TEST_ADMIN", name: "Admin")
        );

        // Roles
        context.Set<SqlOSFgaRole>().AddRange(
            FgaTestModel.Role(SystemAdminRoleId, key: "SystemAdmin", name: "System Admin"),
            FgaTestModel.Role(AgencyAdminRoleId, key: "AgencyAdmin", name: "Agency Admin"),
            FgaTestModel.Role(AgencyMemberRoleId, key: "AgencyMember", name: "Agency Member")
        );

        await context.SaveChangesAsync();

        // Role-Permission mappings
        context.Set<SqlOSFgaRolePermission>().AddRange(
            // SystemAdmin gets all permissions
            new SqlOSFgaRolePermission(SystemAdminRoleId, ViewPermissionId),
            new SqlOSFgaRolePermission(SystemAdminRoleId, EditPermissionId),
            new SqlOSFgaRolePermission(SystemAdminRoleId, AdminPermissionId),
            // AgencyAdmin gets view + edit
            new SqlOSFgaRolePermission(AgencyAdminRoleId, ViewPermissionId),
            new SqlOSFgaRolePermission(AgencyAdminRoleId, EditPermissionId),
            // AgencyMember gets view only
            new SqlOSFgaRolePermission(AgencyMemberRoleId, ViewPermissionId)
        );

        // Resources (hierarchy: root > agency > team/project, root > other_agency)
        context.Set<SqlOSFgaResource>().AddRange(
            FgaTestModel.Resource(TestAgencyResourceId, "Test Agency", "agency", parentId: "root"),
            FgaTestModel.Resource(TestTeamResourceId, "Test Team", "team", parentId: TestAgencyResourceId),
            FgaTestModel.Resource(TestProjectResourceId, "Test Project", "project", parentId: TestAgencyResourceId),
            FgaTestModel.Resource(OtherAgencyResourceId, "Other Agency", "agency", parentId: "root")
        );

        // Subjects
        context.Set<SqlOSFgaSubject>().AddRange(
            FgaTestModel.Subject(SystemAdminSubjectId, "user", displayName: "System Admin"),
            FgaTestModel.Subject(AgencyAdminSubjectId, "user", displayName: "Agency Admin"),
            FgaTestModel.Subject(AgencyMemberSubjectId, "user", displayName: "Agency Member"),
            FgaTestModel.Subject(GroupMemberSubjectId, "user", displayName: "Group Member"),
            FgaTestModel.Subject(UnauthorizedSubjectId, "user", displayName: "Unauthorized User"),
            FgaTestModel.Subject(TestGroupSubjectId, "group", displayName: "Test Group"),
            FgaTestModel.Subject(TestUserSubjectId, "user", displayName: "Test User"),
            FgaTestModel.Subject(TestAgentSubjectId, "agent", displayName: "Test Agent"),
            FgaTestModel.Subject(TestServiceAccountSubjectId, "service_account", displayName: "Test Service Account")
        );

        // User extension
        context.Set<SqlOSFgaUser>().AddRange(
            FgaTestModel.User("usr_test_sysadmin", SystemAdminSubjectId),
            FgaTestModel.User("usr_test_agencyadmin", AgencyAdminSubjectId),
            FgaTestModel.User("usr_test_member", AgencyMemberSubjectId),
            FgaTestModel.User("usr_test_groupmember", GroupMemberSubjectId),
            FgaTestModel.User("usr_test_unauth", UnauthorizedSubjectId),
            FgaTestModel.User(TestUserId, TestUserSubjectId, email: "testuser@example.com"));

        // Agent extension
        context.Set<SqlOSFgaAgent>().Add(FgaTestModel.Agent(TestAgentId, TestAgentSubjectId, agentType: "background_job", description: "Test background job agent"));

        // Service account extension
        context.Set<SqlOSFgaServiceAccount>().Add(FgaTestModel.ServiceAccount(TestServiceAccountId, TestServiceAccountSubjectId, "test_client_id", clientSecretHash: "test_hash"));

        // User group
        context.Set<SqlOSFgaUserGroup>().Add(
            FgaTestModel.Group(TestGroupId, TestGroupSubjectId, "Test Group")
        );

        await context.SaveChangesAsync();

        // Group membership (GroupMember belongs to TestGroup, Agent also in TestGroup for inheritance tests)
        context.Set<SqlOSFgaUserGroupMembership>().AddRange(
            FgaTestModel.Membership(GroupMemberSubjectId, TestGroupId),
            FgaTestModel.Membership(TestAgentSubjectId, TestGroupId)
        );

        // Grants
        context.Set<SqlOSFgaGrant>().AddRange(
            // SystemAdmin at root
            new SqlOSFgaGrant { Id = "grant_test_sysadmin", SubjectId = SystemAdminSubjectId, ResourceId = "root", RoleId = SystemAdminRoleId },
            // AgencyAdmin at test agency
            new SqlOSFgaGrant { Id = "grant_test_agencyadmin", SubjectId = AgencyAdminSubjectId, ResourceId = TestAgencyResourceId, RoleId = AgencyAdminRoleId },
            // AgencyMember at test agency
            new SqlOSFgaGrant { Id = "grant_test_member", SubjectId = AgencyMemberSubjectId, ResourceId = TestAgencyResourceId, RoleId = AgencyMemberRoleId },
            // Group at test agency (via group subject)
            new SqlOSFgaGrant { Id = "grant_test_group", SubjectId = TestGroupSubjectId, ResourceId = TestAgencyResourceId, RoleId = AgencyMemberRoleId }
        );

        await context.SaveChangesAsync();
    }
}
