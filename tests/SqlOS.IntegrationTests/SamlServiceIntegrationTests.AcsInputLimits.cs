using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

public sealed partial class SamlServiceIntegrationTests
{
    [TestMethod]
    public async Task SamlAcs_OversizedOrMalformedPosts_ReturnGenericBadRequestAndLeaveRequestUsable()
    {
        await using var server = await SamlAcsServer.CreateAsync();
        using var client = server.App.GetTestClient();
        var flow = await server.StartFlowAsync();

        var tooLargeForm = "SAMLResponse=" + new string('A', 2 * SqlOSSamlService.MaxEncodedSamlResponseLength + 1)
            + "&RelayState=" + Uri.EscapeDataString(flow.RelayState);
        var rejectedPosts = new[]
        {
            tooLargeForm,
            FormBody(new string('A', SqlOSSamlService.MaxEncodedSamlResponseLength + 1), flow.RelayState),
            FormBody("not base64 at all!", flow.RelayState),
            FormBody(Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text, not xml")), flow.RelayState),
            FormBody(Convert.ToBase64String(Encoding.UTF8.GetBytes("<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\"><saml")), flow.RelayState),
            FormBody(Convert.ToBase64String(Encoding.UTF8.GetBytes(OverCharacterLimitXml())), flow.RelayState)
        };

        foreach (var body in rejectedPosts)
        {
            var response = await client.PostAsync(
                $"/sqlos/auth/saml/acs/{server.ConnectionId}",
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("error").GetString().Should().Be("access_denied");
            json.RootElement.GetProperty("message").GetString()
                .Should().Be(SqlOSPublicAuthErrorMapper.DefaultExternalSignInMessage);
        }

        (await server.IsRequestCompletedAsync(flow.RelayState)).Should().BeFalse();
        (await server.EmailExistsAsync()).Should().BeFalse();
        (await server.CountPublicErrorAuditsAsync()).Should().Be(rejectedPosts.Length);

        var signed = BuildSignedSamlResponse(
            server.Certificate,
            server.IdentityProviderEntityId,
            server.Email,
            "Acs",
            "Limits",
            flow);
        var accepted = await client.PostAsync(
            $"/sqlos/auth/saml/acs/{server.ConnectionId}",
            new StringContent(FormBody(signed, flow.RelayState), Encoding.UTF8, "application/x-www-form-urlencoded"));

        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        accepted.Headers.Location!.ToString().Should().StartWith("https://client.example.local/callback?code=");
        (await server.IsRequestCompletedAsync(flow.RelayState)).Should().BeTrue();
    }

    [TestMethod]
    public async Task SamlAcs_OverLimitEncodedResponse_IsRejectedBeforeLookupOrDecoding()
    {
        var (_, _, saml) = CreateSamlServices();

        // Not base64 and an unknown connection: only a pre-lookup, pre-decode length check yields this error.
        var action = async () => await saml.HandleAcsAsync(
            "missing-connection",
            new string('!', SqlOSSamlService.MaxEncodedSamlResponseLength + 1),
            "missing-relay-state");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSSamlService.SamlResponseTooLargeMessage);
    }

    [TestMethod]
    public async Task SamlAcs_OverCharacterLimitDocument_IsRejectedAsMalformed()
    {
        await using var server = await SamlAcsServer.CreateAsync();
        var flow = await server.StartFlowAsync();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(OverCharacterLimitXml()));
        encoded.Length.Should().BeLessThanOrEqualTo(SqlOSSamlService.MaxEncodedSamlResponseLength);

        await using var scope = server.App.Services.CreateAsyncScope();
        var saml = scope.ServiceProvider.GetRequiredService<SqlOSSamlService>();
        var action = async () => await saml.HandleAcsAsync(server.ConnectionId, encoded, flow.RelayState);

        // A well-formed document under the cap would fail later on its missing signature.
        var rejection = await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSSamlService.SamlResponseMalformedMessage);
        rejection.Which.InnerException.Should().BeOfType<System.Xml.XmlException>();
        (await server.IsRequestCompletedAsync(flow.RelayState)).Should().BeFalse();
    }

    private static string FormBody(string samlResponse, string relayState)
        => $"SAMLResponse={Uri.EscapeDataString(samlResponse)}&RelayState={Uri.EscapeDataString(relayState)}";

    private static string OverCharacterLimitXml()
        => "<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" ID=\"_padded\" Version=\"2.0\"><!--"
            + new string('x', (int)SqlOSSamlService.MaxSamlDocumentCharacters)
            + "--></samlp:Response>";

    private sealed class SamlAcsServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required X509Certificate2 Certificate { get; init; }
        public required string ConnectionId { get; init; }
        public required string IdentityProviderEntityId { get; init; }
        public required string ClientId { get; init; }
        public required string Email { get; init; }

        public static async Task<SamlAcsServer> CreateAsync()
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("SamlAcsLimits");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(database => database.UseTestProvider(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = AspireFixture.Options.Issuer;
                options.AuthServer.BasePath = AspireFixture.Options.BasePath;
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("ACS limits", null));
            var domain = $"acs-{Guid.NewGuid():N}.example";
            context.Set<SqlOSOrganizationDomain>().Add(VerifiedDomainClaim(organization.Id, domain));
            await context.SaveChangesAsync();
            var client = await CreateSamlClientAsync(admin, "acs-limits");
            var certificate = CreateCertificate("CN=SqlOSAcsLimits");
            var identityProviderEntityId = $"urn:acs-limits:{Guid.NewGuid():N}:idp";
            var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
                organization.Id, "ACS limits SSO", identityProviderEntityId, "https://idp.example.test/sso",
                certificate.ExportCertificatePem(), true, false, "email", "first_name", "last_name"));
            await app.StartAsync();

            return new SamlAcsServer
            {
                App = app,
                Certificate = certificate,
                ConnectionId = connection.Id,
                IdentityProviderEntityId = identityProviderEntityId,
                ClientId = client.ClientId,
                Email = $"acs-user@{domain}"
            };
        }

        public async Task<SamlFlow> StartFlowAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await StartSamlRequestAsync(
                scope.ServiceProvider.GetRequiredService<SqlOSSamlService>(),
                ConnectionId,
                ClientId,
                scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>());
        }

        public async Task<bool> IsRequestCompletedAsync(string authorizationRequestId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var request = await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSAuthorizationRequest>()
                .SingleAsync(x => x.Id == authorizationRequestId);
            return request.CompletedAt != null;
        }

        public async Task<bool> EmailExistsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var normalized = Email.ToUpperInvariant();
            return await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSUserEmail>()
                .AnyAsync(x => x.NormalizedEmail == normalized);
        }

        public async Task<int> CountPublicErrorAuditsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSAuditEvent>()
                .CountAsync(x => x.EventType == SqlOSPublicAuthErrorAudit.EventType);
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }
            await App.StopAsync();
            await App.DisposeAsync();
            Certificate.Dispose();
        }
    }
}
