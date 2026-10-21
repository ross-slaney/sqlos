using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Self-service and administrative account creation accept only a plain mailbox, so markup
/// cannot be stored as a user's email through them (#425). JSON and admin routes answer 400; the
/// headless API answers with its signup view carrying the same error, as it does for every
/// validation failure. Values that arrive by other paths
/// (SCIM, IdP assertions) are returned unchanged by the admin API; the dashboard escapes them.
/// </summary>
[TestClass]
public sealed class EmailAddressSyntaxIntegrationTests
{
    private const string HostileEmail = "x<meta http-equiv=refresh content='0;url=https://evil.example'>@a.b";
    private const string InvalidEmailMessage = "Enter a valid email address.";

    [TestMethod]
    public async Task EveryCreationPath_RejectsMarkupInTheEmail_AndStoresNothing()
    {
        await using var host = await EmailSyntaxHost.CreateAsync();

        using (var publicSignup = await host.Fixture.Client.PostAsJsonAsync("/sqlos/auth/signup", new
        {
            displayName = "Jane",
            email = HostileEmail,
            password = HostedAuthorizeTokenFixture.Password,
            organizationName = "Jane Org",
            clientId = HostedAuthorizeTokenFixture.ClientId
        }))
        {
            await AssertInvalidEmailAsync(publicSignup, "public JSON signup");
        }

        var requestId = await host.StartHeadlessAuthorizationAsync();
        using (var headlessSignup = await host.Fixture.Client.PostAsJsonAsync("/sqlos/auth/headless/signup", new
        {
            requestId,
            displayName = "Jane",
            email = HostileEmail,
            password = HostedAuthorizeTokenFixture.Password,
            organizationName = "Jane Org"
        }))
        {
            await AssertHeadlessInvalidEmailAsync(headlessSignup, "headless password signup");
        }

        using (var emailOtpSignup = await host.Fixture.Client.PostAsJsonAsync("/sqlos/auth/headless/signup/email-otp/start", new
        {
            requestId,
            displayName = "Jane",
            email = HostileEmail,
            organizationName = "Jane Org"
        }))
        {
            await AssertHeadlessInvalidEmailAsync(emailOtpSignup, "headless email-code signup");
        }

        using (var adminCreate = await host.AdminPostAsync("/sqlos/admin/auth/api/users", new
        {
            displayName = "Jane",
            email = HostileEmail,
            password = HostedAuthorizeTokenFixture.Password
        }))
        {
            adminCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest, "admin user creation rejects the address");
            (await adminCreate.Content.ReadAsStringAsync()).Should().Contain(InvalidEmailMessage);
        }

        var organizationId = await host.CreateOrganizationAsync();
        using (var invitation = await host.AdminPostAsync($"/sqlos/admin/auth/api/organizations/{organizationId}/invitations", new
        {
            email = HostileEmail,
            role = "member",
            sendEmail = true
        }))
        {
            invitation.StatusCode.Should().Be(HttpStatusCode.BadRequest, "invitation creation rejects the address");
            (await invitation.Content.ReadAsStringAsync()).Should().Contain(InvalidEmailMessage);
        }

