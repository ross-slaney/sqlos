using Sqlzibar.Models;

namespace Sqlzibar.IntegrationTests.Infrastructure;

public static class TestDataSeeder
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

    public static async Task SeedAsync(TestSqlzibarDbContext context)
    {
        // Resource types
        context.Set<SqlzibarResourceType>().AddRange(
            new SqlzibarResourceType { Id = "agency", Name = "Agency" },
            new SqlzibarResourceType { Id = "team", Name = "Team" },
            new SqlzibarResourceType { Id = "project", Name = "Project" }
        );

        // Permissions
        context.Set<SqlzibarPermission>().AddRange(
            new SqlzibarPermission { Id = ViewPermissionId, Key = "TEST_VIEW", Name = "View" },
            new SqlzibarPermission { Id = EditPermissionId, Key = "TEST_EDIT", Name = "Edit" },
            new SqlzibarPermission { Id = AdminPermissionId, Key = "TEST_ADMIN", Name = "Admin" }
        );

        // Roles
        context.Set<SqlzibarRole>().AddRange(
            new SqlzibarRole { Id = SystemAdminRoleId, Key = "SystemAdmin", Name = "System Admin" },
            new SqlzibarRole { Id = AgencyAdminRoleId, Key = "AgencyAdmin", Name = "Agency Admin" },
            new SqlzibarRole { Id = AgencyMemberRoleId, Key = "AgencyMember", Name = "Agency Member" }
        );

        await context.SaveChangesAsync();

        // Role-Permission mappings
        context.Set<SqlzibarRolePermission>().AddRange(
            // SystemAdmin gets all permissions
            new SqlzibarRolePermission { RoleId = SystemAdminRoleId, PermissionId = ViewPermissionId },
            new SqlzibarRolePermission { RoleId = SystemAdminRoleId, PermissionId = EditPermissionId },
            new SqlzibarRolePermission { RoleId = SystemAdminRoleId, PermissionId = AdminPermissionId },
            // AgencyAdmin gets view + edit
            new SqlzibarRolePermission { RoleId = AgencyAdminRoleId, PermissionId = ViewPermissionId },
            new SqlzibarRolePermission { RoleId = AgencyAdminRoleId, PermissionId = EditPermissionId },
            // AgencyMember gets view only
            new SqlzibarRolePermission { RoleId = AgencyMemberRoleId, PermissionId = ViewPermissionId }
        );

        // Resources (hierarchy: root > agency > team/project, root > other_agency)
        context.Set<SqlzibarResource>().AddRange(
            new SqlzibarResource { Id = TestAgencyResourceId, ParentId = "root", Name = "Test Agency", ResourceTypeId = "agency" },
            new SqlzibarResource { Id = TestTeamResourceId, ParentId = TestAgencyResourceId, Name = "Test Team", ResourceTypeId = "team" },
            new SqlzibarResource { Id = TestProjectResourceId, ParentId = TestAgencyResourceId, Name = "Test Project", ResourceTypeId = "project" },
            new SqlzibarResource { Id = OtherAgencyResourceId, ParentId = "root", Name = "Other Agency", ResourceTypeId = "agency" }
        );

        // Subjects
        context.Set<SqlzibarSubject>().AddRange(
            new SqlzibarSubject { Id = SystemAdminSubjectId, SubjectTypeId = "user", DisplayName = "System Admin" },
            new SqlzibarSubject { Id = AgencyAdminSubjectId, SubjectTypeId = "user", DisplayName = "Agency Admin" },
            new SqlzibarSubject { Id = AgencyMemberSubjectId, SubjectTypeId = "user", DisplayName = "Agency Member" },
            new SqlzibarSubject { Id = GroupMemberSubjectId, SubjectTypeId = "user", DisplayName = "Group Member" },
            new SqlzibarSubject { Id = UnauthorizedSubjectId, SubjectTypeId = "user", DisplayName = "Unauthorized User" },
            new SqlzibarSubject { Id = TestGroupSubjectId, SubjectTypeId = "group", DisplayName = "Test Group" },
            new SqlzibarSubject { Id = TestUserSubjectId, SubjectTypeId = "user", DisplayName = "Test User" },
            new SqlzibarSubject { Id = TestAgentSubjectId, SubjectTypeId = "agent", DisplayName = "Test Agent" },
            new SqlzibarSubject { Id = TestServiceAccountSubjectId, SubjectTypeId = "service_account", DisplayName = "Test Service Account" }
        );

        // User extension
        context.Set<SqlzibarUser>().Add(new SqlzibarUser
        {
            Id = TestUserId,
            SubjectId = TestUserSubjectId,
            Email = "testuser@example.com",
            IsActive = true
        });

        // Agent extension
        context.Set<SqlzibarAgent>().Add(new SqlzibarAgent
        {
            Id = TestAgentId,
            SubjectId = TestAgentSubjectId,
            AgentType = "background_job",
            Description = "Test background job agent"
        });

        // Service account extension
        context.Set<SqlzibarServiceAccount>().Add(new SqlzibarServiceAccount
        {
            Id = TestServiceAccountId,
            SubjectId = TestServiceAccountSubjectId,
            ClientId = "test_client_id",
            ClientSecretHash = "test_hash"
        });

        // User group
        context.Set<SqlzibarUserGroup>().Add(
            new SqlzibarUserGroup { Id = TestGroupId, Name = "Test Group", SubjectId = TestGroupSubjectId }
        );

        await context.SaveChangesAsync();

        // Group membership (GroupMember belongs to TestGroup, Agent also in TestGroup for inheritance tests)
        context.Set<SqlzibarUserGroupMembership>().AddRange(
            new SqlzibarUserGroupMembership { SubjectId = GroupMemberSubjectId, UserGroupId = TestGroupId },
            new SqlzibarUserGroupMembership { SubjectId = TestAgentSubjectId, UserGroupId = TestGroupId }
        );

        // Grants
        context.Set<SqlzibarGrant>().AddRange(
            // SystemAdmin at root
            new SqlzibarGrant { Id = "grant_test_sysadmin", SubjectId = SystemAdminSubjectId, ResourceId = "root", RoleId = SystemAdminRoleId },
            // AgencyAdmin at test agency
            new SqlzibarGrant { Id = "grant_test_agencyadmin", SubjectId = AgencyAdminSubjectId, ResourceId = TestAgencyResourceId, RoleId = AgencyAdminRoleId },
            // AgencyMember at test agency
            new SqlzibarGrant { Id = "grant_test_member", SubjectId = AgencyMemberSubjectId, ResourceId = TestAgencyResourceId, RoleId = AgencyMemberRoleId },
            // Group at test agency (via group subject)
            new SqlzibarGrant { Id = "grant_test_group", SubjectId = TestGroupSubjectId, ResourceId = TestAgencyResourceId, RoleId = AgencyMemberRoleId }
        );

        await context.SaveChangesAsync();
    }
}
