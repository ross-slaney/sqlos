using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SignupTransactionIntegrationTests
{
    private const string Password = "P@ssword123!";
    private const string ValidPkceCodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string TrustedOrigin = "https://auth.example.test";

    [TestMethod]
    public async Task PublicJsonSignup_UnknownClient_LeavesNoArtifacts()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var email = $"unknown-client-{Guid.NewGuid():N}@example.com";

        var act = async () => await database.Auth.SignUpAsync(
            new SqlOSSignupRequest(
                "Unknown Client",
                email,
                Password,
                $"Org {Guid.NewGuid():N}",
                "missing-client",
                null),
            CreateHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unknown client 'missing-client'.");
        await AssertNoSignupArtifactsAsync(database.Context, email);
    }

    [TestMethod]
    public async Task PublicJsonSignup_ApplicationAccessDenied_RollsBackUserAndAudits()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var client = await database.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == database.ClientId);
        client.AccessMode = SqlOSApplicationAccessModes.SelectedOrganizations;
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var email = $"access-denied-{Guid.NewGuid():N}@example.com";
        var act = async () => await database.Auth.SignUpAsync(
            new SqlOSSignupRequest(
                "Denied Access",
                email,
                Password,
                $"Denied Org {Guid.NewGuid():N}",
                database.ClientId,
                null),
            CreateHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Application access is not allowed.");
        await AssertNoSignupArtifactsAsync(database.Context, email);
    }

    [TestMethod]
    public async Task PublicJsonSignup_ConcurrentDuplicateEmail_ResolvesDeterministically()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var email = $"race-{Guid.NewGuid():N}@example.com";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var actor = database.CreateActor();
            await start.Task;
            try
            {
                var result = await actor.Auth.SignUpAsync(
                    new SqlOSSignupRequest(
                        "Race User",
                        email,
                        Password,
                        $"Race Org {Guid.NewGuid():N}",
                        database.ClientId,
                        null),
                    CreateHttpContext());
                return result.Tokens != null;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
            {
                return false;
            }
        }).ToArray();

        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(static succeeded => succeeded).Should().Be(1);
        outcomes.Count(static succeeded => !succeeded).Should().Be(3);
        database.Context.ChangeTracker.Clear();
        var normalized = SqlOSAdminService.NormalizeEmail(email);
        (await database.Context.Set<SqlOSUserEmail>().CountAsync(x => x.NormalizedEmail == normalized))
            .Should().Be(1);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "user.signup"))
            .Should().Be(1);
        (await database.Context.Set<SqlOSSession>().CountAsync())
            .Should().Be(1);
    }

    [TestMethod]
    public async Task PublicJsonSignup_SuccessfulSignup_CommitsOnce()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var email = $"ok-{Guid.NewGuid():N}@example.com";

        var result = await database.Auth.SignUpAsync(
            new SqlOSSignupRequest(
                "Success User",
                email,
                Password,
                $"Success Org {Guid.NewGuid():N}",
                database.ClientId,
                null),
            CreateHttpContext());

        result.Tokens.Should().NotBeNull();
        database.Context.ChangeTracker.Clear();
        var normalized = SqlOSAdminService.NormalizeEmail(email);
        (await database.Context.Set<SqlOSUserEmail>().CountAsync(x => x.NormalizedEmail == normalized))
            .Should().Be(1);
        (await database.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "user.signup"))
            .Should().Be(1);
        (await database.Context.Set<SqlOSSession>().CountAsync())
            .Should().Be(1);
    }

    [TestMethod]
    public async Task HeadlessSignup_InactiveClient_LeavesNoArtifacts()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var authorizationRequest = await CreateAuthorizationRequestAsync(database);
        await DeactivateClientAsync(database);
        var email = $"inactive-client-{Guid.NewGuid():N}@example.com";

        var result = await database.Headless.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Inactive Client",
                email,
                Password,
                $"Org {Guid.NewGuid():N}",
                null));

        result.Type.Should().Be("view");
        result.ViewModel.Should().NotBeNull();
        await AssertNoSignupArtifactsAsync(database.Context, email);
    }

    [TestMethod]
    public async Task HeadlessSignup_RedirectRemovedAfterAuthorize_LeavesNoArtifacts()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var authorizationRequest = await CreateAuthorizationRequestAsync(database);
        var client = await database.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == database.ClientId);
        client.RedirectUrisJson = """["https://client.example.test/other"]""";
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var email = $"redirect-{Guid.NewGuid():N}@example.com";

        var result = await database.Headless.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Redirect Removed",
                email,
                Password,
                $"Org {Guid.NewGuid():N}",
                null));

        result.Type.Should().Be("view");
        result.ViewModel.Should().NotBeNull();
        await AssertNoSignupArtifactsAsync(database.Context, email);
    }

    [TestMethod]
    public async Task HeadlessSignup_UnauthorizedResource_LeavesNoArtifacts()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var authorizationRequest = await database.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                database.ClientId,
                database.RedirectUri,
                "state-resource",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                "https://evil.example.test/resource",
                null,
                null,
                null,
                "headless",
                null));
        database.Options.ResourceIndicators.Enabled = false;
        var email = $"resource-{Guid.NewGuid():N}@example.com";

        var result = await database.Headless.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Bad Resource",
                email,
                Password,
                $"Org {Guid.NewGuid():N}",
                null));

        result.Type.Should().Be("view");
        result.ViewModel!.Error.Should().Be(SqlOSSignupOrchestration.UnauthorizedResourceMessage);
        await AssertNoSignupArtifactsAsync(database.Context, email);
    }

    [TestMethod]
    public async Task HeadlessSignup_InvitationEmailMismatch_LeavesNoArtifacts()
    {
        await using var database = await SignupDatabase.CreateAsync();
        var organization = await database.Admin.CreateOrganizationAsync(
            new SqlOSCreateOrganizationRequest($"Invite Org {Guid.NewGuid():N}", null));
        var invitation = await database.Invitation.CreateEmailInvitationAsync(
            new SqlOSCreateEmailInvitationRequest(organization.Id, "alice-invite@example.com", "member"),
            CreateHttpContext());
        var authorizationRequest = await CreateAuthorizationRequestAsync(database);
        var email = $"bob-mismatch-{Guid.NewGuid():N}@example.com";

        var act = async () => await database.Headless.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Bob Mismatch",
                email,
                Password,
                OrganizationName: null,
                CustomFields: null,
                InvitationToken: GetInvitationToken(invitation)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSSignupOrchestration.InvitationEmailMismatchMessage);
        await AssertNoSignupArtifactsAsync(database.Context, email);
        (await database.Context.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == organization.Id))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task HeadlessSignup_HookFailure_LeavesNoArtifacts()
    {
        await using var database = await SignupDatabase.CreateAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (_, _) =>
                throw new SqlOSHeadlessValidationException(
                    "Delivery failed.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["companyName"] = "Company name is already in use."
                    },
                    ["Please review the highlighted fields."]);
        });
        var authorizationRequest = await CreateAuthorizationRequestAsync(database);
        var email = $"hook-{Guid.NewGuid():N}@example.com";

        var result = await database.Headless.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Hook User",
                email,
                Password,
                $"Hook Org {Guid.NewGuid():N}",
                new JsonObject { ["companyName"] = "Acme" }));

        result.Type.Should().Be("view");
        result.ViewModel!.Error.Should().Be("Please review the highlighted fields.");
        await AssertNoSignupArtifactsAsync(database.Context, email);
    }

    [TestMethod]
    public async Task PublicJsonHttpSignup_UnknownClient_LeavesNoArtifacts()
    {
        await using var server = await SignupHttpServer.CreateAsync();
        using var client = server.App.GetTestClient();
        client.BaseAddress = new Uri(TrustedOrigin);
        var email = $"http-unknown-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/sqlos/auth/signup", new
        {
            displayName = "HTTP Unknown",
            email,
            password = Password,
            organizationName = $"HTTP Org {Guid.NewGuid():N}",
            clientId = "missing-client"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var context = server.CreateContext();
        await AssertNoSignupArtifactsAsync(context, email);
    }

    [TestMethod]
    public async Task HostedSignupSubmit_InactiveClient_LeavesNoArtifacts()
    {
        await using var server = await SignupHttpServer.CreateAsync();
        using var client = server.App.GetTestClient();
        client.BaseAddress = new Uri(TrustedOrigin);
        var email = $"hosted-{Guid.NewGuid():N}@example.com";

        var authorize = await client.GetAsync(
            "/sqlos/auth/authorize?response_type=code&client_id=browser-client"
            + "&redirect_uri=https%3A%2F%2Fclient.example.test%2Fcallback"
            + "&scope=openid%20profile%20email&state=hosted-state"
            + "&code_challenge=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&code_challenge_method=S256");
        authorize.EnsureSuccessStatusCode();
        var authorizeHtml = await authorize.Content.ReadAsStringAsync();
        var requestId = ExtractInputValue(authorizeHtml, "requestId");

        await server.DeactivateBrowserClientAsync();

        var signupPage = await client.GetAsync($"/sqlos/auth/signup?request={Uri.EscapeDataString(requestId)}");
        signupPage.EnsureSuccessStatusCode();
        var signupHtml = await signupPage.Content.ReadAsStringAsync();
        var requestToken = ExtractInputValue(signupHtml, "__RequestVerificationToken");
        var antiforgeryCookie = ExtractCookie(signupPage, "sqlos_auth_page_csrf_");

        using var submit = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/signup/submit")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = requestId,
                ["displayName"] = "Hosted User",
                ["email"] = email,
                ["password"] = Password,
                ["organizationName"] = $"Hosted Org {Guid.NewGuid():N}",
                ["__RequestVerificationToken"] = requestToken
            })
        };
        submit.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        submit.Headers.TryAddWithoutValidation("Origin", TrustedOrigin);
        var rejected = await client.SendAsync(submit);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var context = server.CreateContext();
        await AssertNoSignupArtifactsAsync(context, email);
    }

    private static async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(SignupDatabase database)
        => await database.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                database.ClientId,
                database.RedirectUri,
                $"state-{Guid.NewGuid():N}",
                "openid profile email",
                ValidPkceCodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                null));

    private static async Task DeactivateClientAsync(SignupDatabase database)
    {
        var client = await database.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == database.ClientId);
        client.IsActive = false;
        client.DisabledAt = DateTime.UtcNow;
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
    }

    private static async Task AssertNoSignupArtifactsAsync(TestSqlOSDbContext context, string email)
    {
        context.ChangeTracker.Clear();
        var normalized = SqlOSAdminService.NormalizeEmail(email);
        (await context.Set<SqlOSUserEmail>().CountAsync(x => x.NormalizedEmail == normalized)).Should().Be(0);
        (await context.Set<SqlOSUser>().CountAsync(x => x.DefaultEmail == email)).Should().Be(0);
        (await context.Set<SqlOSCredential>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSMembership>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x =>
                x.EventType == "user.signup" || x.EventType.StartsWith("user.login.")))
            .Should().Be(0);
    }

    private static string GetInvitationToken(SqlOSEmailInvitationResult result)
    {
        result.InviteUrl.Should().NotBeNullOrWhiteSpace();
        var marker = "token=";
        var index = result.InviteUrl!.IndexOf(marker, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0);
        var token = result.InviteUrl[(index + marker.Length)..];
        var ampersand = token.IndexOf('&');
        if (ampersand >= 0)
        {
            token = token[..ampersand];
        }

        return Uri.UnescapeDataString(token);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tests");
        context.Request.Headers.UserAgent = "SqlOSTest";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        return context;
    }

    private static string ExtractInputValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(name)}"" value=""([^""]+)""",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"the hosted page should contain {name}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string ExtractCookie(HttpResponseMessage response, string prefix)
    {
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        return values!.Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class SignupDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;

        private SignupDatabase(
            TestSqlOSDbContext context,
            string connectionString,
            SqlOSAuthServerOptions options,
            string clientId,
            string redirectUri,
            SqlOSAdminService admin,
            SqlOSAuthService auth,
            SqlOSAuthorizationServerService authorization,
            SqlOSHeadlessAuthService headless,
            SqlOSInvitationService invitation)
        {
            Context = context;
            _connectionString = connectionString;
            Options = options;
            ClientId = clientId;
            RedirectUri = redirectUri;
            Admin = admin;
            Auth = auth;
            Authorization = authorization;
            Headless = headless;
            Invitation = invitation;
        }

        public TestSqlOSDbContext Context { get; }
        public SqlOSAuthServerOptions Options { get; }
        public string ClientId { get; }
        public string RedirectUri { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSAuthService Auth { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSHeadlessAuthService Headless { get; }
        public SqlOSInvitationService Invitation { get; }

        public static async Task<SignupDatabase> CreateAsync(Action<SqlOSHeadlessAuthOptions>? configureHeadless = null)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync("SignupTx");
            var connectionString = context.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The signup database has no connection string.");
            var clientId = "test-client";
            var redirectUri = "https://client.example.test/callback";
            var optionsValue = CreateOptions(clientId, redirectUri, configureHeadless);
            var actor = BuildActor(context, optionsValue, ownsContext: false);
            await actor.Crypto.EnsureActiveSigningKeyAsync();
            await actor.Admin.UpsertSeededClientsAsync();
            await actor.Settings.UpsertSeededAuthPageSettingsAsync();
            await actor.Settings.UpsertSeededAuthEmailSettingsAsync();
            await new SqlOSEmailAdminService(context, actor.Crypto, new SqlOSEmailTemplateRenderer())
                .EnsureBuiltInTemplatesAsync();
            return new SignupDatabase(
                context,
                connectionString,
                optionsValue,
                clientId,
                redirectUri,
                actor.Admin,
                actor.Auth,
                actor.Authorization,
                actor.Headless,
                actor.Invitation);
        }

        public SignupActor CreateActor()
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseSqlServer(_connectionString)
                    .Options);
            return BuildActor(context, Options, ownsContext: true);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.Database.EnsureDeletedAsync();
            await Context.DisposeAsync();
        }

        private static SqlOSAuthServerOptions CreateOptions(
            string clientId,
            string redirectUri,
            Action<SqlOSHeadlessAuthOptions>? configureHeadless)
        {
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth",
                PublicOrigin = TrustedOrigin
            };
            options.SeedBrowserClient(clientId, "Test Client", redirectUri);
            options.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["password", "email_otp"];
                page.EnablePasswordSignup = true;
            });
            options.UseHeadlessAuthPage(headless =>
            {
                headless.BuildUiUrl = ctx =>
                    $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}";
            });
            configureHeadless?.Invoke(options.Headless);
            return options;
        }

        internal static SignupActor BuildActor(
            TestSqlOSDbContext context,
            SqlOSAuthServerOptions optionsValue,
            bool ownsContext)
        {
            var options = Microsoft.Extensions.Options.Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender { IsConfigured = true };
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var transactionalEmail = new SqlOSTransactionalEmailService(
                context,
                crypto,
                emailSender,
                new SqlOSEmailTemplateRenderer(),
                Microsoft.Extensions.Options.Options.Create(new SqlOSEmailOptions()));
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options, transactionalEmail);
            var invitation = new SqlOSInvitationService(context, admin, crypto, emailSender, settings, options, transactionalEmail);
            var passwordAbuse = new SqlOSPasswordLoginAbuseService(context, admin, crypto, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                invitation,
                passwordAbuse,
                transactionalEmail);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                authPageSession,
                options,
                invitation,
                passwordAbuse);
            var discovery = new SqlOSHomeRealmDiscoveryService(context);
            var oidcAuth = new SqlOSOidcAuthService(
                context,
                admin,
                crypto,
                new FakeOidcProviderHttpClientFactory(),
                NullLogger<SqlOSOidcAuthService>.Instance);
            var saml = new SqlOSSamlService(context, options, admin, crypto);
            var oidcBrowser = new SqlOSOidcBrowserAuthService(
                context,
                admin,
                auth,
                authorization,
                crypto,
                oidcAuth,
                options);
            var headless = new SqlOSHeadlessAuthService(
                context,
                admin,
                authorization,
                discovery,
                oidcBrowser,
                saml,
                settings,
                emailOtp,
                options,
                invitationService: invitation,
                authService: auth);
            return new SignupActor(context, admin, crypto, settings, auth, authorization, headless, invitation, ownsContext);
        }
    }

    internal sealed class SignupActor(
        TestSqlOSDbContext context,
        SqlOSAdminService admin,
        SqlOSCryptoService crypto,
        SqlOSSettingsService settings,
        SqlOSAuthService auth,
        SqlOSAuthorizationServerService authorization,
        SqlOSHeadlessAuthService headless,
        SqlOSInvitationService invitation,
        bool ownsContext) : IAsyncDisposable
    {
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSSettingsService Settings { get; } = settings;
        public SqlOSAuthService Auth { get; } = auth;
        public SqlOSAuthorizationServerService Authorization { get; } = authorization;
        public SqlOSHeadlessAuthService Headless { get; } = headless;
        public SqlOSInvitationService Invitation { get; } = invitation;

        public async ValueTask DisposeAsync()
        {
            if (ownsContext)
            {
                await context.DisposeAsync();
            }
        }
    }

    private sealed class SignupHttpServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string ConnectionString { get; init; }

        public TestSqlOSDbContext CreateContext()
            => new(new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);

        public static async Task<SignupHttpServer> CreateAsync()
        {
            await using var bootstrap = await AspireFixture.CreateIsolatedAuthContextAsync("SignupHttp");
            var connectionString = bootstrap.Database.GetConnectionString()!;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(database => database.UseSqlServer(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = $"{TrustedOrigin}/sqlos/auth";
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.PublicOrigin = TrustedOrigin;
                options.AuthServer.SeedBrowserClient(
                    "browser-client",
                    "Browser Client",
                    "https://client.example.test/callback");
                options.AuthServer.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["password"];
                    page.EnablePasswordSignup = true;
                });
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            }

            await app.StartAsync();
            return new SignupHttpServer
            {
                App = app,
                ConnectionString = connectionString
            };
        }

        public async Task DeactivateBrowserClientAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var client = await context.Set<SqlOSClientApplication>()
                .SingleAsync(x => x.ClientId == "browser-client");
            client.IsActive = false;
            client.DisabledAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }

            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
