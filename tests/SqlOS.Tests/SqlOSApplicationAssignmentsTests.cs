using FluentAssertions;
using System.Text.Json;
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

    [TestMethod]
    public async Task SeededAssignments_ResolveOrganizationSlugAndAuthorizeWithoutSetupScript()
    {
        await using var harness = await Harness.CreateAsync(options =>
        {
            var client = options.ClientSeeds.Single(x => x.ClientId == "test-client");
            client.AccessMode = SqlOSApplicationAccessModes.SelectedOrganizations;
            client.AssignOrganization("primary-org", "allowed", description: "production tenant");
        });

        var client = await harness.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == "test-client");
        client.AccessMode.Should().Be(SqlOSApplicationAccessModes.SelectedOrganizations);
        var assignment = await harness.Context.Set<SqlOSApplicationAssignment>().SingleAsync(x => x.ClientApplicationId == client.Id);
        assignment.OrganizationId.Should().Be("org_allowed");
        assignment.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        assignment.ConfigurationSourceKey.Should().Be("primary-org");
        JsonSerializer.Serialize(await harness.Admin.ListApplicationAssignmentsAsync(client.Id)).Should()
            .Contain("\"Owner\":\"code\"").And.Contain("\"IsEditable\":false");

        var result = await harness.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest("ada@example.com", "P@ssword123!", "test-client", "org_allowed"),
            harness.Http);
        result.Tokens.Should().NotBeNull();
    }

    [TestMethod]
    public async Task SeededAssignments_RerunIsIdempotentAndPreservesDashboardAssignments()
    {
        await using var harness = await Harness.CreateAsync(options =>
        {
            var client = options.ClientSeeds.Single(x => x.ClientId == "test-client");
            client.AccessMode = SqlOSApplicationAccessModes.SelectedOrganizations;
            client.AssignOrganization("primary-org", "allowed");
        });
        var dashboard = await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_blocked"));

        await harness.Admin.UpsertSeededClientsAsync();

        var assignments = await harness.Context.Set<SqlOSApplicationAssignment>().ToListAsync();
        assignments.Should().HaveCount(2);
        assignments.Single(x => x.Id == dashboard.Id).ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Dashboard);
        assignments.Single(x => x.ConfigurationSourceKey == "primary-org").RevokedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task SeededClient_OmittedAccessModePreservesExistingStoredPolicy()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Admin.SetApplicationAccessModeAsync(
            "test-client",
            new SqlOSSetApplicationAccessModeRequest(SqlOSApplicationAccessModes.SelectedOrganizations));

        await harness.Admin.UpsertSeededClientsAsync();

        (await harness.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == "test-client"))
            .AccessMode.Should().Be(SqlOSApplicationAccessModes.SelectedOrganizations);
    }

    [TestMethod]
    public async Task SeededClient_AssignmentsRequireExplicitAccessMode()
    {
        var act = () => Harness.CreateAsync(options =>
            options.ClientSeeds.Single(x => x.ClientId == "test-client")
                .AssignOrganization("primary-org", "allowed"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must set AccessMode explicitly*");
    }

    [TestMethod]
    public async Task SeededAssignments_RemovalRevokesOnlyCodeOwnedAssignmentAndAuditsOutcome()
    {
        await using var harness = await Harness.CreateAsync(options =>
        {
            var client = options.ClientSeeds.Single(x => x.ClientId == "test-client");
            client.AccessMode = SqlOSApplicationAccessModes.SelectedOrganizations;
            client.AssignOrganization("primary-org", "allowed");
        });
        var dashboard = await harness.Admin.AssignApplicationAsync("test-client", new SqlOSCreateApplicationAssignmentRequest(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            OrganizationId: "org_blocked"));
        harness.Options.ClientSeeds.Single(x => x.ClientId == "test-client").Assignments.Clear();

        await harness.Admin.UpsertSeededClientsAsync();

        var codeOwned = await harness.Context.Set<SqlOSApplicationAssignment>().SingleAsync(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code);
        codeOwned.RevokedAt.Should().NotBeNull();
        codeOwned.ConfigurationOrphanedAt.Should().NotBeNull();
        (await harness.Context.Set<SqlOSApplicationAssignment>().SingleAsync(x => x.Id == dashboard.Id)).RevokedAt.Should().BeNull();
        (await harness.Context.Set<SqlOSAuditEvent>().Where(x => x.EventType == "configuration.reconciled").Select(x => x.DataJson).ToListAsync())
            .Should().Contain(x => x.Contains("application_assignment") && x.Contains("revoked"));
    }

    [TestMethod]
    public async Task SeededAssignments_InvalidOrCrossTenantPrincipalFailsClosed()
    {
        var act = () => Harness.CreateAsync(options =>
        {
            var client = options.ClientSeeds.Single(x => x.ClientId == "test-client");
            client.AccessMode = SqlOSApplicationAccessModes.SelectedUsersGroupsRoles;
            client.AssignUser("wrong-tenant", "missing-user", "allowed");
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found or is inactive*");
    }

    [TestMethod]
    public async Task ApplicationAssignments_RejectOverlongFieldsBeforePersistence()
    {
        await using var harness = await Harness.CreateAsync();
        var act = () => harness.Admin.AssignApplicationAsync(
            "test-client",
            new SqlOSCreateApplicationAssignmentRequest(
                SqlOSApplicationAssignmentPrincipalTypes.User,
                PrincipalId: new string('u', 129)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*principalId must be 128 characters or fewer*");
        (await harness.Context.Set<SqlOSApplicationAssignment>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededAssignments_CodeOwnedRowsAreReadOnlyThroughDashboardService()
    {
        await using var harness = await Harness.CreateAsync(options =>
        {
            var client = options.ClientSeeds.Single(x => x.ClientId == "test-client");
            client.AccessMode = SqlOSApplicationAccessModes.SelectedOrganizations;
            client.AssignOrganization("primary-org", "allowed");
        });
        var assignment = await harness.Context.Set<SqlOSApplicationAssignment>().SingleAsync();

        var revoke = () => harness.Admin.RevokeApplicationAssignmentAsync("test-client", assignment.Id);
        await revoke.Should().ThrowAsync<InvalidOperationException>().WithMessage("*owned by the 'code' configuration source*");
    }

    [TestMethod]
    public async Task SeededAssignments_AllSupportedPrincipalBuildersUseSharedValidation()
    {
        await using var harness = await Harness.CreateAsync();
        var group = await harness.SeedFgaGroupMembershipAsync();
        var (serviceAccountId, agentId) = await harness.SeedFgaMachinePrincipalsAsync();
        var client = harness.Options.ClientSeeds.Single(x => x.ClientId == "test-client");
        client.AccessMode = SqlOSApplicationAccessModes.SelectedUsersGroupsRoles;
        client.AssignOrganization("organization", "allowed")
            .AssignUser("user", harness.User.Id)
            .AssignGroup("group", group.Id)
            .AssignRole("role", "allowed", "admin")
            .AssignServiceAccount("service-account", serviceAccountId, "allowed")
            .AssignAgent("agent", agentId, "allowed");

        await harness.Admin.UpsertSeededClientsAsync();

        var assignments = await harness.Context.Set<SqlOSApplicationAssignment>()
            .Where(x => x.ConfigurationOwner == SqlOSConfigurationOwners.Code && x.RevokedAt == null)
            .ToListAsync();
        assignments.Should().HaveCount(6);
        assignments.Select(x => x.PrincipalType).Should().BeEquivalentTo(
            SqlOSApplicationAssignmentPrincipalTypes.Organization,
            SqlOSApplicationAssignmentPrincipalTypes.User,
            SqlOSApplicationAssignmentPrincipalTypes.Group,
            SqlOSApplicationAssignmentPrincipalTypes.Role,
            SqlOSApplicationAssignmentPrincipalTypes.ServiceAccount,
            SqlOSApplicationAssignmentPrincipalTypes.Agent);
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
        public SqlOSAuthServerOptions Options { get; private init; } = null!;

        public static async Task<Harness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
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
            configure?.Invoke(authOptions);

            var options = Microsoft.Extensions.Options.Options.Create(authOptions);
            var crypto = TestCryptoService.Create(context, options);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var mfaPolicy = new SqlOSMfaPolicyService(context, settings, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp, mfaPolicyService: mfaPolicy);
            var authorization = new SqlOSAuthorizationServerService(context, admin, auth, crypto, settings, authPageSession, options, mfaPolicyService: mfaPolicy);
            var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options, mfaPolicy);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

            await crypto.EnsureActiveSigningKeyAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await settings.EnsureDefaultMfaSettingsAsync();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Ada Lovelace", "ada@example.com", "P@ssword123!"));
            context.Set<SqlOSOrganization>().AddRange(
                new SqlOSOrganization { Id = "org_allowed", Slug = "allowed", Name = "Allowed Org", CreatedAt = DateTime.UtcNow, IsActive = true },
                new SqlOSOrganization { Id = "org_blocked", Slug = "blocked", Name = "Blocked Org", CreatedAt = DateTime.UtcNow, IsActive = true });
            context.Set<SqlOSMembership>().AddRange(
                new SqlOSMembership { OrganizationId = "org_allowed", UserId = user.Id, Role = "admin", CreatedAt = DateTime.UtcNow, IsActive = true },
                new SqlOSMembership { OrganizationId = "org_blocked", UserId = user.Id, Role = "member", CreatedAt = DateTime.UtcNow, IsActive = true });
            await context.SaveChangesAsync();

            await admin.UpsertSeededClientsAsync();

            return new Harness(context, admin, auth, authorization, device, crypto, user, http) { Options = authOptions };
        }

        public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync()
        {
            var request = await Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                "test-client",
                "https://client.example.test/callback",
                _crypto.GenerateOpaqueToken(),
                "openid offline_access",
                _crypto.CreatePkceCodeChallenge(_crypto.GenerateOpaqueToken()),
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

        public async Task<(string ServiceAccountId, string AgentId)> SeedFgaMachinePrincipalsAsync()
        {
            var serviceSubject = new SqlOSFgaSubject
            {
                Id = "subj_service_app",
                SubjectTypeId = "service_account",
                OrganizationId = "org_allowed",
                DisplayName = "Application worker",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var agentSubject = new SqlOSFgaSubject
            {
                Id = "subj_agent_app",
                SubjectTypeId = "agent",
                OrganizationId = "org_allowed",
                DisplayName = "Application agent",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var serviceAccount = new SqlOSFgaServiceAccount
            {
                Id = "sa_app",
                SubjectId = serviceSubject.Id,
                ClientId = "sa-app",
                ClientSecretHash = "not-used-in-assignment-test",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var agent = new SqlOSFgaAgent
            {
                Id = "agt_app",
                SubjectId = agentSubject.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Context.Set<SqlOSFgaSubject>().AddRange(serviceSubject, agentSubject);
            Context.Set<SqlOSFgaServiceAccount>().Add(serviceAccount);
            Context.Set<SqlOSFgaAgent>().Add(agent);
            await Context.SaveChangesAsync();
            return (serviceAccount.Id, agent.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}
