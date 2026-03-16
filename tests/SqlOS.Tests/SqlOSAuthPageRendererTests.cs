using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthPageRendererTests
{
    [TestMethod]
    public void RenderPage_LoginMode_UsesMountedBasePathForLinksAndActions()
    {
        var model = CreateModel(
            mode: "login",
            requestId: "req_login",
            email: "alice@example.com",
            providers: new[]
            {
                new SqlOSAuthPageProviderLink(
                    "oidc_contoso",
                    "Contoso",
                    "/sqlos/auth/login/oidc/oidc_contoso?request=req_login&email=alice%40example.com")
            });

        var html = SqlOSAuthPageRenderer.RenderPage(model);

        html.Should().Contain("action=\"/sqlos/auth/login/identify\"");
        html.Should().Contain("href=\"/sqlos/auth/signup?request=req_login\"");
        html.Should().Contain("class=\"auth-shell split\"");
        html.Should().Contain("data-flow-kind=\"hrd\"");
        html.Should().Contain("href=\"/sqlos/auth/login/oidc/oidc_contoso?request=req_login&amp;email=alice%40example.com\"");
    }

    [TestMethod]
    public void RenderPage_PasswordMode_UsesMountedBasePathForPasswordPostAndProviders()
    {
        var model = CreateModel(
            mode: "password",
            requestId: "req_password",
            email: "alice@example.com",
            providers: new[]
            {
                new SqlOSAuthPageProviderLink(
                    "oidc_contoso",
                    "Contoso",
                    "/sqlos/auth/login/oidc/oidc_contoso?request=req_password&email=alice%40example.com")
            });

        var html = SqlOSAuthPageRenderer.RenderPage(model);

        html.Should().Contain("action=\"/sqlos/auth/login/password\"");
        html.Should().Contain("href=\"/sqlos/auth/login/oidc/oidc_contoso?request=req_password&amp;email=alice%40example.com\"");
        html.Should().Contain("href=\"/sqlos/auth/signup?request=req_password\"");
    }

    [TestMethod]
    public void RenderPage_OtherModes_UseMountedBasePathForFollowUpActions()
    {
        var signupHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "signup",
            requestId: "req_signup",
            email: "alice@example.com",
            displayName: "Alice"));
        var organizationHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "organization",
            pendingToken: "pending_123",
            organizations: new[]
            {
                new SqlOSOrganizationOption("org_1", "contoso", "Contoso", "admin")
            }));
        var loggedOutHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(mode: "logged-out"));

        signupHtml.Should().Contain("action=\"/sqlos/auth/signup/submit\"");
        signupHtml.Should().Contain("href=\"/sqlos/auth/login?request=req_signup\"");
        organizationHtml.Should().Contain("action=\"/sqlos/auth/login/select-organization\"");
        loggedOutHtml.Should().Contain("href=\"/sqlos/auth/login\"");
    }

    [TestMethod]
    public void RenderPage_StackedLayout_UsesConfiguredBrandingWithoutSecondaryPanel()
    {
        var settings = new SqlOSAuthPageSettingsDto(
            LogoBase64: "data:image/png;base64,AAAA",
            PrimaryColor: "#112233",
            AccentColor: "#445566",
            BackgroundColor: "#778899",
            Layout: "stacked",
            PageTitle: "Sign in to Example",
            PageSubtitle: "Use your work email to access the Example workspace.",
            EnablePasswordSignup: false,
            EnabledCredentialTypes: ["password"],
            UpdatedAt: DateTime.UtcNow,
            ManagedByStartupSeed: false);

        var html = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "login",
            requestId: "req_branding",
            email: "alice@example.com",
            settings: settings));

        html.Should().Contain("--primary: #112233");
        html.Should().Contain("--accent: #445566");
        html.Should().Contain("--page-bg: #778899");
        html.Should().Contain("Sign in to Example");
        html.Should().Contain("Use your work email to access the Example workspace.");
        html.Should().Contain("src=\"data:image/png;base64,AAAA\"");
        html.Should().Contain("class=\"auth-shell stacked\"");
        html.Should().NotContain("Get started");
    }

    [TestMethod]
    public void RenderPage_WhenPasswordSignupDisabled_DoesNotRenderSignupLinks()
    {
        var settings = new SqlOSAuthPageSettingsDto(
            LogoBase64: null,
            PrimaryColor: "#0D9488",
            AccentColor: "#1A1A1A",
            BackgroundColor: "#FAFAF8",
            Layout: "split",
            PageTitle: "Sign in",
            PageSubtitle: "Test auth page",
            EnablePasswordSignup: false,
            EnabledCredentialTypes: ["password"],
            UpdatedAt: DateTime.UtcNow,
            ManagedByStartupSeed: false);

        var loginHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "login",
            requestId: "req_no_signup",
            email: "alice@example.com",
            settings: settings));
        var passwordHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "password",
            requestId: "req_no_signup_password",
            email: "alice@example.com",
            settings: settings));

        loginHtml.Should().NotContain("Get started");
        passwordHtml.Should().NotContain("Get started");
    }

    private static SqlOSAuthPageViewModel CreateModel(
        string mode,
        string? requestId = null,
        string? email = null,
        string? displayName = null,
        string? pendingToken = null,
        IReadOnlyList<SqlOSOrganizationOption>? organizations = null,
        IReadOnlyList<SqlOSAuthPageProviderLink>? providers = null,
        SqlOSAuthPageSettingsDto? settings = null)
    {
        return new SqlOSAuthPageViewModel(
            mode,
            settings ?? new SqlOSAuthPageSettingsDto(
                LogoBase64: null,
                PrimaryColor: "#0D9488",
                AccentColor: "#1A1A1A",
                BackgroundColor: "#FAFAF8",
                Layout: "split",
                PageTitle: "Sign in",
                PageSubtitle: "Test auth page",
                EnablePasswordSignup: true,
                EnabledCredentialTypes: ["password"],
                UpdatedAt: DateTime.UtcNow,
                ManagedByStartupSeed: false),
            "/sqlos/auth",
            requestId,
            email,
            displayName,
            null,
            null,
            pendingToken,
            organizations ?? Array.Empty<SqlOSOrganizationOption>(),
            providers ?? Array.Empty<SqlOSAuthPageProviderLink>());
    }
}
