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
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Services;
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
            await client.PostAsync("/sqlos/admin/auth/api/machine-clients/parity-worker/revoke", null),
            await client.PostAsync("/sqlos/admin/auth/api/machine-clients/parity-worker/emergency-disable", null),
            await client.PostAsync("/sqlos/admin/auth/api/machine-clients/parity-worker/emergency-enable", null),
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

    [TestMethod]
    public async Task DeactivatedOrganization_PortalRoutesReturnOnlyGenericCapabilityErrors()
    {
        await using var app = await CreateAppAsync(Environments.Production);
        string setupToken;
        string portalCookie;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var portal = scope.ServiceProvider.GetRequiredService<SqlOSSsoPortalService>();
            var organization = await admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(
                    "Generic Portal Error Org",
                    null,
                    "generic-portal-error.test"));
            var pending = await portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id));
            setupToken = ExtractSetupToken(pending.SetupUrl!);
            var opened = await portal.CreateSessionAsync(
                new SqlOSCreateSsoPortalSessionRequest(organization.Id));
            var openContext = new DefaultHttpContext();
            await portal.OpenSessionAsync(ExtractSetupToken(opened.SetupUrl!), openContext);
            portalCookie = openContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

            await admin.UpdateOrganizationAsync(
                organization.Id,
                new SqlOSUpdateOrganizationRequest(
                    organization.Name,
                    organization.Slug,
                    organization.PrimaryDomain,
                    IsActive: false));
        }

        var client = app.GetTestClient();
        var startResponse = await client.GetAsync(
            $"{PortalPrefix}/start?token={Uri.EscapeDataString(setupToken)}");
        startResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var startBody = await startResponse.Content.ReadAsStringAsync();
        startBody.Should().Contain("Portal setup token is invalid or expired.");
        startBody.ToLowerInvariant().Should().NotContain("organization");

        using var stateRequest = new HttpRequestMessage(HttpMethod.Get, $"{PortalApiPrefix}/state");
        stateRequest.Headers.Add("Cookie", portalCookie);
        var stateResponse = await client.SendAsync(stateRequest);
        stateResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await stateResponse.Content.ReadAsStringAsync())
            .Should().Contain("Portal session is invalid or expired.")
            .And.NotContain("organization");
    }

    [TestMethod]
    public async Task PortalSessionAvailabilityFilter_MapsRevalidationRaceToGenericUnauthorized()
    {
        var filter = new SqlOSSsoPortalSessionAvailabilityFilter();
        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(new DefaultHttpContext()),
            _ => ValueTask.FromException<object?>(
                new SqlOSSsoPortalSessionUnavailableException()));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ((IValueHttpResult)result!).Value.Should().BeEquivalentTo(
            new { message = "Portal session is invalid or expired." });
    }

    private static string ExtractSetupToken(string setupUrl)
    {
        var query = new Uri(setupUrl).Query;
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)["token"].ToString();
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

    private sealed class TestEndpointFilterInvocationContext(HttpContext httpContext)
        : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index)
            => (T)Arguments[index]!;
    }
}
