using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSApplicationAssignmentsTests
{
    [TestMethod]
    public async Task ApplicationAssignments_DefaultMigration_AllowsExistingClients()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);

        result.Tokens.Should().NotBeNull();
        result.Tokens!.ClientId.Should().Be("test-client");
    }

    [TestMethod]
    public async Task ApplicationAssignments_OrganizationAssignment_AllowsOrgUser()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));
        await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_allowed"));

        var result = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);

        result.Tokens.Should().NotBeNull();
        result.Tokens!.OrganizationId.Should().Be("org_allowed");
    }

    [TestMethod]
    public async Task ApplicationAssignments_UnassignedOrganization_DeniesAuthorization()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));
        await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_allowed"));
        var request = await harness.CreateAuthorizationRequestAsync();

        var act = async () => await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            harness.User,
            "org_blocked",
            "password",
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application access is not allowed.");
    }

    [TestMethod]
    public async Task ApplicationAssignments_UserAssignment_AllowsSpecificUser()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedUsersGroupsRoles));
        await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.User,
            PrincipalId: harness.User.Id));

        var result = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);

        result.Tokens.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ApplicationAssignments_GroupAssignment_AllowsGroupMember()
    {
        await using var harness = await Harness.CreateAsync();
        var group = await harness.SeedFgaGroupMembershipAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedUsersGroupsRoles));
        await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Group,
            PrincipalId: group.Id));

        var result = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);

        result.Tokens.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ApplicationAssignments_RoleAssignment_AllowsOrgRole()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedUsersGroupsRoles));
        await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Role,
            OrganizationId: "org_allowed",
            RoleKey: "admin"));

        var result = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);

        result.Tokens.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ApplicationAssignments_DisabledApplication_DeniesAuthorization()
    {
        await using var harness = await Harness.CreateAsync();
        var request = await harness.CreateAuthorizationRequestAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.Disabled));

        var act = async () => await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            harness.User,
            "org_allowed",
            "password",
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application access is not allowed.");
    }

    [TestMethod]
    public async Task ApplicationAssignments_DeviceAuthorization_UnassignedUserDenied()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-cli", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));

        var start = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("test-cli", "openid offline_access", "test-cli"),
            harness.Http);

        var act = async () => await harness.Device.ApproveAsync(
            new SqlOSDeviceAuthorizationApprovalRequest(start.UserCode, "org_allowed"),
            harness.User,
            "password",
            harness.Http);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application access is not allowed.");
        var stored = await harness.Context.Set<SqlOSDeviceAuthorization>().SingleAsync();
        stored.Status.Should().Be(SqlOSDeviceAuthorizationService.PendingStatus);
    }

    [TestMethod]
    public async Task ApplicationAssignments_RefreshAfterRevocation_FollowsDocumentedPolicy()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));
        var assignment = await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_allowed"));
        var login = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);

        await harness.Admin.RevokeApplicationAssignmentAsync("test-client", assignment.Id);

        var act = async () => await harness.Auth.RefreshAsync(new SqlOSRefreshRequest(login.Tokens!.RefreshToken, null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application access is not allowed.");
    }

    [TestMethod]
    public async Task ApplicationAssignments_AccessCheck_ExplainsDecisionSource()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));
        var assignment = await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_allowed",
            Reason: "pilot"));

        var check = await harness.Admin.CheckApplicationAccessAsync("test-client", harness.User.Id, "org_allowed");

        check.Allowed.Should().BeTrue();
        check.Source.Should().Be("organization_assignment");
        check.AssignmentId.Should().Be(assignment.Id);
        check.Reason.Should().Be("pilot");
    }

    [TestMethod]
    public async Task ApplicationAssignments_Audit_WritesCreateRevokeAndDeniedEvents()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));
        var assignment = await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_allowed"));
        await harness.Admin.RevokeApplicationAssignmentAsync("test-client", assignment.Id);

        var request = await harness.CreateAuthorizationRequestAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            harness.User,
            "org_blocked",
            "password",
            harness.Http));

        var events = await harness.Context.Set<SqlOSAuditEvent>().Select(x => x.EventType).ToListAsync();
        events.Should().Contain("application.assignment.created");
        events.Should().Contain("application.assignment.revoked");
        events.Should().Contain("application.access.authorization_denied");
    }

    [TestMethod]
    public async Task ApplicationAssignments_DoesNotLeakAssignmentStateInPublicError()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync("test-client", new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));
        var request = await harness.CreateAuthorizationRequestAsync();

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            harness.User,
            "org_blocked",
            "password",
            harness.Http));

        failure.Message.Should().Be("Application access is not allowed.");
        failure.Message.Should().NotContain("org_blocked");
        failure.Message.Should().NotContain("selected_organizations");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqlOSCryptoService _crypto;

        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAdminService admin,
            SqlOSAuthService auth,
            SqlOSAuthorizationServerService authorization,
            SqlOSDeviceAuthorizationService device,
            SqlOSCryptoService crypto,
            SqlOSUser user,
            DefaultHttpContext http)
        {
            Context = context;
            Admin = admin;
            Auth = auth;
            Authorization = authorization;
            Device = device;
            _crypto = crypto;
            User = user;
            Http = http;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSAuthService Auth { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSDeviceAuthorizationService Device { get; }
        public SqlOSUser User { get; }
        public DefaultHttpContext Http { get; }

        public static async Task<Harness> CreateAsync()
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions
            {
                Issuer = "https://auth.example.test/sqlos/auth",
                PublicOrigin = "https://auth.example.test",
                DefaultAudience = "test-client"
            };
            authOptions.ResourceIndicators.Enabled = true;
            authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            authOptions.SeedCliClient("test-cli", "Test CLI", "test-cli", "openid", "offline_access");

            var options = Options.Create(authOptions);
            var crypto = TestCryptoService.Create(context, options);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var authorization = new SqlOSAuthorizationServerService(context, admin, auth, crypto, settings, authPageSession, options);
            var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

            await crypto.EnsureActiveSigningKeyAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await admin.UpsertSeededClientsAsync();

            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Ada Lovelace", "ada@example.com", "P@ssword123!"));
            context.Set<SqlOSOrganization>().AddRange(
                new SqlOSOrganization { Id = "org_allowed", Slug = "allowed", Name = "Allowed Org", CreatedAt = DateTime.UtcNow, IsActive = true },
                new SqlOSOrganization { Id = "org_blocked", Slug = "blocked", Name = "Blocked Org", CreatedAt = DateTime.UtcNow, IsActive = true });
            context.Set<SqlOSMembership>().AddRange(
                new SqlOSMembership { OrganizationId = "org_allowed", UserId = user.Id, Role = "admin", CreatedAt = DateTime.UtcNow, IsActive = true },
                new SqlOSMembership { OrganizationId = "org_blocked", UserId = user.Id, Role = "member", CreatedAt = DateTime.UtcNow, IsActive = true });
            await context.SaveChangesAsync();

            return new Harness(context, admin, auth, authorization, device, crypto, user, http);
        }

        public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync()
        {
            var request = await Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                "test-client",
                "https://client.example.test/callback",
                _crypto.GenerateOpaqueToken(),
                "openid offline_access",
                _crypto.HashToken("verifier"),
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));
            return request;
        }

        public async Task<SqlOSFgaUserGroup> SeedFgaGroupMembershipAsync()
        {
            var subject = new SqlOSFgaSubject
            {
                Id = "subj_ada",
                SubjectTypeId = "user",
                DisplayName = "Ada Lovelace",
                ExternalRef = User.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var groupSubject = new SqlOSFgaSubject
            {
                Id = "subj_group_app",
                SubjectTypeId = "group",
                DisplayName = "App Group",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var user = new SqlOSFgaUser
            {
                Id = "fga_user_ada",
                SubjectId = subject.Id,
                Email = "ada@example.com",
                IsActive = true
            };
            var group = new SqlOSFgaUserGroup
            {
                Id = "grp_app",
                Name = "App Group",
                SubjectId = groupSubject.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            Context.Set<SqlOSFgaSubject>().AddRange(subject, groupSubject);
            Context.Set<SqlOSFgaUser>().Add(user);
            Context.Set<SqlOSFgaUserGroup>().Add(group);
            Context.Set<SqlOSFgaUserGroupMembership>().Add(new SqlOSFgaUserGroupMembership
            {
                SubjectId = subject.Id,
                UserGroupId = group.Id,
                CreatedAt = DateTime.UtcNow
            });
            await Context.SaveChangesAsync();
            return group;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}
