using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthorizationPromptTests
{
    private const string ValidCodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_PublicClientCannotDisablePkce()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("public-web", "Public Web", "https://app.example.test/auth/callback");
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);
        await admin.UpsertSeededClientsAsync();

        var client = await context.Set<SqlOS.AuthServer.Models.SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == "public-web");
        client.RequirePkce = false;
        client.TokenEndpointAuthMethod = "none";
        await context.SaveChangesAsync();

        var missingPkce = async () => await authorizationServer.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                client.ClientId,
                "https://app.example.test/auth/callback",
                "state-public",
                "openid",
                null,
                null,
                null,
                null,
                null,
                null,
                "hosted",
                null));
        await missingPkce.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A PKCE code challenge is required.");

        var plainPkce = async () => await authorizationServer.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                client.ClientId,
                "https://app.example.test/auth/callback",
                "state-public",
                "openid",
                "challenge",
                "plain",
                null,
                null,
                null,
                null,
                "hosted",
                null));
        await plainPkce.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Only S256 PKCE is supported.");

        var invalidS256Challenge = async () => await authorizationServer.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                client.ClientId,
                "https://app.example.test/auth/callback",
                "state-public",
                "openid",
                new string('A', 42),
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));
        await invalidS256Challenge.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*43-character RFC 7636 S256 value*");
        (await context.Set<SqlOS.AuthServer.Models.SqlOSAuthorizationRequest>().CountAsync())
            .Should().Be(0);
    }

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_ValidatesMaxAge()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("maxage-web", "MaxAge Web", "https://app.example.test/auth/callback");
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);
        await admin.UpsertSeededClientsAsync();

        SqlOSAuthorizeRequestInput Input(string? maxAge) => new(
            "code",
            "maxage-web",
            "https://app.example.test/auth/callback",
            "state-maxage",
            "openid",
            ValidCodeChallenge,
            "S256",
            null,
            null,
            null,
            null,
            "hosted",
            null,
            maxAge);

        var garbage = async () => await authorizationServer.CreateAuthorizationRequestAsync(Input("not-a-number"));
        await garbage.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("max_age must be a non-negative integer.");

        var negative = async () => await authorizationServer.CreateAuthorizationRequestAsync(Input("-1"));
        await negative.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("max_age must be a non-negative integer.");

        // A present but whitespace-only value must not silently drop the
        // requested freshness constraint by reading as absent.
        var whitespace = async () => await authorizationServer.CreateAuthorizationRequestAsync(Input(" "));
        await whitespace.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("max_age must be a non-negative integer.");

        var zero = await authorizationServer.CreateAuthorizationRequestAsync(Input("0"));
        zero.Id.Should().NotBeNullOrWhiteSpace();

        var absent = await authorizationServer.CreateAuthorizationRequestAsync(Input(null));
        absent.Id.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_RejectsPromptNoneCombinedWithOtherValues()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("prompt-web", "Prompt Web", "https://app.example.test/auth/callback");
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);
        await admin.UpsertSeededClientsAsync();

        SqlOSAuthorizeRequestInput Input(string? prompt) => new(
            "code",
            "prompt-web",
            "https://app.example.test/auth/callback",
            "state-prompt",
            "openid",
            ValidCodeChallenge,
            "S256",
            null,
            null,
            prompt,
            null,
            "hosted",
            null);

        // OIDC Core 3.1.2.1: none MUST NOT be combined with any other value.
        var combined = async () => await authorizationServer.CreateAuthorizationRequestAsync(Input("none login"));
        await combined.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("prompt cannot combine none with other values.");

        var consentCombined = async () => await authorizationServer.CreateAuthorizationRequestAsync(Input("consent none"));
        await consentCombined.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("prompt cannot combine none with other values.");

        var noneAlone = await authorizationServer.CreateAuthorizationRequestAsync(Input("none"));
        noneAlone.Id.Should().NotBeNullOrWhiteSpace();

        var interactive = await authorizationServer.CreateAuthorizationRequestAsync(Input("login consent"));
        interactive.Id.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void RequiresFreshAuthentication_MapsMaxAgeZeroAndForcedPrompts()
    {
        static SqlOS.AuthServer.Models.SqlOSAuthorizationRequest Request(long? maxAgeSeconds, string? prompt) => new()
        {
            Id = "req_fresh",
            ClientApplicationId = "client",
            RedirectUri = "https://app.example.test/auth/callback",
            State = "state",
            Scope = "openid",
            CodeChallenge = ValidCodeChallenge,
            CodeChallengeMethod = "S256",
            MaxAgeSeconds = maxAgeSeconds,
            Prompt = prompt,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        SqlOSAuthorizationServerService.RequiresFreshAuthentication(Request(0, null)).Should().BeTrue();
        SqlOSAuthorizationServerService.RequiresFreshAuthentication(Request(null, "login")).Should().BeTrue();
        SqlOSAuthorizationServerService.RequiresFreshAuthentication(Request(null, "select_account consent")).Should().BeTrue();
        SqlOSAuthorizationServerService.RequiresFreshAuthentication(Request(null, null)).Should().BeFalse();
        SqlOSAuthorizationServerService.RequiresFreshAuthentication(Request(60, "consent")).Should().BeFalse();
        SqlOSAuthorizationServerService.RequiresFreshAuthentication(Request(null, "none")).Should().BeFalse();
    }

    [TestMethod]
    public void TryParseMaxAge_AcceptsOnlyUnsignedIntegers()
    {
        SqlOSAuthorizationServerService.TryParseMaxAge(null, out var absent).Should().BeTrue();
        absent.Should().BeNull();

        SqlOSAuthorizationServerService.TryParseMaxAge("", out var blank).Should().BeTrue();
        blank.Should().BeNull();

        SqlOSAuthorizationServerService.TryParseMaxAge("0", out var zero).Should().BeTrue();
        zero.Should().Be(0);

        SqlOSAuthorizationServerService.TryParseMaxAge("86400", out var day).Should().BeTrue();
        day.Should().Be(86400);

        // long.MaxValue is accepted; the age comparison must use TotalSeconds so
        // no TimeSpan is constructed (TimeSpan.FromSeconds would overflow).
        SqlOSAuthorizationServerService.TryParseMaxAge("9223372036854775807", out var huge).Should().BeTrue();
        huge.Should().Be(long.MaxValue);
        var age = (DateTime.UtcNow - DateTime.UtcNow.AddYears(-1)).TotalSeconds >= huge!.Value;
        age.Should().BeFalse();

        SqlOSAuthorizationServerService.TryParseMaxAge("9223372036854775808", out _).Should().BeFalse();
        // A present but whitespace-only value is malformed, not absent: treating
        // it as absent would silently drop the requested freshness constraint.
        SqlOSAuthorizationServerService.TryParseMaxAge(" ", out _).Should().BeFalse();
        SqlOSAuthorizationServerService.TryParseMaxAge("\t", out _).Should().BeFalse();
        SqlOSAuthorizationServerService.TryParseMaxAge("-1", out _).Should().BeFalse();
        SqlOSAuthorizationServerService.TryParseMaxAge("+1", out _).Should().BeFalse();
        SqlOSAuthorizationServerService.TryParseMaxAge(" 5", out _).Should().BeFalse();
        SqlOSAuthorizationServerService.TryParseMaxAge("5.5", out _).Should().BeFalse();
        SqlOSAuthorizationServerService.TryParseMaxAge("abc", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task BuildAuthorizationErrorRedirectAsync_CancelsRequest_AndPreservesState()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("example-web", "Example Web", "https://app.example.test/auth/callback");
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorizationServerService = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();

        var authorizationRequest = await authorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "example-web",
                "https://app.example.test/auth/callback",
                "state-123",
                "openid profile email",
                ValidCodeChallenge,
                "S256",
                null,
                "alice@example.com",
                "none",
                null,
                "hosted",
                null));

        var redirectUrl = await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
            authorizationRequest,
            "login_required",
            "The user is not signed in.");

        redirectUrl.Should().StartWith("https://app.example.test/auth/callback?");
        redirectUrl.Should().Contain("error=login_required");
        redirectUrl.Should().Contain("error_description=The%20user%20is%20not%20signed%20in.");
        redirectUrl.Should().Contain("state=state-123");

        authorizationRequest.CancelledAt.Should().NotBeNull();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}
