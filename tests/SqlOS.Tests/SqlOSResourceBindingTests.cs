using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSResourceBindingTests
{
    [TestMethod]
    public void ValidateAccessTokenAsync_RequiresExpectedAudience_ForPublicApi()
    {
        var publicValidatorTypes = new[]
        {
            typeof(SqlOSAuthService),
            typeof(SqlOSCryptoService)
        };

        foreach (var validatorType in publicValidatorTypes)
        {
            var overloads = validatorType
                .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(method => method.Name == nameof(SqlOSAuthService.ValidateAccessTokenAsync))
                .ToList();

            overloads.Should().NotBeEmpty();
            overloads.Should().OnlyContain(method =>
                method.GetParameters().Any(parameter =>
                    parameter.Name == "expectedAudience"
                    && parameter.ParameterType == typeof(string)));
        }
    }

    [TestMethod]
    public async Task ValidateAccessTokenWithoutAudienceForIntrospectionOnly_AllowsSignatureAndLifetimeButIsExplicitlyNamed()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "introspection-client", "https://client.example.test/callback", "https://api-a.example.test");

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1");

        var wrongAudience = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "https://api-b.example.test");
        wrongAudience.Should().BeNull();

#pragma warning disable CS0618
        var introspected = await auth.ValidateAccessTokenWithoutAudienceForIntrospectionOnlyAsync(tokens.AccessToken);
