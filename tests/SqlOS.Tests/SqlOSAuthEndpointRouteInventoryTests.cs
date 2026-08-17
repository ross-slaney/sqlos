using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public class SqlOSAuthEndpointRouteInventoryTests
{
    [TestMethod]
    public async Task MapAuthServer_RegistersCriticalRoutesWithUnchangedMethods()
    {
        await using var app = await CreateAppAsync();
        var actual = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.Ordinal);

        var expected = new[]
        {
            "GET /sqlos/auth/.well-known/oauth-authorization-server",
            "GET /sqlos/auth/.well-known/jwks.json",
            "GET /sqlos/auth/authorize",
            "GET /sqlos/auth/continue",
            "POST /sqlos/auth/token",
            "POST /sqlos/auth/register",
            "GET /sqlos/auth/login",
            "POST /sqlos/auth/login/password",
            "GET /sqlos/auth/signup",
            "POST /sqlos/auth/signup/submit",
            "POST /sqlos/auth/headless/start",
            "POST /sqlos/auth/headless/password/login",
            "GET /sqlos/admin/auth/api/organizations",
            "GET /sqlos/admin/auth/api/users",
            "GET /sqlos/admin/auth/api/clients",
            "POST /sqlos/admin/auth/api/clients/{clientId}/emergency-disable",
            "POST /sqlos/admin/auth/api/clients/{clientId}/emergency-enable",
            "GET /sqlos/admin/auth/api/clients/{clientId}/credentials",
            "POST /sqlos/admin/auth/api/clients/{clientId}/credentials",
            "DELETE /sqlos/admin/auth/api/clients/{clientId}/credentials/{credentialId}",
            "GET /sqlos/admin/auth/api/sessions",
            "POST /sqlos/admin/auth/api/sessions/revocation/preview",
            "POST /sqlos/admin/auth/api/sessions/revocation",
            "GET /sqlos/admin/auth/api/settings/security",
            "GET /sqlos/admin/auth/api/otp/readiness",
            "POST /sqlos/admin/auth/api/otp/test-delivery",
            "GET /sqlos/admin/auth/api/machine-clients",
            "POST /sqlos/admin/auth/api/machine-clients"
        };

        actual.Should().Contain(expected);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(database =>
            database.UseInMemoryDatabase($"endpoint-inventory-{Guid.NewGuid():N}"));
        builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Issuer = "https://auth.example.test/sqlos/auth";
            options.AuthServer.ClientRegistration.Dcr.Enabled = true;
            options.AuthServer.Headless.EnableApi = true;
        });
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.MapSqlOS();
        await app.StartAsync();
        return app;
    }
}
