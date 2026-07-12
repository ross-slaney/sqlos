using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class MfaEnrollmentSecurityIntegrationTests
{
    [TestMethod]
    public async Task SqlServer_EnrollmentProofForDifferentUser_DoesNotMutateOrIssueArtifacts()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaBindUser");
        var userA = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA User A",
            $"sql-mfa-a-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var userB = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA User B",
            $"sql-mfa-b-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var loginA = await fixture.LoginAsync(userA);
        var loginB = await fixture.LoginAsync(userB);
        var enrollment = await fixture.Auth.StartTotpEnrollmentForChallengeAsync(
            loginA.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("SQL authenticator"));
        var code = fixture.Totp.GenerateCodeForTesting(enrollment.Secret);

        var act = async () => await fixture.Auth.VerifyTotpEnrollmentAsync(
            new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, loginB.MfaToken));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("MFA enrollment is not authorized for this challenge.");
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Set<SqlOSUserAuthenticator>()
            .SingleAsync(x => x.Id == enrollment.AuthenticatorId)).IsConfirmed.Should().BeFalse();
        (await fixture.Context.Set<SqlOSRecoveryCode>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSSession>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await fixture.Context.Set<SqlOSDeviceAuthorization>().CountAsync(x => x.ApprovedAt != null)).Should().Be(0);
    }

    [TestMethod]
    public async Task SqlServer_ConcurrentEnrollmentVerification_ConsumesOneProofAndIssuesOneSession()
    {
        await using var fixture = await SqlMfaFixture.CreateAsync("MfaBindRace");
        var user = await fixture.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "SQL MFA Race",
            $"sql-mfa-race-{Guid.NewGuid():N}@example.com",
            "P@ssword123!"));
        var login = await fixture.LoginAsync(user);
        var enrollment = await fixture.Auth.StartTotpEnrollmentForChallengeAsync(
            login.MfaToken!,
            new SqlOSTotpEnrollmentStartRequest("Race authenticator"));
        var code = fixture.Totp.GenerateCodeForTesting(enrollment.Secret);
        var connectionString = fixture.Context.Database.GetConnectionString()!;

        await using var instanceA = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        await using var instanceB = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        var request = new SqlOSTotpEnrollmentVerifyRequest(enrollment.EnrollmentToken, code, login.MfaToken);
        var outcomes = await Task.WhenAll(CaptureAsync(instanceA.Auth, request), CaptureAsync(instanceB.Auth, request));

        outcomes.Count(x => x.Success).Should().Be(1);
        outcomes.Count(x => !x.Success).Should().Be(1);
        outcomes.Single(x => !x.Success).Error.Should().BeOfType<InvalidOperationException>();

        await using var verify = SqlMfaFixture.CreateForExistingDatabase(connectionString, fixture.Options);
        (await verify.Context.Set<SqlOSUserAuthenticator>()
            .CountAsync(x => x.UserId == user.Id && x.IsConfirmed && x.RevokedAt == null)).Should().Be(1);
        (await verify.Context.Set<SqlOSRecoveryCode>()
            .CountAsync(x => x.UserId == user.Id && x.ConsumedAt == null && x.RevokedAt == null))
            .Should().Be(fixture.Options.Mfa.Totp.RecoveryCodeCount);
        (await verify.Context.Set<SqlOSSession>().CountAsync(x => x.UserId == user.Id)).Should().Be(1);
        (await verify.Context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(1);
        (await verify.Context.Set<SqlOSAuthorizationCode>().CountAsync()).Should().Be(0);
        (await verify.Context.Set<SqlOSDeviceAuthorization>().CountAsync(x => x.ApprovedAt != null)).Should().Be(0);
        (await verify.Context.Set<SqlOSTemporaryToken>()
            .CountAsync(x =>
                (x.Purpose == SqlOSAuthService.MfaChallengePurpose || x.Purpose == SqlOSTotpMfaService.EnrollmentPurpose)
                && x.ConsumedAt != null)).Should().Be(2);
    }

    [TestMethod]
    public async Task RealEndpoints_PublicHeadlessAndHostedRejectChallengeSubstitution()
    {
        await using var server = await MfaEndpointServer.CreateAsync();
        using var client = server.App.GetTestClient();

        await VerifyPublicEndpointRejectsFirstFactorEnrollmentAsync(server, client);
        await VerifyHeadlessEndpointBindsAuthorizationRequestAsync(server, client);
        await VerifyHostedEndpointBindsAuthorizationRequestAsync(server, client);
    }

    private static async Task VerifyPublicEndpointRejectsFirstFactorEnrollmentAsync(
        MfaEndpointServer server,
        HttpClient client)
    {
        string email;
        string userId;
        string[] recoveryHashes;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var auth = scope.ServiceProvider.GetRequiredService<SqlOSAuthService>();
            var totp = scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Existing MFA",
                $"endpoint-existing-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            userId = user.Id;
            var enrollment = await auth.StartTotpEnrollmentAsync(user.Id, new SqlOSTotpEnrollmentStartRequest());
            await auth.VerifyTotpEnrollmentAsync(new SqlOSTotpEnrollmentVerifyRequest(
                enrollment.EnrollmentToken,
                totp.GenerateCodeForTesting(enrollment.Secret)));
            recoveryHashes = await context.Set<SqlOSRecoveryCode>()
                .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .Select(x => x.CodeHash)
                .ToArrayAsync();
        }

        var loginResponse = await client.PostAsJsonAsync("/sqlos/auth/password/login", new
        {
            email,
            password = "P@ssword123!",
            clientId = "test-client"
        });
        loginResponse.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var mfaToken = loginJson.RootElement.GetProperty("mfaToken").GetString()!;
        loginJson.RootElement.GetProperty("requiresMfaEnrollment").GetBoolean().Should().BeFalse();

        var startResponse = await client.PostAsJsonAsync(
            "/sqlos/auth/mfa/challenge/totp/enroll/start",
            new { mfaToken, displayName = "Attacker authenticator" });
        startResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await startResponse.Content.ReadAsStringAsync()).Should().Contain("not authorized for this challenge");

        await using var verifyScope = server.App.Services.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSUserAuthenticator>()
            .CountAsync(x => x.UserId == userId && x.RevokedAt == null)).Should().Be(1);
        (await verifyContext.Set<SqlOSRecoveryCode>()
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .Select(x => x.CodeHash)
            .ToArrayAsync()).Should().BeEquivalentTo(recoveryHashes);
        (await verifyContext.Set<SqlOSSession>().CountAsync(x => x.UserId == userId)).Should().Be(0);
    }

    private static async Task VerifyHeadlessEndpointBindsAuthorizationRequestAsync(
        MfaEndpointServer server,
        HttpClient client)
    {
        string email;
        string requestA;
        string requestB;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var authorization = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Headless MFA",
                $"endpoint-headless-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            requestA = (await CreateAuthorizationRequestAsync(authorization, "headless-a", email, "headless")).Id;
            requestB = (await CreateAuthorizationRequestAsync(authorization, "headless-b", email, "headless")).Id;
        }

        var loginResponse = await client.PostAsJsonAsync("/sqlos/auth/headless/password/login", new
        {
            requestId = requestA,
            email,
            password = "P@ssword123!"
        });
        loginResponse.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var view = loginJson.RootElement.GetProperty("viewModel");
        var mfaToken = view.GetProperty("mfaToken").GetString()!;
        var enrollment = view.GetProperty("totpEnrollment");
        var enrollmentToken = enrollment.GetProperty("enrollmentToken").GetString()!;
        var secret = enrollment.GetProperty("secret").GetString()!;
        var code = await GenerateCodeAsync(server, secret);

        var rejected = await client.PostAsJsonAsync("/sqlos/auth/headless/mfa/totp/enroll/verify", new
        {
            requestId = requestB,
            mfaToken,
            enrollmentToken,
            code
        });
        rejected.EnsureSuccessStatusCode();
        using (var rejectedJson = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
        {
            rejectedJson.RootElement.GetProperty("type").GetString().Should().Be("view");
            rejectedJson.RootElement.GetProperty("viewModel").GetProperty("error").GetString()
                .Should().Be("MFA enrollment is not authorized for this challenge.");
        }

        await AssertNoAuthorizationArtifactsAsync(server, requestA, requestB);

        var accepted = await client.PostAsJsonAsync("/sqlos/auth/headless/mfa/totp/enroll/verify", new
        {
            requestId = requestA,
            mfaToken,
            enrollmentToken,
            code
        });
        accepted.EnsureSuccessStatusCode();
        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        acceptedJson.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        await AssertAuthorizationCodeOnlyForAsync(server, requestA, requestB);
    }

    private static async Task VerifyHostedEndpointBindsAuthorizationRequestAsync(
        MfaEndpointServer server,
        HttpClient client)
    {
        string email;
        string requestA;
        string requestB;
        await using (var scope = server.App.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var authorization = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Endpoint Hosted MFA",
                $"endpoint-hosted-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            email = user.DefaultEmail!;
            requestA = (await CreateAuthorizationRequestAsync(authorization, "hosted-a", email, null)).Id;
            requestB = (await CreateAuthorizationRequestAsync(authorization, "hosted-b", email, null)).Id;
        }

        var loginResponse = await client.PostAsync(
            "/sqlos/auth/login/password",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = requestA,
                ["email"] = email,
                ["password"] = "P@ssword123!"
            }));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginResponse.Content.ReadAsStringAsync();
        var mfaToken = ExtractHtmlValue(html, "mfaToken");
        var enrollmentToken = ExtractHtmlValue(html, "enrollmentToken");
        var secret = Regex.Match(html, @"<span>Setup key</span>\s*<code>([^<]+)</code>").Groups[1].Value;
        secret.Should().NotBeNullOrWhiteSpace();
        var code = await GenerateCodeAsync(server, WebUtility.HtmlDecode(secret));

        var rejected = await client.PostAsync(
            "/sqlos/auth/mfa/totp/enroll/verify",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = requestB,
                ["mfaToken"] = mfaToken,
                ["enrollmentToken"] = enrollmentToken,
                ["code"] = code
            }));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoAuthorizationArtifactsAsync(server, requestA, requestB);

        var accepted = await client.PostAsync(
            "/sqlos/auth/mfa/totp/enroll/verify",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = requestA,
                ["mfaToken"] = mfaToken,
                ["enrollmentToken"] = enrollmentToken,
                ["code"] = code
            }));
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await AssertAuthorizationCodeOnlyForAsync(server, requestA, requestB);
    }

    private static async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(
        SqlOSAuthorizationServerService authorization,
        string state,
        string email,
        string? presentationMode)
        => await authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "test-client",
            "https://client.example.test/callback",
            state,
            "openid profile email",
            $"challenge-{state}-1234567890123456789012345678901234567890123",
            "S256",
            null,
            email,
            null,
            null,
            presentationMode,
            null));

    private static async Task<string> GenerateCodeAsync(MfaEndpointServer server, string secret)
    {
        await using var scope = server.App.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<SqlOSTotpMfaService>().GenerateCodeForTesting(secret);
    }

    private static string ExtractHtmlValue(string html, string inputName)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(inputName)}"" value=""([^""]+)""",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"hosted MFA HTML should include {inputName}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task AssertNoAuthorizationArtifactsAsync(
        MfaEndpointServer server,
        string requestA,
        string requestB)
    {
        await using var scope = server.App.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.AuthorizationRequestId == requestA || x.AuthorizationRequestId == requestB)).Should().Be(0);
        (await context.Set<SqlOSSession>().CountAsync(x =>
            x.AuthenticationMethod != null && x.AuthenticationMethod.Contains("totp"))).Should().Be(0);
        (await context.Set<SqlOSRefreshToken>().CountAsync()).Should().Be(0);
    }

    private static async Task AssertAuthorizationCodeOnlyForAsync(
        MfaEndpointServer server,
        string requestA,
        string requestB)
    {
        await using var scope = server.App.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.AuthorizationRequestId == requestA)).Should().Be(1);
        (await context.Set<SqlOSAuthorizationCode>().CountAsync(x => x.AuthorizationRequestId == requestB)).Should().Be(0);
    }

    private static async Task<VerificationOutcome> CaptureAsync(
        SqlOSAuthService auth,
        SqlOSTotpEnrollmentVerifyRequest request)
    {
        try
        {
            await auth.VerifyTotpEnrollmentAsync(request, CreateHttpContext());
            return new VerificationOutcome(true, null);
        }
        catch (Exception ex)
        {
            return new VerificationOutcome(false, ex);
        }
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.230");
        context.Request.Headers.UserAgent = "SqlOSMfaIntegrationTest";
        return context;
    }

    private sealed record VerificationOutcome(bool Success, Exception? Error);

    private sealed class SqlMfaFixture : IAsyncDisposable
    {
        public required TestSqlOSDbContext Context { get; init; }
        public required SqlOSAuthServerOptions Options { get; init; }
        public required SqlOSAdminService Admin { get; init; }
        public required SqlOSAuthService Auth { get; init; }
        public required SqlOSTotpMfaService Totp { get; init; }
        public required SqlOSCryptoService Crypto { get; init; }
        public required SqlOSSettingsService Settings { get; init; }
        public bool DeleteDatabaseOnDispose { get; init; }

        public static async Task<SqlMfaFixture> CreateAsync(string databasePrefix)
        {
            var context = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix);
            var options = CreateOptions();
            var fixture = Build(context, options, deleteDatabaseOnDispose: true);
            await fixture.Admin.UpsertSeededClientsAsync();
            await fixture.Settings.UpsertSeededMfaSettingsAsync();
            await fixture.Crypto.EnsureActiveSigningKeyAsync();
            return fixture;
        }

        public static SqlMfaFixture CreateForExistingDatabase(
            string connectionString,
            SqlOSAuthServerOptions options)
        {
            var context = new TestSqlOSDbContext(
                new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseSqlServer(connectionString)
                    .Options);
            return Build(context, options, deleteDatabaseOnDispose: false);
        }

        private static SqlMfaFixture Build(
            TestSqlOSDbContext context,
            SqlOSAuthServerOptions optionsValue,
            bool deleteDatabaseOnDispose)
        {
            var options = Microsoft.Extensions.Options.Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);
            var sender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, sender);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, sender, options);
            var abuse = new SqlOSPasswordLoginAbuseService(context, admin, crypto, options);
            var policy = new SqlOSMfaPolicyService(context, settings, options);
            var totp = new SqlOSTotpMfaService(context, crypto, policy, options);
            var auth = new SqlOSAuthService(
                context,
                options,
                admin,
                crypto,
                settings,
                emailOtp,
                passwordLoginAbuseService: abuse,
                mfaPolicyService: policy,
                totpMfaService: totp);

            return new SqlMfaFixture
            {
                Context = context,
                Options = optionsValue,
                Admin = admin,
                Auth = auth,
                Totp = totp,
                Crypto = crypto,
                Settings = settings,
                DeleteDatabaseOnDispose = deleteDatabaseOnDispose
            };
        }

        public async Task<SqlOSLoginResult> LoginAsync(SqlOSUser user)
        {
            var result = await Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(user.DefaultEmail!, "P@ssword123!", "test-client", null),
                CreateHttpContext());
            result.RequiresMfa.Should().BeTrue();
            result.RequiresMfaEnrollment.Should().BeTrue();
            result.Tokens.Should().BeNull();
            return result;
        }

        private static SqlOSAuthServerOptions CreateOptions()
        {
            var options = new SqlOSAuthServerOptions
            {
                Issuer = "https://tests/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            options.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
            options.Mfa.Enabled = true;
            options.Mfa.RequireForAllUsersByDefault = true;
            options.Mfa.AllowUserSelfEnrollmentByDefault = true;
            options.Mfa.RecoveryCodesEnabledByDefault = true;
            return options;
        }

        public async ValueTask DisposeAsync()
        {
            if (DeleteDatabaseOnDispose)
            {
                await Context.Database.EnsureDeletedAsync();
            }

            await Context.DisposeAsync();
        }
    }

    private sealed class MfaEndpointServer : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public static async Task<MfaEndpointServer> CreateAsync()
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("MfaEndpoint");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(db => db.UseSqlServer(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = "https://tests/sqlos/auth";
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
                options.AuthServer.Mfa.Enabled = true;
                options.AuthServer.Mfa.RequireForAllUsersByDefault = true;
                options.AuthServer.Mfa.AllowUserSelfEnrollmentByDefault = true;
                options.AuthServer.Mfa.RecoveryCodesEnabledByDefault = true;
                options.AuthServer.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["password"];
                    page.EnablePasswordSignup = true;
                });
                options.AuthServer.UseHeadlessAuthPage(headless =>
                {
                    headless.BuildUiUrl = context =>
                        $"https://app.example.test/authorize?request={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(context.View)}";
                });
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await app.StartAsync();

            await using var scope = app.Services.CreateAsyncScope();
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var settings = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            await settings.UpsertSeededAuthPageSettingsAsync();
            await settings.UpsertSeededMfaSettingsAsync();

            return new MfaEndpointServer { App = app };
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
