using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Extensions;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSErgonomicsExtensionsTests
{
    [TestMethod]
    public void SqlOSToken_AndSqlOSUserId_ReadValidatedToken()
    {
        var httpContext = new DefaultHttpContext();
        var token = new SqlOS.AuthServer.Contracts.SqlOSValidatedToken(
            new System.Security.Claims.ClaimsPrincipal(),
            "sess_1",
            "usr_1",
            "org_1",
            "client_1",
            "api");
        httpContext.Items[SqlOSAccessTokenValidationExtensions.ValidatedTokenItemKey] = token;

        httpContext.SqlOSToken().Should().BeSameAs(token);
        httpContext.SqlOSUserId().Should().Be("usr_1");
    }

    [TestMethod]
    public async Task Allows_ReturnsCheckAccessDecision()
    {
        var allowed = await new FakeFgaAuthService(true).Allows("usr_1", "READ", "res_1");
        var denied = await new FakeFgaAuthService(false).Allows("usr_1", "READ", "res_1");

        allowed.Should().BeTrue();
        denied.Should().BeFalse();
    }

    [TestMethod]
    public async Task SubjectResourceAndGrantHelpers_ProvisionExplicitlyAndGrantExistingRows()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var user = await context.ProvisionUserSubjectAsync("usr_1", "User One", "user@example.test");
        var agent = await context.ProvisionAgentSubjectAsync("agt_1", "Agent One", "worker", "Background worker");
        var serviceAccount = await context.ProvisionServiceAccountSubjectAsync(
            "sa_1",
            "Service Account One",
            "client_1",
            "hashed-secret",
            "API integration");
        var resource = await context.CreateResourceWithIdAsync("workspace_1", "workspace", "Workspace 1", "root");
        var grant = await context.GrantRoleAsync("usr_1", "workspace_1", "owner");
        await context.SaveChangesAsync();

        user.SubjectId.Should().Be("usr_1");
        agent.SubjectId.Should().Be("agt_1");
        serviceAccount.SubjectId.Should().Be("sa_1");
        resource.Id.Should().Be("workspace_1");
        grant.RoleId.Should().Be("role_owner");
        (await context.Set<SqlOSFgaUser>().SingleAsync(x => x.SubjectId == "usr_1")).Email.Should().Be("user@example.test");
        (await context.Set<SqlOSFgaAgent>().SingleAsync(x => x.SubjectId == "agt_1")).AgentType.Should().Be("worker");
        (await context.Set<SqlOSFgaServiceAccount>().SingleAsync(x => x.SubjectId == "sa_1")).ClientId.Should().Be("client_1");
    }

    [TestMethod]
    public void LegacyErgonomicsHelperNames_AreNotPublic()
    {
        var legacyNames = new[]
        {
            string.Concat("Ensure", "SqlOS", "User", "Subject", "Async"),
            string.Concat("Ensure", "SqlOS", "Agent", "Subject", "Async"),
            string.Concat("Ensure", "SqlOS", "Service", "Account", "Subject", "Async"),
            string.Concat("Ensure", "SqlOS", "Resource", "Async"),
            string.Concat("Ensure", "SqlOS", "Role", "Grant", "Async"),
            string.Concat("Grant", "SqlOS", "Role"),
            string.Concat("Grant", "SqlOS", "Role", "Async"),
            string.Concat("Add", "SqlOS", "Resource")
        };

        typeof(SqlOSErgonomicsExtensions)
            .GetMethods()
            .Where(method => legacyNames.Contains(method.Name, StringComparer.Ordinal))
            .Should()
            .BeEmpty("the ergonomics API should have one canonical path before it ships");
    }

    [TestMethod]
    public async Task GrantRoleAsync_RequiresExistingSubjectAndResource()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var missingSubject = async () => await context.GrantRoleAsync("missing_user", "root", "owner");
        await missingSubject.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*subject 'missing_user' was not found*");

        await context.ProvisionUserSubjectAsync("usr_1", "User One");
        var missingResource = async () => await context.GrantRoleAsync("usr_1", "missing_resource", "owner");
        await missingResource.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource 'missing_resource' was not found*");
    }

    [TestMethod]
    public async Task CreateResourceWithIdAsync_RequiresExistingParentResource()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var missingParent = async () => await context.CreateResourceWithIdAsync(
            "workspace_1",
            "workspace",
            "Workspace 1",
            "missing_parent");

        await missingParent.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource 'missing_parent' was not found*");
        context.Set<SqlOSFgaResource>().Local.Should().NotContain(x => x.Id == "workspace_1");
    }

    [TestMethod]
    public async Task CreateResourceWithIdAsync_RequiresExistingResourceType()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var missingResourceType = async () => await context.CreateResourceWithIdAsync(
            "workspace_1",
            "workpsace",
            "Workspace 1",
            "root");

        await missingResourceType.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource type 'workpsace' was not found*");
        context.Set<SqlOSFgaResource>().Local.Should().NotContain(x => x.Id == "workspace_1");
    }

    [TestMethod]
    public async Task ProvisionResourceWithIdAsync_ValidatesParentAndResourceTypeWhenUpdating()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        await context.CreateResourceWithIdAsync("workspace_1", "workspace", "Workspace 1", "root");
        await context.SaveChangesAsync();

        var missingParent = async () => await context.ProvisionResourceWithIdAsync(
            "workspace_1",
            "workspace",
            "Workspace 1",
            "missing_parent");
        await missingParent.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource 'missing_parent' was not found*");

        var missingResourceType = async () => await context.ProvisionResourceWithIdAsync(
            "workspace_1",
            "workpsace",
            "Workspace 1",
            "root");
        await missingResourceType.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource type 'workpsace' was not found*");

        var resource = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_1");
        resource.ParentId.Should().Be("root");
        resource.ResourceTypeId.Should().Be("workspace");
    }

    [TestMethod]
    public async Task ProvisionSubjectHelpers_PreserveExistingOptionalMetadataWhenArgumentsAreOmitted()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        await context.ProvisionUserSubjectAsync(
            "usr_1",
            "User One",
            "user@example.test",
            "org_1",
            "external_1",
            false);
        await context.ProvisionAgentSubjectAsync(
            "agt_1",
            "Agent One",
            "worker",
            "Initial description",
            "org_1",
            "agent_external_1");
        await context.ProvisionServiceAccountSubjectAsync(
            "sa_1",
            "Service Account One",
            "client_1",
            "secret_1",
            "Initial account",
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "org_1",
            "sa_external_1");
        await context.SaveChangesAsync();

        await context.ProvisionUserSubjectAsync("usr_1", "User One Updated");
        await context.ProvisionAgentSubjectAsync("agt_1", "Agent One Updated");
        await context.ProvisionServiceAccountSubjectAsync("sa_1", "Service Account One Updated", "client_2", "secret_2");
        await context.SaveChangesAsync();

        var subject = await context.Set<SqlOSFgaSubject>().SingleAsync(x => x.Id == "usr_1");
        subject.DisplayName.Should().Be("User One Updated");
        subject.OrganizationId.Should().Be("org_1");
        subject.ExternalRef.Should().Be("external_1");

        var user = await context.Set<SqlOSFgaUser>().SingleAsync(x => x.SubjectId == "usr_1");
        user.Email.Should().Be("user@example.test");
        user.IsActive.Should().BeFalse();

        var agentSubject = await context.Set<SqlOSFgaSubject>().SingleAsync(x => x.Id == "agt_1");
        agentSubject.OrganizationId.Should().Be("org_1");
        agentSubject.ExternalRef.Should().Be("agent_external_1");
        var agent = await context.Set<SqlOSFgaAgent>().SingleAsync(x => x.SubjectId == "agt_1");
        agent.AgentType.Should().Be("worker");
        agent.Description.Should().Be("Initial description");

        var serviceAccountSubject = await context.Set<SqlOSFgaSubject>().SingleAsync(x => x.Id == "sa_1");
        serviceAccountSubject.OrganizationId.Should().Be("org_1");
        serviceAccountSubject.ExternalRef.Should().Be("sa_external_1");
        var serviceAccount = await context.Set<SqlOSFgaServiceAccount>().SingleAsync(x => x.SubjectId == "sa_1");
        serviceAccount.ClientId.Should().Be("client_2");
        serviceAccount.ClientSecretHash.Should().Be("secret_2");
        serviceAccount.Description.Should().Be("Initial account");
        serviceAccount.ExpiresAt.Should().Be(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    public async Task ManualResourceApis_CreateProvisionAndDeleteResourceAndGrants()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.ProvisionUserSubjectAsync("usr_1", "User One");
        await context.SaveChangesAsync();

        await context.CreateResourceWithIdAsync(
            "workspace_1",
            "workspace",
            "Workspace 1",
            "root",
            "Initial resource");
        await context.SaveChangesAsync();

        var duplicateCreate = async () => await context.CreateResourceWithIdAsync(
            "workspace_1",
            "workspace",
            "Workspace Duplicate",
            "root");
        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource 'workspace_1' already exists*");

        await context.ProvisionResourceWithIdAsync(
            "workspace_1",
            "workspace",
            "Workspace One",
            "root");
        await context.GrantRoleAsync("usr_1", "workspace_1", "owner");
        await context.SaveChangesAsync();

        var resource = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_1");
        resource.Name.Should().Be("Workspace One");
        resource.Description.Should().Be("Initial resource");
        context.Set<SqlOSFgaGrant>().Count(x => x.ResourceId == "workspace_1").Should().Be(1);

        await context.DeleteResourceAsync("workspace_1");
        await context.SaveChangesAsync();

        (await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == "workspace_1")).Should().BeFalse();
        (await context.Set<SqlOSFgaGrant>().AnyAsync(x => x.ResourceId == "workspace_1")).Should().BeFalse();
    }

    [TestMethod]
    public async Task ProvisionResourceWithIdAsync_PreservesExistingParentWhenParentIsOmitted()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.CreateResourceWithIdAsync("workspace_parent", "workspace", "Workspace parent", "root");
        await context.CreateResourceWithIdAsync("workspace_child", "workspace", "Workspace child", "workspace_parent");
        await context.SaveChangesAsync();

        await context.ProvisionResourceWithIdAsync("workspace_child", "workspace", "Workspace child renamed");
        await context.SaveChangesAsync();

        var resource = await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "workspace_child");
        resource.Name.Should().Be("Workspace child renamed");
        resource.ParentId.Should().Be("workspace_parent");
    }

    [TestMethod]
    public async Task CreateResourceAsync_GeneratesResourceId()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var resource = await context.CreateResourceAsync(
            "workspace",
            "Generated workspace",
            "root");
        await context.SaveChangesAsync();

        resource.Id.Should().StartWith("workspace::");
        (await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == resource.Id)).Should().BeTrue();
    }

    [TestMethod]
    public async Task DeleteResourceAsync_FailsWhenChildResourcesStillExist()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.CreateResourceWithIdAsync("workspace_1", "workspace", "Workspace 1", "root");
        await context.CreateResourceWithIdAsync("workspace_child", "workspace", "Workspace child", "workspace_1");
        await context.SaveChangesAsync();

        var deleteParent = async () => await context.DeleteResourceAsync("workspace_1");

        await deleteParent.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has child resources*Delete or reparent child resources*");
    }

    [TestMethod]
    public async Task GrantRoleAsync_ResolvesRoleKeyOrIdAndIsIdempotent()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.ProvisionUserSubjectAsync("usr_1", "User One");
        await context.SaveChangesAsync();

        var byKey = await context.GrantRoleAsync("usr_1", "root", "owner");
        var byId = await context.GrantRoleAsync("usr_1", "root", "role_owner");
        await context.SaveChangesAsync();

        byKey.RoleId.Should().Be("role_owner");
        byId.RoleId.Should().Be("role_owner");
        context.Set<SqlOSFgaGrant>().Count(x => x.SubjectId == "usr_1" && x.ResourceId == "root").Should().Be(1);

        await context.RevokeRoleAsync("usr_1", "root", "owner");
        await context.SaveChangesAsync();

        context.Set<SqlOSFgaGrant>().Count(x => x.SubjectId == "usr_1" && x.ResourceId == "root").Should().Be(0);
    }

    [TestMethod]
    public async Task GrantRoleAsync_RequiresExistingSubjectAndResourceWithoutProvisioningSubjects()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var missingSubject = async () => await context.GrantRoleAsync("missing_user", "root", "owner");
        await missingSubject.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*subject 'missing_user' was not found*");

        await context.ProvisionUserSubjectAsync("usr_1", "User One");
        var missingResource = async () => await context.GrantRoleAsync("usr_1", "missing_resource", "owner");
        await missingResource.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resource 'missing_resource' was not found*");

        context.Set<SqlOSFgaSubject>().Local.Should().NotContain(x => x.Id == "missing_user");
    }

    [TestMethod]
    public void FgaSeedDsl_CreatesPermissionsRolesAndRolePermissions()
    {
        var seed = new SqlOSFgaSeedBuilder();

        seed.ResourceType("workspace", "Workspace");
        seed.Permission("CHECKLIST_READ", "Read", "workspace");
        seed.Permission("CHECKLIST_WRITE", "Write", "workspace");
        seed.Role("owner", "Owner").Can("CHECKLIST_READ", "CHECKLIST_WRITE");

        var data = seed.Build();

        data.Permissions.Should().ContainSingle(x =>
            x.Id == "CHECKLIST_READ"
            && x.Key == "CHECKLIST_READ"
            && x.Name == "Read"
            && x.ResourceTypeId == "workspace");
        data.Roles.Should().ContainSingle(x => x.Id == "owner" && x.Key == "owner" && x.Name == "Owner");
        var rolePermissions = data.RolePermissions.Should().ContainSingle(x => x.RoleKey == "owner").Subject;
        rolePermissions.PermissionKeys.Should().Equal("CHECKLIST_READ", "CHECKLIST_WRITE");
    }

    [TestMethod]
    public void SeedDeviceFlowClient_CreatesDeviceFlowPublicClient()
    {
        var options = new SqlOSAuthServerOptions();

        options.SeedDeviceFlowClient("checklist-cli", "Checklist CLI", "https://api.example.test", "READ", "WRITE");

        var client = options.ClientSeeds.Should().ContainSingle().Subject;
        client.ClientId.Should().Be("checklist-cli");
        client.Name.Should().Be("Checklist CLI");
        client.Audience.Should().Be("https://api.example.test");
        client.ClientType.Should().Be("public_cli");
        client.RequirePkce.Should().BeTrue();
        client.AllowDeviceAuthorization.Should().BeTrue();
        client.AllowedScopes.Should().Equal("READ", "WRITE");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static void SeedFgaCore(TestSqlOSInMemoryDbContext context)
    {
        context.Set<SqlOSFgaSubjectType>().AddRange(
            new SqlOSFgaSubjectType { Id = "user", Name = "User" },
            new SqlOSFgaSubjectType { Id = "agent", Name = "Agent" },
            new SqlOSFgaSubjectType { Id = "service_account", Name = "Service Account" });
        context.Set<SqlOSFgaResourceType>().AddRange(
            new SqlOSFgaResourceType { Id = "root", Name = "Root" },
            new SqlOSFgaResourceType { Id = "workspace", Name = "Workspace" });
        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "root",
            Name = "Root",
            ResourceTypeId = "root"
        });
        context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole
        {
            Id = "role_owner",
            Key = "owner",
            Name = "Owner"
        });
    }

    private sealed class FakeFgaAuthService(bool allowed) : ISqlOSFgaAuthService
    {
        public Task<SqlOSFgaAccessCheckResult> CheckAccessAsync(string subjectId, string permissionKey, string resourceId)
            => Task.FromResult(new SqlOSFgaAccessCheckResult { Allowed = allowed });

        public Task<bool> HasCapabilityAsync(string subjectId, string permissionKey)
            => Task.FromResult(allowed);

        public Task<SqlOSFgaResourceAccessTrace> TraceResourceAccessAsync(string subjectId, string resourceId, string permissionKey)
            => throw new NotImplementedException();

        public Task<Expression<Func<T, bool>>> GetAuthorizationFilterAsync<T>(string subjectId, string permissionKey)
            where T : IHasResourceId
            => throw new NotImplementedException();
    }
}