#pragma warning restore CS0618

        introspected.Should().NotBeNull();
        introspected!.Audience.Should().Be("https://api-a.example.test");

        var method = typeof(SqlOSAuthService).GetMethod(
            nameof(SqlOSAuthService.ValidateAccessTokenWithoutAudienceForIntrospectionOnlyAsync),
            [typeof(string), typeof(CancellationToken)]);
        method.Should().NotBeNull();
        method!.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task ResourceServerMiddleware_RejectsTokenForDifferentAudience()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "middleware-client", "https://client.example.test/callback", "https://api-a.example.test");

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1");

        var nextCalled = false;
        var middleware = new SqlOSAccessTokenValidationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new SqlOSAccessTokenValidationOptions
            {
                ExpectedAudience = "https://api-b.example.test"
            });

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {tokens.AccessToken}";

        await middleware.InvokeAsync(httpContext, auth);

        nextCalled.Should().BeFalse();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        httpContext.GetSqlOSValidatedToken().Should().BeNull();
    }

    [TestMethod]
    public void ResourceServerMiddleware_FailsClosed_WhenAudienceNotConfigured()
    {
        var constructMiddleware = () => new SqlOSAccessTokenValidationMiddleware(
            _ => Task.CompletedTask,
            new SqlOSAccessTokenValidationOptions());

        constructMiddleware.Should().Throw<InvalidOperationException>()
            .WithMessage("*expected audience*");

        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        var configurePipeline = () => app.UseSqlOSAccessTokenValidation("");

        configurePipeline.Should().Throw<InvalidOperationException>()
            .WithMessage("*expected audience*");
    }

    [TestMethod]
    public void DocsExamples_DoNotUseNoAudienceValidationOverload()
    {
        var docsRoot = Path.Combine(FindRepoRoot(), "web", "content", "docs");
        var unsafeValidationCall = new System.Text.RegularExpressions.Regex(
            @"ValidateAccessTokenAsync\s*\((?<arguments>[\s\S]*?)\);",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var offenders = Directory
            .EnumerateFiles(docsRoot, "*.mdx", SearchOption.AllDirectories)
            .Where(path => HasNoAudienceValidationExample(File.ReadAllText(path), unsafeValidationCall))
            .Select(path => Path.GetRelativePath(FindRepoRoot(), path))
            .ToList();

        offenders.Should().BeEmpty("resource-server docs must pass an expected audience to ValidateAccessTokenAsync");
    }

    [TestMethod]
    public async Task CreateSessionTokensForUserAsync_UsesResourceAsAudience()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "resource-client", "https://client.example.test/callback", "sqlos-api");

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            "https://api.example.test/resource");

        var session = await context.Set<SqlOSSession>().SingleAsync();
        session.Resource.Should().Be("https://api.example.test/resource");
        session.EffectiveAudience.Should().Be("https://api.example.test/resource");

        var validated = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "https://api.example.test/resource");
        validated.Should().NotBeNull();

        var wrongAudience = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "sqlos-api");
        wrongAudience.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateSessionTokensForUserAsync_FallsBackToClientAudience_WhenResourceMissing()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "audience-client", "https://client.example.test/callback", "sqlos-api");

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1");

        var session = await context.Set<SqlOSSession>().SingleAsync();
        session.Resource.Should().BeNull();
        session.EffectiveAudience.Should().Be("sqlos-api");

        var validated = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "sqlos-api");
        validated.Should().NotBeNull();
    }

    [TestMethod]
    public async Task RefreshAsync_PreservesOriginalResourceBinding()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "refresh-client", "https://client.example.test/callback", "sqlos-api");

        var initial = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            "https://api.example.test/resource");

        var refreshed = await auth.RefreshAsync(new SqlOSRefreshRequest(initial.RefreshToken, null, "https://api.example.test/resource"));

        var validated = await auth.ValidateAccessTokenAsync(refreshed.AccessToken, "https://api.example.test/resource");
        validated.Should().NotBeNull();
    }

    [TestMethod]
    public async Task RefreshAsync_RejectsMismatchedResource()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "refresh-client", "https://client.example.test/callback", "sqlos-api");

        var initial = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            "https://api.example.test/resource");

        var act = async () => await auth.RefreshAsync(new SqlOSRefreshRequest(initial.RefreshToken, null, "https://api.example.test/other"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Resource does not match*");
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCodeAsync_RejectsMismatchedResource()
    {
        using var context = CreateContext();
        var (options, admin, auth, authorizationServer, crypto) = CreateAuthorizationHarness(context);
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        await admin.UpsertSeededClientsAsync();

        var codeVerifier = crypto.GenerateOpaqueToken();
        var redirectUri = "https://client.example.test/callback";
        var authorizationRequest = await authorizationServer.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "resource-client",
            redirectUri,
            "state-123",
            "openid profile",
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256",
            "https://api.example.test/resource",
            null,
            null,
            null,
            "hosted",
            null));

        var redirect = await authorizationServer.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            null,
            "password",
            new DefaultHttpContext());
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        var act = async () => await authorizationServer.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                redirectUri,
                "resource-client",
                codeVerifier,
                null,
                "https://api.example.test/other"),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Resource does not match*");
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCodeAsync_MintsAccessTokenForRequestedResource()
    {
        using var context = CreateContext();
        var (options, admin, auth, authorizationServer, crypto) = CreateAuthorizationHarness(context);
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        await admin.UpsertSeededClientsAsync();

        var codeVerifier = crypto.GenerateOpaqueToken();
        var redirectUri = "https://client.example.test/callback";
        var authorizationRequest = await authorizationServer.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "resource-client",
            redirectUri,
            "state-123",
            "openid profile",
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256",
            "https://api.example.test/resource",
            null,
            null,
            null,
            "hosted",
            null));

        var redirect = await authorizationServer.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            null,
            "password",
            new DefaultHttpContext());
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        var result = await authorizationServer.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                redirectUri,
                "resource-client",
                codeVerifier,
                null,
                "https://api.example.test/resource"),
            new DefaultHttpContext());

        var validated = await auth.ValidateAccessTokenAsync(result.Tokens.AccessToken, "https://api.example.test/resource");
        validated.Should().NotBeNull();
        var session = await context.Set<SqlOSSession>().SingleAsync(x => x.Id == result.Tokens.SessionId);
        session.Resource.Should().Be("https://api.example.test/resource");
        session.EffectiveAudience.Should().Be("https://api.example.test/resource");
    }

    private static async Task<SqlOSUser> SeedUserAsync(TestSqlOSInMemoryDbContext context)
    {
        var user = new SqlOSUser
        {
            Id = "usr_test",
            DisplayName = "Alice",
            DefaultEmail = "alice@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Set<SqlOSUser>().Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<SqlOSClientApplication> SeedClientAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue,
        string clientId,
        string redirectUri,
        string audience)
    {
        var client = new SqlOSClientApplication
        {
            Id = $"cli_{clientId}",
            ClientId = clientId,
            Name = clientId,
            Audience = audience,
            RedirectUrisJson = $"[\"{redirectUri}\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            RegistrationSource = "manual",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]"
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();
        return client;
    }

    private static (IOptions<SqlOSAuthServerOptions> Options, SqlOSAdminService Admin, SqlOSAuthService Auth) CreateAuthHarness(TestSqlOSInMemoryDbContext context)
    {
        var optionsValue = new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        return (options, admin, auth);
    }

    private static (
        IOptions<SqlOSAuthServerOptions> Options,
        SqlOSAdminService Admin,
        SqlOSAuthService Auth,
        SqlOSAuthorizationServerService AuthorizationServer,
        SqlOSCryptoService Crypto) CreateAuthorizationHarness(TestSqlOSInMemoryDbContext context)
    {
        var optionsValue = new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };
        optionsValue.SeedBrowserClient("resource-client", "Resource Client", "https://client.example.test/callback");
        optionsValue.ClientSeeds[0].Audience = "sqlos-api";

        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorizationServer = new SqlOSAuthorizationServerService(context, admin, auth, crypto, settings, authPageSession, options);
        return (options, admin, auth, authorizationServer, crypto);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SqlOS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the SqlOS repository root.");
    }

    private static bool HasNoAudienceValidationExample(
        string markdown,
        System.Text.RegularExpressions.Regex validationCall)
    {
        foreach (System.Text.RegularExpressions.Match match in validationCall.Matches(markdown))
        {
            var arguments = match.Groups["arguments"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (arguments.Length == 1)
            {
                return true;
            }

            if (arguments.Length == 2
                && (string.Equals(arguments[1], "ct", StringComparison.Ordinal)
                    || string.Equals(arguments[1], "cancellationToken", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
