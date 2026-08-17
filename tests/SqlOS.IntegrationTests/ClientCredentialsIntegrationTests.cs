using FluentAssertions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class ClientCredentialsIntegrationTests
{
    [TestMethod]
    public async Task CredentialCreation_ConcurrentRealSqlRequests_EnforcesActiveLimitAtomically()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("CredentialLimit");
        try
        {
            var options = Options.Create(new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            });
            var setupCrypto = new SqlOSCryptoService(setup, options, AspireFixture.DataProtectionProvider);
            var clientId = $"credential-limit-{Guid.NewGuid():N}";
            var client = new SqlOSClientApplication
            {
                Id = $"cli_{Guid.NewGuid():N}",
                ClientId = clientId,
                Name = "Credential limit client",
                Audience = "sqlos",
                ClientType = "confidential",
                TokenEndpointAuthMethod = "client_secret_basic",
                GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
                RedirectUrisJson = "[\"https://client.example.test/callback\"]",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            setup.Set<SqlOSClientApplication>().Add(client);
            const string retainedSecret = "retained-secret-with-at-least-256-bits-of-entropy-123456789";
            for (var index = 0; index < 4; index++)
            {
                setup.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
                {
                    Id = $"clcred_{Guid.NewGuid():N}",
                    ClientApplicationId = client.Id,
                    SecretHash = setupCrypto.HashPassword(index == 0
                        ? retainedSecret
                        : $"credential-{index}-with-at-least-256-bits-of-entropy-123456789"),
                    DisplayName = $"Existing {index}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-index)
                });
            }
            await setup.SaveChangesAsync();

            var connectionString = setup.Database.GetConnectionString()!;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<(SqlOSClientCredentialCreated? Created, Exception? Error)> CreateAsync(string label)
            {
                await using var context = new TestSqlOSDbContext(
                    new DbContextOptionsBuilder<TestSqlOSDbContext>()
                        .UseSqlServer(connectionString)
                        .Options);
                var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
                var service = new SqlOSClientAuthenticationService(
                    context,
                    crypto,
                    new SqlOSAdminService(context, options, crypto));
                await ready.Task;
                try
                {
                    return (await service.CreateCredentialAsync(client.Id, label), null);
                }
                catch (Exception ex)
                {
                    return (null, ex);
                }
            }

            var first = CreateAsync("Concurrent A");
            var second = CreateAsync("Concurrent B");
            ready.SetResult();
            var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(45));

            outcomes.Should().ContainSingle(x => x.Created != null);
            outcomes.Should().ContainSingle(x =>
                x.Error is InvalidOperationException
                && x.Error.Message.Contains("at most 5 active credentials", StringComparison.Ordinal));

            setup.ChangeTracker.Clear();
            (await setup.Set<SqlOSClientCredential>()
                    .CountAsync(x => x.ClientApplicationId == client.Id && x.RevokedAt == null))
                .Should().Be(5);

            var authentication = new SqlOSClientAuthenticationService(
                setup,
                setupCrypto,
                new SqlOSAdminService(setup, options, setupCrypto));
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{retainedSecret}"))).ToString();
            (await authentication.AuthenticateTokenEndpointClientAsync(
                new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                {
                    ["client_id"] = clientId
                }),
                httpContext)).ClientId.Should().Be(clientId);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    [TestMethod]
    public async Task CodeOwnedMachineClientRotation_RealSqlRejectsBeforePrimaryCredentialMutation()
    {
        await using var context = await AspireFixture.CreateIsolatedAuthContextAsync("CodeCredential");
        try
        {
            var options = Options.Create(new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            });
            var fgaOptions = Options.Create(new SqlOSFgaOptions());
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            await new SqlOSFgaSchemaInitializer(
                    context,
                    fgaOptions,
                    loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>())
                .EnsureSchemaAsync();
            await new SqlOSFgaSeedService(
                    context,
                    fgaOptions,
                    loggerFactory.CreateLogger<SqlOSFgaSeedService>())
                .SeedCoreAsync();
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            const string clientId = "code-owned-machine-client";
            const string secret = "code-owned-secret-with-at-least-256-bits-of-entropy-123456789";
            context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
            {
                Id = "cli_code_owned",
                ClientId = clientId,
                Name = "Code-owned machine client",
                Audience = "sqlos",
                ClientType = "confidential",
                TokenEndpointAuthMethod = "client_secret_basic",
                GrantTypesJson = "[\"client_credentials\"]",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                ConfigurationOwner = SqlOSConfigurationOwners.Code,
                ConfigurationSourceKey = clientId
            });
            context.Set<SqlOSFgaSubject>().Add(new SqlOSFgaSubject
            {
                Id = "service_account::code-owned-machine-client",
                SubjectTypeId = "service_account",
                DisplayName = "Code-owned machine client"
            });
            var secretHash = crypto.HashPassword(secret);
            context.Set<SqlOSFgaServiceAccount>().Add(new SqlOSFgaServiceAccount
            {
                Id = "sa_code_owned",
                SubjectId = "service_account::code-owned-machine-client",
                ClientId = clientId,
                ClientSecretHash = secretHash,
                ConfigurationOwner = SqlOSConfigurationOwners.Code,
                ConfigurationSourceKey = clientId
            });
            context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
            {
                Id = "clcred_code_owned",
                ClientApplicationId = "cli_code_owned",
                SecretHash = secretHash,
                DisplayName = "Code-owned primary credential",
                CreatedAt = DateTime.UtcNow,
                ConfigurationOwner = SqlOSConfigurationOwners.Code,
                ConfigurationSourceKey = "primary"
            });
            await context.SaveChangesAsync();

            var admin = new SqlOSAdminService(context, options, crypto);
            var service = new SqlOSClientCredentialsService(context, crypto, admin, options);
            var machines = new SqlOSMachineClientAdminService(context, admin, crypto, options);
            await FluentActions.Invoking(() => service.RotateSecretAsync(
                    clientId,
                    "replacement-code-owned-secret-with-at-least-256-bits-123456789",
                    "admin-1"))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*owned by the 'code' configuration source*");
            await FluentActions.Invoking(() => service.RevokeAsync(clientId, "admin-1"))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*owned by the 'code' configuration source*");
            await FluentActions.Invoking(() => machines.RevokeAsync(clientId))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*code-owned*");

            context.ChangeTracker.Clear();
            var retained = await context.Set<SqlOSClientCredential>().SingleAsync();
            var retainedAccount = await context.Set<SqlOSFgaServiceAccount>().SingleAsync();
            retained.RevokedAt.Should().BeNull();
            retainedAccount.ExpiresAt.Should().BeNull();
            crypto.VerifyPassword(retained.SecretHash, secret).Should().BeTrue();
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

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

        var emergency = await machines.EmergencyDisableAsync(clientId);
        emergency.EmergencyDisabled.Should().BeTrue();
        await FluentActions.Invoking(() => protocol.ExchangeAsync(clientId, rotated.ClientSecret, audience, "jobs.run", new DefaultHttpContext(), default))
            .Should().ThrowAsync<SqlOSClientCredentialsException>();
        (await crypto.ValidateAccessTokenAsync(issued.AccessToken, audience)).Should().BeNull();
        await machines.EmergencyEnableAsync(clientId);
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
        context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
        {
            Id = $"clcred_{suffix}",
            ClientApplicationId = $"cli_{suffix}",
            SecretHash = crypto.HashPassword(secret),
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
