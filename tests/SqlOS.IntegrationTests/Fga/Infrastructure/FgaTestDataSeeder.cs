using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Infrastructure;

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
            new SqlOSFgaResourceType { Id = "agency", Name = "Agency" },
            new SqlOSFgaResourceType { Id = "team", Name = "Team" },
            new SqlOSFgaResourceType { Id = "project", Name = "Project" }
        );

        // Permissions
        context.Set<SqlOSFgaPermission>().AddRange(
            new SqlOSFgaPermission { Id = ViewPermissionId, Key = "TEST_VIEW", Name = "View" },
            new SqlOSFgaPermission { Id = EditPermissionId, Key = "TEST_EDIT", Name = "Edit" },
            new SqlOSFgaPermission { Id = AdminPermissionId, Key = "TEST_ADMIN", Name = "Admin" }
        );

        // Roles
        context.Set<SqlOSFgaRole>().AddRange(
            new SqlOSFgaRole { Id = SystemAdminRoleId, Key = "SystemAdmin", Name = "System Admin" },
            new SqlOSFgaRole { Id = AgencyAdminRoleId, Key = "AgencyAdmin", Name = "Agency Admin" },
            new SqlOSFgaRole { Id = AgencyMemberRoleId, Key = "AgencyMember", Name = "Agency Member" }
        );

        await context.SaveChangesAsync();

        // Role-Permission mappings
        context.Set<SqlOSFgaRolePermission>().AddRange(
            // SystemAdmin gets all permissions
            new SqlOSFgaRolePermission { RoleId = SystemAdminRoleId, PermissionId = ViewPermissionId },
            new SqlOSFgaRolePermission { RoleId = SystemAdminRoleId, PermissionId = EditPermissionId },
            new SqlOSFgaRolePermission { RoleId = SystemAdminRoleId, PermissionId = AdminPermissionId },
            // AgencyAdmin gets view + edit
            new SqlOSFgaRolePermission { RoleId = AgencyAdminRoleId, PermissionId = ViewPermissionId },
            new SqlOSFgaRolePermission { RoleId = AgencyAdminRoleId, PermissionId = EditPermissionId },
            // AgencyMember gets view only
            new SqlOSFgaRolePermission { RoleId = AgencyMemberRoleId, PermissionId = ViewPermissionId }
        );

        // Resources (hierarchy: root > agency > team/project, root > other_agency)
        context.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = TestAgencyResourceId, ParentId = "root", Name = "Test Agency", ResourceTypeId = "agency" },
            new SqlOSFgaResource { Id = TestTeamResourceId, ParentId = TestAgencyResourceId, Name = "Test Team", ResourceTypeId = "team" },
            new SqlOSFgaResource { Id = TestProjectResourceId, ParentId = TestAgencyResourceId, Name = "Test Project", ResourceTypeId = "project" },
            new SqlOSFgaResource { Id = OtherAgencyResourceId, ParentId = "root", Name = "Other Agency", ResourceTypeId = "agency" }
        );

        // Subjects
        context.Set<SqlOSFgaSubject>().AddRange(
            new SqlOSFgaSubject { Id = SystemAdminSubjectId, SubjectTypeId = "user", DisplayName = "System Admin" },
            new SqlOSFgaSubject { Id = AgencyAdminSubjectId, SubjectTypeId = "user", DisplayName = "Agency Admin" },
            new SqlOSFgaSubject { Id = AgencyMemberSubjectId, SubjectTypeId = "user", DisplayName = "Agency Member" },
            new SqlOSFgaSubject { Id = GroupMemberSubjectId, SubjectTypeId = "user", DisplayName = "Group Member" },
            new SqlOSFgaSubject { Id = UnauthorizedSubjectId, SubjectTypeId = "user", DisplayName = "Unauthorized User" },
            new SqlOSFgaSubject { Id = TestGroupSubjectId, SubjectTypeId = "group", DisplayName = "Test Group" },
            new SqlOSFgaSubject { Id = TestUserSubjectId, SubjectTypeId = "user", DisplayName = "Test User" },
            new SqlOSFgaSubject { Id = TestAgentSubjectId, SubjectTypeId = "agent", DisplayName = "Test Agent" },
            new SqlOSFgaSubject { Id = TestServiceAccountSubjectId, SubjectTypeId = "service_account", DisplayName = "Test Service Account" }
        );

        // User extension
        context.Set<SqlOSFgaUser>().Add(new SqlOSFgaUser
        {
            Id = TestUserId,
            SubjectId = TestUserSubjectId,
            Email = "testuser@example.com",
            IsActive = true
        });

        // Agent extension
        context.Set<SqlOSFgaAgent>().Add(new SqlOSFgaAgent
        {
            Id = TestAgentId,
            SubjectId = TestAgentSubjectId,
            AgentType = "background_job",
            Description = "Test background job agent"
        });

        // Service account extension
        context.Set<SqlOSFgaServiceAccount>().Add(new SqlOSFgaServiceAccount
        {
            Id = TestServiceAccountId,
            SubjectId = TestServiceAccountSubjectId,
            ClientId = "test_client_id",
            ClientSecretHash = "test_hash"
        });

        // User group
        context.Set<SqlOSFgaUserGroup>().Add(
            new SqlOSFgaUserGroup { Id = TestGroupId, Name = "Test Group", SubjectId = TestGroupSubjectId }
        );

        await context.SaveChangesAsync();

        // Group membership (GroupMember belongs to TestGroup, Agent also in TestGroup for inheritance tests)
        context.Set<SqlOSFgaUserGroupMembership>().AddRange(
            new SqlOSFgaUserGroupMembership { SubjectId = GroupMemberSubjectId, UserGroupId = TestGroupId },
            new SqlOSFgaUserGroupMembership { SubjectId = TestAgentSubjectId, UserGroupId = TestGroupId }
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
