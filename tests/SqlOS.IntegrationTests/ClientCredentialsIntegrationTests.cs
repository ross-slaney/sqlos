using FluentAssertions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class ClientCredentialsIntegrationTests
{
    [TestMethod]
    public async Task UnifiedMachineClient_RealSql_AtomicallyProvisionsRotatesAndRevokesProtocolIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"unified-{suffix}";
        var audience = $"https://api.example.test/unified/{suffix}";
        var context = AspireFixture.SharedContext;
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var machines = new SqlOSMachineClientAdminService(context, admin, crypto, options);
        var protocol = new SqlOSClientCredentialsService(context, crypto, admin, options);
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Unified {suffix}", $"unified-{suffix}"));
        var resourceTypeId = await context.Set<SqlOSFgaResourceType>().Select(x => x.Id).FirstAsync();
        var role = new SqlOSFgaRole { Id = $"role_{suffix}", Key = $"runner-{suffix}", Name = "Runner" };
        var resource = new SqlOSFgaResource { Id = $"res_{suffix}", ResourceTypeId = resourceTypeId, Name = "Jobs", IsActive = true };
        context.Set<SqlOSFgaRole>().Add(role);
        context.Set<SqlOSFgaResource>().Add(resource);
        await context.SaveChangesAsync();

        var created = await machines.CreateAsync(new SqlOSCreateMachineClientRequest(
            clientId, "Unified worker", null, audience, ["jobs.run"], organization.Id, null, [new(resource.Id, role.Id)]));
        var issued = await protocol.ExchangeAsync(clientId, created.ClientSecret, audience, "jobs.run", new DefaultHttpContext(), default);
        (await crypto.ValidateAccessTokenAsync(issued.AccessToken, audience)).Should().NotBeNull();
        var account = await context.Set<SqlOSFgaServiceAccount>().SingleAsync(x => x.ClientId == clientId);
        (await context.Set<SqlOSFgaGrant>().AnyAsync(x => x.SubjectId == account.SubjectId && x.ResourceId == resource.Id && x.RoleId == role.Id)).Should().BeTrue();

        var rotated = await machines.RotateAsync(clientId);
        await FluentActions.Invoking(() => protocol.ExchangeAsync(clientId, created.ClientSecret, audience, "jobs.run", new DefaultHttpContext(), default))
            .Should().ThrowAsync<SqlOSClientCredentialsException>();
        (await protocol.ExchangeAsync(clientId, rotated.ClientSecret, audience, "jobs.run", new DefaultHttpContext(), default)).AccessToken.Should().NotBeNullOrWhiteSpace();

        await machines.RevokeAsync(clientId);
        (await crypto.ValidateAccessTokenAsync(issued.AccessToken, audience)).Should().BeNull();
        JsonSerializer.Serialize(await context.Set<SqlOSAuditEvent>().Where(x => x.ActorId == clientId || x.DataJson!.Contains(clientId)).ToListAsync())
            .Should().NotContain(created.ClientSecret).And.NotContain(rotated.ClientSecret);
    }

    [TestMethod]
    public async Task ClientCredentials_RealSql_IssuesValidServiceTokenAndRevokesWithoutHumanSession()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"worker-{suffix}";
        var subjectId = $"service_account::{clientId}";
        var audience = $"https://api.example.test/jobs/{suffix}";
        var secret = $"integration-secret-{suffix}-with-sufficient-entropy";
        var context = AspireFixture.SharedContext;
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var service = new SqlOSClientCredentialsService(context, crypto, admin, options);

        context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
        {
            Id = $"cli_{suffix}",
            ClientId = clientId,
            Name = "Integration Worker",
            Audience = audience,
            ClientType = "confidential",
            TokenEndpointAuthMethod = "client_secret_basic",
            GrantTypesJson = "[\"client_credentials\"]",
            AllowedScopesJson = "[\"jobs.run\"]",
            RedirectUrisJson = "[]",
            RequirePkce = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
        {
            Id = subjectId,
            SubjectTypeId = "service_account",
            DisplayName = "Integration Worker"
        });
        context.Set<SqlOSFgaServiceAccount>().Add(new SqlOSFgaServiceAccount
        {
            Id = $"sa_{suffix}",
            SubjectId = subjectId,
            ClientId = clientId,
            ClientSecretHash = crypto.HashPassword(secret)
        });
        await context.SaveChangesAsync();

        var issued = await service.ExchangeAsync(
            clientId, secret, audience, "jobs.run", new DefaultHttpContext(), default);
        var validated = await crypto.ValidateAccessTokenAsync(issued.AccessToken, audience);

        validated.Should().NotBeNull();
        validated!.UserId.Should().BeNull();
        validated.SessionId.Should().BeEmpty();
        validated.Principal.FindFirst("sub")!.Value.Should().Be(subjectId);
        (await crypto.ValidateAccessTokenAsync(issued.AccessToken, "https://api.example.test/wrong"))
            .Should().BeNull();

        await service.RevokeAsync(clientId, "integration-admin");
        (await crypto.ValidateAccessTokenAsync(issued.AccessToken, audience)).Should().BeNull();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "oauth.client_credentials.issued" && x.ActorId == clientId)).Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "oauth.client_credentials.revoked")).Should().BeTrue();
    }
}
