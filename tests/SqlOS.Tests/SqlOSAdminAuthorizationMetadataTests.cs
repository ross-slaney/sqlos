using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Dashboard;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAdminAuthorizationMetadataTests
{
    private const string PortalPrefix = "/sqlos/admin/auth/sso-portal";
    private const string PortalApiPrefix = $"{PortalPrefix}/api";

    [TestMethod]
    public async Task AdminRouteInventory_RequiresCentralAuthorizationOrExplicitException()
    {
        await using var app = await CreateAppAsync(Environments.Production);

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/admin/", StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() != null)
            .ToArray();

        endpoints.Should().NotBeEmpty();
        foreach (var endpoint in endpoints)
        {
            var path = endpoint.RoutePattern.RawText!;
            var requiresAdmin = endpoint.Metadata.GetMetadata<SqlOSAdminRequiredMetadata>() != null;
            var publicException = endpoint.Metadata.GetMetadata<SqlOSAdminPublicExceptionMetadata>();

            (requiresAdmin ^ (publicException != null)).Should().BeTrue(
                $"{path} must be centrally protected or carry one explicit public-exception marker");

            if (publicException != null)
            {
                path.Should().StartWith(PortalPrefix);
                publicException.Reason.Should().Contain("portal session");
            }
        }
    }

    [TestMethod]
    public async Task AdminAuthorizationFilter_MissingSqlOSOptions_FailsClosedInDevelopment()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();

        await using var app = builder.Build();
        app.MapGroup("/admin")
            .RequireSqlOSAdminAuthorization()
            .MapGet("/probe", () => Results.Ok());
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/admin/probe");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task AdminApiRouteInventory_AllHttpMethodsFailClosedWithoutDashboardAuthorization()
    {
        await using var app = await CreateAppAsync(Environments.Production);
        var client = app.GetTestClient();

        var responses = new[]
        {
            await client.GetAsync("/sqlos/admin/auth/api/stats"),
            await client.PostAsJsonAsync("/sqlos/admin/auth/api/users", new { }),
            await client.PostAsJsonAsync("/sqlos/admin/auth/api/sessions/revocation/preview", new { userId = "victim" }),
            await client.PostAsJsonAsync("/sqlos/admin/auth/api/sessions/revocation", new { userId = "victim", confirm = true }),
            await client.GetAsync("/sqlos/admin/auth/api/otp/readiness"),
            await client.PostAsJsonAsync("/sqlos/admin/auth/api/otp/test-delivery", new { method = "email", destination = "operator@example.test" }),
            await client.GetAsync("/sqlos/admin/auth/api/machine-clients"),
            await client.PostAsJsonAsync("/sqlos/admin/auth/api/machine-clients", new { }),
            await client.PutAsJsonAsync("/sqlos/admin/email/api/templates/missing", new { }),
            await client.DeleteAsync("/sqlos/admin/email/api/templates/missing")
        };

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task PortalSessionApis_AreExplicitExceptions_NotDashboardAdminRoutes()
    {
        await using var app = await CreateAppAsync(Environments.Production);

        var portalEndpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(PortalApiPrefix, StringComparison.Ordinal) == true)
            .ToArray();

        portalEndpoints.Should().NotBeEmpty();
        portalEndpoints.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<SqlOSAdminPublicExceptionMetadata>() != null
            && endpoint.Metadata.GetMetadata<SqlOSAdminRequiredMetadata>() == null);
    }

    private static async Task<WebApplication> CreateAppAsync(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(database =>
            database.UseInMemoryDatabase($"admin-auth-routes-{Guid.NewGuid():N}"));
        builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Issuer = "https://auth.example.test/sqlos/auth";
            options.AuthServer.SsoPortal.UseHostedPortal = true;
            options.AuthServer.SsoPortal.EnableApi = true;
            options.Calendar.Enabled = true;
        });
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.MapSqlOS();
        await app.StartAsync();
        return app;
    }
}
