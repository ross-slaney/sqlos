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
    public async Task ResourceAndGrantHelpers_AddAndEnsureFgaRowsWithoutManualModelTypes()
    {
        using var context = CreateContext();
        SeedFgaCore(context);
        await context.SaveChangesAsync();

        var resource = context.AddSqlOSResource("workspace_1", "root", "Workspace 1", "workspace");
        var grant = context.GrantSqlOSRole("usr_1", "workspace_1", "owner", "Workspace owner");
        await context.SaveChangesAsync();

        resource.Id.Should().Be("workspace_1");
        grant.RoleId.Should().Be("role_owner");
        (await context.Set<SqlOSFgaSubject>().FindAsync("usr_1")).Should().NotBeNull();

        var ensuredResource = await context.EnsureSqlOSResourceAsync(
            "workspace_1",
            "root",
            "Workspace One",
            "workspace",
            "Updated");
        var ensuredGrant = await context.EnsureSqlOSRoleGrantAsync(
            "usr_1",
            "workspace_1",
            "owner",
            "Updated owner grant");
        await context.SaveChangesAsync();

        ensuredResource.Name.Should().Be("Workspace One");
        ensuredResource.Description.Should().Be("Updated");
        ensuredGrant.Description.Should().Be("Updated owner grant");
        context.Set<SqlOSFgaGrant>().Count(x => x.SubjectId == "usr_1" && x.ResourceId == "workspace_1").Should().Be(1);
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
    public void SeedMcpStackClient_CreatesDeviceFlowPublicClient()
    {
        var options = new SqlOSAuthServerOptions();

        options.SeedMcpStackClient("checklist-mcpstack", "MCP Stack", "https://api.example.test", "READ", "WRITE");

        var client = options.ClientSeeds.Should().ContainSingle().Subject;
        client.ClientId.Should().Be("checklist-mcpstack");
        client.Name.Should().Be("MCP Stack");
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
        context.Set<SqlOSFgaSubjectType>().Add(new SqlOSFgaSubjectType { Id = "user", Name = "User" });
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
