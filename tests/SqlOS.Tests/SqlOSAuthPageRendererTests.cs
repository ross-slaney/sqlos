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
        html.Should().Contain("href=\"/sqlos/auth/password/forgot?request=req_password&amp;email=alice%40example.com\"");
        html.Should().Contain("Forgot password?");
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
        var forgotHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "forgot-password",
            requestId: "req_forgot",
            email: "alice@example.com"));
        var forgotSentHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(mode: "forgot-password-sent"));

        signupHtml.Should().Contain("action=\"/sqlos/auth/signup/submit\"");
        signupHtml.Should().Contain("href=\"/sqlos/auth/login?request=req_signup\"");
        organizationHtml.Should().Contain("action=\"/sqlos/auth/login/select-organization\"");
        loggedOutHtml.Should().Contain("href=\"/sqlos/auth/login\"");
        forgotHtml.Should().Contain("action=\"/sqlos/auth/password/forgot/submit\"");
        forgotHtml.Should().Contain("value=\"alice@example.com\"");
        forgotSentHtml.Should().Contain("Check your email.");
    }

    [TestMethod]
    public void RenderPage_StandaloneStatusModes_DoNotShowLoginForm()
    {
        var signedInHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(mode: "signed-in"));
        var signedUpHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(mode: "signed-up"));
        var invitationHtml = SqlOSAuthPageRenderer.RenderPage(CreateModel(mode: "invitation-accepted"));

        signedInHtml.Should().Contain("You are signed in.");
        signedUpHtml.Should().Contain("Your account is ready.");
        invitationHtml.Should().Contain("Invitation accepted.");
        signedInHtml.Should().Contain("href=\"/sqlos/auth/logout\"");
        signedInHtml.Should().NotContain("action=\"/sqlos/auth/login/identify\"");
        signedUpHtml.Should().NotContain("action=\"/sqlos/auth/login/identify\"");
        invitationHtml.Should().NotContain("action=\"/sqlos/auth/login/identify\"");
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
            ManagedByStartupSeed: false,
            HeadlessCapabilityRegistered: false,
            LocalPasswordRuntimeEnabled: true,
            EmailOtpRuntimeConfigured: false);

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
            ManagedByStartupSeed: false,
            HeadlessCapabilityRegistered: false,
            LocalPasswordRuntimeEnabled: true,
            EmailOtpRuntimeConfigured: false);

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

    [TestMethod]
    public void RenderPage_WhenProviderLogoIsAvailable_RendersProviderImage()
    {
        var model = CreateModel(
            mode: "login",
            requestId: "req_provider_logo",
            email: "alice@example.com",
            providers: new[]
            {
                new SqlOSAuthPageProviderLink(
                    "oidc_microsoft",
                    "Microsoft",
                    "/sqlos/auth/login/oidc/oidc_microsoft?request=req_provider_logo&email=alice%40example.com",
                    "data:image/svg+xml;charset=utf-8,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20viewBox%3D%220%200%2024%2024%22%3E%3C%2Fsvg%3E")
            });

        var html = SqlOSAuthPageRenderer.RenderPage(model);

        html.Should().Contain("class=\"provider-logo\"");
        html.Should().Contain("src=\"data:image/svg+xml;charset=utf-8,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20viewBox%3D%220%200%2024%2024%22%3E%3C%2Fsvg%3E\"");
    }

    [TestMethod]
    public void RenderPage_MagicLinkConfirm_UsesPostOnlyCompletionForm()
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
            EnabledCredentialTypes: ["magic_link"],
            UpdatedAt: DateTime.UtcNow,
            ManagedByStartupSeed: false,
            HeadlessCapabilityRegistered: false,
            LocalPasswordRuntimeEnabled: false,
            EmailOtpRuntimeConfigured: false,
            MagicLinkRuntimeConfigured: true);

        var html = SqlOSAuthPageRenderer.RenderPage(CreateModel(
            mode: "magic-link-confirm",
            requestId: "req_magic",
            pendingToken: "magic_token_123",
            settings: settings));

        html.Should().Contain("action=\"/sqlos/auth/login/magic-link/complete\"");
        html.Should().Contain("method=\"post\"");
        html.Should().Contain("name=\"token\" value=\"magic_token_123\"");
        html.Should().NotContain("action=\"/sqlos/auth/login/magic-link/complete?token=");
    }

    [TestMethod]
    [DataRow("PrimaryColor", "</style><script>alert(1)</script>", "--primary: #4f46e5")]
    [DataRow("AccentColor", "url(https://evil.example)", "--accent: #111827")]
    [DataRow("BackgroundColor", "red;}</style><script>alert(1)</script>", "--page-bg: #f8fafc")]
    public void RenderPage_LegacyInvalidColors_UseSafeDefaults(string field, string payload, string expectedDeclaration)
    {
        var settings = CreateSettings(
            primary: field == "PrimaryColor" ? payload : "#0D9488",
            accent: field == "AccentColor" ? payload : "#1A1A1A",
            background: field == "BackgroundColor" ? payload : "#FAFAF8");

        var html = SqlOSAuthPageRenderer.RenderPage(CreateModel(mode: "login", settings: settings));

        html.Should().Contain(expectedDeclaration);
        html.Should().NotContain(payload);
        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().Contain($"<style {SqlOS.Security.SqlOSCspNonce.Attribute}>");
        html.Should().Contain($"<script {SqlOS.Security.SqlOSCspNonce.Attribute}>");
    }

    private static SqlOSAuthPageSettingsDto CreateSettings(
        string primary,
        string accent,
        string background)
        => new(
            LogoBase64: null,
            PrimaryColor: primary,
            AccentColor: accent,
            BackgroundColor: background,
            Layout: "split",
            PageTitle: "Sign in",
            PageSubtitle: "Test auth page",
            EnablePasswordSignup: false,
            EnabledCredentialTypes: ["password"],
            UpdatedAt: DateTime.UtcNow,
            ManagedByStartupSeed: false,
            HeadlessCapabilityRegistered: false,
            LocalPasswordRuntimeEnabled: true,
            EmailOtpRuntimeConfigured: false);

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
                ManagedByStartupSeed: false,
                HeadlessCapabilityRegistered: false,
                LocalPasswordRuntimeEnabled: true,
                EmailOtpRuntimeConfigured: false),
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
