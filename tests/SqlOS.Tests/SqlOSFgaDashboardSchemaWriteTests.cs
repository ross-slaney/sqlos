using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Configuration;
using SqlOS.Dashboard;
using SqlOS.Fga.Dashboard;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSFgaDashboardSchemaWriteTests
{
    [TestMethod]
    public async Task SchemaWriteRoutes_AreRejected_AndDoNotPersist()
    {
        await using var harness = await FgaDashboardHarness.CreateAsync();

        var createRole = await harness.SendAsync(HttpMethods.Post, "/sqlos/admin/fga/api/roles", """{"key":"ops","name":"Ops"}""");
        await AssertSchemaWriteRejectedAsync(createRole);
        (await harness.RoleKeyExistsAsync("ops")).Should().BeFalse();

        var updateRole = await harness.SendAsync(HttpMethods.Put, "/sqlos/admin/fga/api/roles/admin", """{"name":"Renamed"}""");
        await AssertSchemaWriteRejectedAsync(updateRole);
        (await harness.GetRoleNameAsync("admin")).Should().Be("Admin");

        var deleteRole = await harness.SendAsync(HttpMethods.Delete, "/sqlos/admin/fga/api/roles/admin");
        await AssertSchemaWriteRejectedAsync(deleteRole);
        (await harness.RoleKeyExistsAsync("admin")).Should().BeTrue();

        var addPermission = await harness.SendAsync(
            HttpMethods.Post,
            "/sqlos/admin/fga/api/roles/admin/permissions",
            """{"permissionId":"extra"}""");
        await AssertSchemaWriteRejectedAsync(addPermission);
        (await harness.RoleHasPermissionAsync("admin", "extra")).Should().BeFalse();

        var removePermission = await harness.SendAsync(
            HttpMethods.Delete,
            "/sqlos/admin/fga/api/roles/admin/permissions/delete_users");
        await AssertSchemaWriteRejectedAsync(removePermission);
        (await harness.RoleHasPermissionAsync("admin", "delete_users")).Should().BeTrue();

        var createPermission = await harness.SendAsync(
            HttpMethods.Post,
            "/sqlos/admin/fga/api/permissions",
            """{"key":"NEW_PERM","name":"New permission"}""");
        await AssertSchemaWriteRejectedAsync(createPermission);
        (await harness.PermissionKeyExistsAsync("NEW_PERM")).Should().BeFalse();
    }

    [TestMethod]
    public async Task SchemaGetRoutes_StillSucceed()
    {
        await using var harness = await FgaDashboardHarness.CreateAsync();

        var role = await harness.SendAsync(HttpMethods.Get, "/sqlos/admin/fga/api/roles/admin");
        role.StatusCode.Should().Be(StatusCodes.Status200OK);
        role.Body.Should().Contain("\"key\":\"admin\"");

        var rolePermissions = await harness.SendAsync(HttpMethods.Get, "/sqlos/admin/fga/api/roles/admin/permissions");
        rolePermissions.StatusCode.Should().Be(StatusCodes.Status200OK);
        rolePermissions.Body.Should().Contain("delete_users");

        var permissions = await harness.SendAsync(HttpMethods.Get, "/sqlos/admin/fga/api/permissions");
        permissions.StatusCode.Should().Be(StatusCodes.Status200OK);
        permissions.Body.Should().Contain("delete_users");
    }

    [TestMethod]
    public async Task GrantCreateAndRevoke_StillSucceed()
    {
        await using var harness = await FgaDashboardHarness.CreateAsync();

        var created = await harness.SendAsync(
            HttpMethods.Post,
            "/sqlos/admin/fga/api/grants",
            """{"subjectId":"user-1","roleId":"admin","resourceId":"root"}""");
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        using var document = JsonDocument.Parse(created.Body);
        var grantId = document.RootElement.GetProperty("id").GetString();
        grantId.Should().NotBeNullOrWhiteSpace();
        (await harness.GrantExistsAsync(grantId!)).Should().BeTrue();

        var revoked = await harness.SendAsync(HttpMethods.Delete, $"/sqlos/admin/fga/api/grants/{grantId}");
        revoked.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        (await harness.GrantExistsAsync(grantId!)).Should().BeFalse();
    }

    private static async Task AssertSchemaWriteRejectedAsync(FgaDashboardResponse response)
    {
        response.StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
        response.Allow.Should().Be("GET");
        response.Body.Should().Contain(SqlOSFgaDashboardMiddleware.SchemaWriteError);
        await Task.CompletedTask;
    }

    private sealed class FgaDashboardHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SqlOSFgaDashboardMiddleware _middleware;

        private FgaDashboardHarness(ServiceProvider services, SqlOSFgaDashboardMiddleware middleware)
        {
            _services = services;
            _middleware = middleware;
        }

        public static async Task<FgaDashboardHarness> CreateAsync()
        {
            var services = new ServiceCollection();
            var databaseName = Guid.NewGuid().ToString("N");
            services.AddDataProtection();
            services.AddDbContext<TestSqlOSInMemoryDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddScoped<ISqlOSFgaDbContext>(sp => sp.GetRequiredService<TestSqlOSInMemoryDbContext>());
            services.AddSingleton(Options.Create(new SqlOSOptions()));
            services.AddSingleton<SqlOSDashboardSessionService>();

            var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var scope = provider.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
                context.Set<SqlOSFgaSubjectType>().Add(new SqlOSFgaSubjectType { Id = "user", Name = "User" });
                context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType { Id = "root", Name = "Root" });
                context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
                {
                    Id = "root",
                    Name = "Root",
                    ResourceTypeId = "root",
                    IsActive = true
                });
                context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
                {
                    Id = "user-1",
                    SubjectTypeId = "user",
                    DisplayName = "Ada"
                });
                context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole { Id = "admin", Key = "admin", Name = "Admin" });
                context.Set<SqlOSFgaPermission>().AddRange(
                    new SqlOSFgaPermission { Id = "delete_users", Key = "delete_users", Name = "Delete users", ResourceTypeId = "root" },
                    new SqlOSFgaPermission { Id = "extra", Key = "EXTRA_VIEW", Name = "Extra", ResourceTypeId = "root" });
                context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission
                {
                    RoleId = "admin",
                    PermissionId = "delete_users"
                });
                await context.SaveChangesAsync();
            }

            var middleware = new SqlOSFgaDashboardMiddleware(
                _ => Task.CompletedTask,
                "/sqlos/admin/fga",
                new TestHostEnvironment { EnvironmentName = Environments.Development },
                new SqlOSDashboardOptions { AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly },
                provider.GetRequiredService<SqlOSDashboardSessionService>(),
                Options.Create(new SqlOSOptions()));

            return new FgaDashboardHarness(provider, middleware);
        }

        public async Task<FgaDashboardResponse> SendAsync(string method, string path, string? body = null)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Scheme = Uri.UriSchemeHttps;
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.80");
            context.Response.Body = new MemoryStream();

            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = "application/json";
            }

            await _middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
            return new FgaDashboardResponse(
                context.Response.StatusCode,
                await reader.ReadToEndAsync(),
                context.Response.Headers.Allow.ToString());
        }

        public async Task<bool> RoleKeyExistsAsync(string roleKey)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return await context.Set<SqlOSFgaRole>().AnyAsync(x => x.Key == roleKey);
        }

        public async Task<string?> GetRoleNameAsync(string roleId)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return await context.Set<SqlOSFgaRole>().Where(x => x.Id == roleId).Select(x => x.Name).SingleOrDefaultAsync();
        }

        public async Task<bool> RoleHasPermissionAsync(string roleId, string permissionId)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return await context.Set<SqlOSFgaRolePermission>().AnyAsync(x => x.RoleId == roleId && x.PermissionId == permissionId);
        }

        public async Task<bool> PermissionKeyExistsAsync(string permissionKey)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return await context.Set<SqlOSFgaPermission>().AnyAsync(x => x.Key == permissionKey);
        }

        public async Task<bool> GrantExistsAsync(string grantId)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            return await context.Set<SqlOSFgaGrant>().AnyAsync(x => x.Id == grantId);
        }

        public async ValueTask DisposeAsync()
            => await _services.DisposeAsync();
    }

    private sealed record FgaDashboardResponse(int StatusCode, string Body, string Allow);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SqlOS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