        await using var db = host.CreateDbContext();
        (await db.Set<SqlOSUserEmail>().AnyAsync(x => x.Email.Contains("<meta"))).Should().BeFalse();
        (await db.Set<SqlOSUser>().AnyAsync(x => x.DefaultEmail != null && x.DefaultEmail.Contains("<meta"))).Should().BeFalse();
        (await db.Set<SqlOSEmailOtpChallenge>().AnyAsync(x => x.Email.Contains("<meta"))).Should().BeFalse();
        (await db.Set<SqlOSInvitation>().AnyAsync()).Should().BeFalse();
        host.Emails.Messages.Should().BeEmpty("nothing is sent to a rejected address");
    }

    [DataTestMethod]
    [DataRow("first.last+tag@sub.example.co.uk")]
    [DataRow("o'brien@example.com")]
    [DataRow("Mixed.Case@Example.COM")]
    public async Task OrdinaryAddresses_SignUpAndAppearInTheUsersList(string email)
    {
        await using var host = await EmailSyntaxHost.CreateAsync();

        using var signup = await host.Fixture.Client.PostAsJsonAsync("/sqlos/auth/signup", new
        {
            displayName = "Ordinary",
            email,
            password = HostedAuthorizeTokenFixture.Password,
            organizationName = "Ordinary Org",
            clientId = HostedAuthorizeTokenFixture.ClientId
        });
        signup.StatusCode.Should().Be(HttpStatusCode.OK, await signup.Content.ReadAsStringAsync());

        var users = await host.AdminGetJsonAsync("/sqlos/admin/auth/api/users?pageSize=100");
        DefaultEmails(users).Should().Contain(email);
    }

    [TestMethod]
    public async Task AdminApi_ReturnsStoredMarkupUnchanged_ForTheDashboardToEscape()
    {
        await using var host = await EmailSyntaxHost.CreateAsync();
        const string organizationName = "<b>x</b>";

        // SCIM and IdP assertions do not go through the submitted-address check; seed the row
        // directly to stand in for them.
        await using (var db = host.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var user = new SqlOSUser
            {
                Id = $"usr_{Guid.NewGuid():N}",
                DisplayName = "Imported",
                DefaultEmail = HostileEmail,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Set<SqlOSUser>().Add(user);
            db.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
            {
                Id = $"eml_{Guid.NewGuid():N}",
                UserId = user.Id,
                Email = HostileEmail,
                NormalizedEmail = SqlOSAdminService.NormalizeEmail(HostileEmail),
                IsPrimary = true,
                CreatedAt = now
            });
            await db.SaveChangesAsync();

            var organizationId = await host.CreateOrganizationAsync(organizationName);
            await using var scope = host.Fixture.App.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateMembershipAsync(organizationId, new SqlOSCreateMembershipRequest(user.Id, "member"));
        }

        var users = await host.AdminGetJsonAsync("/sqlos/admin/auth/api/users?pageSize=100");
        DefaultEmails(users).Should().Contain(HostileEmail);

        var memberships = await host.AdminGetJsonAsync("/sqlos/admin/auth/api/memberships?pageSize=100");
        var membership = Items(memberships).Single(item => item.GetProperty("userEmail").GetString() == HostileEmail);
        membership.GetProperty("organization").GetString().Should().Be(organizationName);
    }

    private static async Task AssertInvalidEmailAsync(HttpResponseMessage response, string path)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} rejects the address; body: {1}", path, body);
        body.Should().Contain(InvalidEmailMessage, "{0} uses the generic public message", path);
        body.Should().NotContain("<meta", "{0} does not echo the submitted markup", path);
    }

    /// <summary>The headless API reports validation errors on the signup view it returns.</summary>
    private static async Task AssertHeadlessInvalidEmailAsync(HttpResponseMessage response, string path)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} answers with a view; body: {1}", path, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("type").GetString().Should().Be("view");
        var view = json.RootElement.GetProperty("viewModel");
        view.GetProperty("view").GetString().Should().Be("signup", "{0} stays on the signup view", path);
        view.GetProperty("error").GetString().Should().Be(InvalidEmailMessage, "{0} uses the generic public message", path);
        view.GetProperty("signupToken").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("challengeToken").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static IEnumerable<string?> DefaultEmails(JsonElement page)
        => Items(page).Select(item => item.GetProperty("defaultEmail").GetString());

    private static IEnumerable<JsonElement> Items(JsonElement page)
        => (page.ValueKind == JsonValueKind.Array ? page : page.GetProperty("data")).EnumerateArray();

    private sealed class EmailSyntaxHost : IAsyncDisposable
    {
        private EmailSyntaxHost(HostedAuthorizeTokenFixture fixture, TestAuthEmailSender emails)
        {
            Fixture = fixture;
            Emails = emails;
        }

        public HostedAuthorizeTokenFixture Fixture { get; }
        public TestAuthEmailSender Emails { get; }

        public static async Task<EmailSyntaxHost> CreateAsync()
        {
            var emails = new TestAuthEmailSender { IsConfigured = true };
            var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
                "EmailSyntax",
                configure: options =>
                {
                    options.AuthServer.SeedAuthPage(page =>
                    {
                        page.EnabledCredentialTypes = ["password", "email_otp"];
                        page.EnablePasswordSignup = true;
                    });
                    options.AuthServer.UseHeadlessAuthPage(page => page.BuildUiUrl = context =>
                        $"https://app.example.test/auth?request={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={context.View}");
                },
                configureServices: services =>
                {
                    services.RemoveAll<ISqlOSAuthEmailSender>();
                    services.AddSingleton<ISqlOSAuthEmailSender>(emails);
                    services.RemoveAll<ISqlOSEmailSender>();
                    services.AddSingleton<ISqlOSEmailSender>(emails);
                });
            return new EmailSyntaxHost(fixture, emails);
        }

        public async Task<string> StartHeadlessAuthorizationAsync()
        {
            using var authorize = await Fixture.AuthorizeRawAsync(HttpMethod.Get, "openid profile email");
            authorize.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var requestId = QueryHelpers.ParseQuery(authorize.Response.Headers.Location!.Query)["request"].ToString();
            requestId.Should().NotBeNullOrWhiteSpace();
            return requestId;
        }

        public async Task<string> CreateOrganizationAsync(string name = "Syntax Org")
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var organization = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(name, null));
            return organization.Id;
        }

        /// <summary>Calls the admin API the way the dashboard does: same origin, with the request header.</summary>
        public Task<HttpResponseMessage> AdminPostAsync(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-SqlOS-Request", "1");
            request.Headers.Add("Origin", HostedAuthorizeTokenFixture.TrustedOrigin);
            return Fixture.Client.SendAsync(request);
        }

        public async Task<JsonElement> AdminGetJsonAsync(string path)
        {
            using var response = await Fixture.Client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        public TestSqlOSDbContext CreateDbContext()
            => Fixture.App.Services.CreateScope().ServiceProvider.GetRequiredService<TestSqlOSDbContext>();

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }
}
