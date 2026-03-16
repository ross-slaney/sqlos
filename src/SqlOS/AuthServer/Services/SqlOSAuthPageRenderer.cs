using System.Globalization;
using System.Net;
using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Services;

public static class SqlOSAuthPageRenderer
{
    public static string RenderPage(SqlOSAuthPageViewModel model)
    {
        var normalizedMode = NormalizeMode(model.Mode);
        var isStackedLayout = string.Equals(model.Settings.Layout, "stacked", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(model.Settings.PageTitle)
            ? "Sign in"
            : model.Settings.PageTitle;
        var subtitle = model.Settings.PageSubtitle ?? string.Empty;
        var subtitleMarkup = string.IsNullOrWhiteSpace(subtitle)
            ? string.Empty
            : $"<p class=\"brand-subtitle\">{Html(subtitle)}</p>";
        var primaryColor = Css(model.Settings.PrimaryColor, "#4f46e5");
        var accentColor = Css(model.Settings.AccentColor, "#111827");
        var backgroundColor = Css(model.Settings.BackgroundColor, "#f8fafc");
        var isDarkTheme = IsDarkColor(backgroundColor);
        var textColor = isDarkTheme ? "#f8fafc" : accentColor;
        var mutedColor = isDarkTheme ? "rgba(248,250,252,0.72)" : "#6b7280";
        var shellColor = isDarkTheme ? "rgba(9,10,12,0.96)" : "rgba(255,255,255,0.96)";
        var panelColor = isDarkTheme ? "#16181d" : "#ffffff";
        var borderColor = isDarkTheme ? "rgba(255,255,255,0.14)" : "#d9dde6";
        var borderStrongColor = isDarkTheme ? "rgba(255,255,255,0.20)" : "#cfd5df";
        var inputBackground = isDarkTheme ? "#101217" : "#ffffff";
        var buttonTextColor = GetContrastingTextColor(primaryColor);
        var shadowColor = isDarkTheme
            ? "0 28px 90px rgba(0,0,0,0.36)"
            : "0 28px 90px rgba(15,23,42,0.10)";
        var logoMarkup = string.IsNullOrWhiteSpace(model.Settings.LogoBase64)
            ? "<div class=\"logo-fallback\">SqlOS</div>"
            : $"<img class=\"logo\" src=\"{Html(model.Settings.LogoBase64)}\" alt=\"Logo\" />";
        var requestIdInput = string.IsNullOrWhiteSpace(model.AuthorizationRequestId)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"requestId\" value=\"{Html(model.AuthorizationRequestId)}\" />";
        var emailValue = Html(model.Email ?? string.Empty);
        var encodedMode = Html(normalizedMode);
        var signupLink = model.Settings.EnablePasswordSignup
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/signup", model.AuthorizationRequestId))}\">Get started</a>"
            : string.Empty;
        var loginLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/login", model.AuthorizationRequestId))}\">Sign in</a>";
        var signInAgainLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/login"))}\">Sign in again</a>";
        var errorMarkup = BuildCallout("error", model.Error);
        var infoMarkup = BuildCallout("info", model.Info);

        var content = normalizedMode switch
        {
            "signup" => $$"""
                {{RenderPanelIntro("Create account", "Use email and password to set up your account.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/submit"))}}">
                  {{requestIdInput}}
                  <input type="hidden" name="mode" value="{{encodedMode}}" />
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required />
                  </label>
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Create a password" autocomplete="new-password" required />
                  </label>
                  <label class="field">
                    <span>Organization name</span>
                    <input name="organizationName" placeholder="Optional" autocomplete="organization" />
                  </label>
                  {{RenderPrimaryAction("Create account", "Creating account")}}
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterPrompt("Already have an account?", loginLink)}}
                """,
            "password" => $$"""
                {{RenderPanelIntro("Password", "Continue with your email and password.")}}
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/password"))}}">
                  {{requestIdInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required />
                  </label>
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Your password" autocomplete="current-password" required />
                  </label>
                  {{RenderPrimaryAction("Continue", "Signing in")}}
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterPrompt("Don't have an account?", signupLink)}}
                """,
            "organization" => $$"""
                {{RenderPanelIntro("Organization", "Choose the workspace you want to continue into.")}}
                <form class="auth-form organization-form" method="post" action="{{Html(AuthPath(model, "/login/select-organization"))}}">
                  <input type="hidden" name="pendingToken" value="{{Html(model.PendingToken ?? string.Empty)}}" />
                  <div class="organization-list">{{RenderOrganizationOptions(model.OrganizationSelection)}}</div>
                </form>
                {{RenderFooterLinks(loginLink)}}
                """,
            "logged-out" => $$"""
                <div class="state-card">
                  <span class="state-icon">OK</span>
                  <div class="state-copy">
                    <strong>You are signed out.</strong>
                    <p>Your session has ended. Return whenever you are ready to continue.</p>
                  </div>
                </div>
                {{RenderFooterLinks(signInAgainLink)}}
                """,
            _ => $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/identify"))}}" data-flow-kind="hrd">
                  {{requestIdInput}}
                  <label class="field">
                    <span>Email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="Your email address" autocomplete="email" required />
                  </label>
                  {{RenderPrimaryAction("Continue", "Checking email")}}
                  <div class="flow-status" hidden>
                    <span class="loader loader-sm" aria-hidden="true"></span>
                    <span>Checking your sign-in method...</span>
                  </div>
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterPrompt("Don't have an account?", signupLink)}}
                """
        };

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{Html(title)}}</title>
          <style>
            :root {
              --page-bg: {{backgroundColor}};
              --primary: {{primaryColor}};
              --accent: {{accentColor}};
              --text: {{textColor}};
              --muted: {{mutedColor}};
              --shell: {{shellColor}};
              --panel: {{panelColor}};
              --border: {{borderColor}};
              --border-strong: {{borderStrongColor}};
              --input-bg: {{inputBackground}};
              --button-text: {{buttonTextColor}};
              --shadow: {{shadowColor}};
            }
            * { box-sizing: border-box; }
            html, body { min-height: 100%; }
            body {
              margin: 0;
              font-family: Inter, "Segoe UI", system-ui, sans-serif;
              color: var(--text);
              background:
                radial-gradient(circle at top, color-mix(in srgb, var(--primary) 10%, transparent) 0%, transparent 38%),
                linear-gradient(180deg, color-mix(in srgb, var(--page-bg) 92%, white) 0%, var(--page-bg) 100%);
            }
            body::before {
              content: "";
              position: fixed;
              inset: 0;
              pointer-events: none;
              background:
                linear-gradient(color-mix(in srgb, var(--accent) 3%, transparent) 1px, transparent 1px),
                linear-gradient(90deg, color-mix(in srgb, var(--accent) 3%, transparent) 1px, transparent 1px);
              background-size: 40px 40px;
              opacity: 0.2;
            }
            h1, h2, p, strong, small, span { margin: 0; }
            input, button { font: inherit; }
            [hidden] { display: none !important; }
            .page-shell {
              min-height: 100vh;
              padding: 28px 16px;
              display: grid;
              place-items: center;
            }
            .auth-shell {
              width: min(calc(100vw - 32px), {{(isStackedLayout ? "760px" : "840px")}});
            }
            .auth-shell.split {
              max-width: 840px;
            }
            .auth-shell.stacked {
              max-width: 760px;
            }
            .auth-frame {
              background: var(--shell);
              border: 1px solid var(--border);
              border-radius: 32px;
              box-shadow: var(--shadow);
              padding: clamp(24px, 4vw, 40px);
            }
            .brand-header {
              display: grid;
              justify-items: center;
              gap: 14px;
              text-align: center;
              margin-bottom: 20px;
            }
            .logo-shell {
              width: 84px;
              height: 84px;
              border-radius: 20px;
              border: 1px solid var(--border);
              background: color-mix(in srgb, var(--panel) 88%, var(--page-bg));
              display: grid;
              place-items: center;
              overflow: hidden;
            }
            .logo,
            .logo-fallback {
              width: 100%;
              height: 100%;
              object-fit: contain;
            }
            .logo-fallback {
              display: grid;
              place-items: center;
              background: linear-gradient(145deg, color-mix(in srgb, var(--accent) 92%, black), color-mix(in srgb, var(--primary) 68%, var(--accent)));
              color: white;
              font-size: 20px;
              font-weight: 700;
              letter-spacing: -0.04em;
            }
            h1 {
              max-width: 14ch;
              font-size: clamp(30px, 5vw, 44px);
              line-height: 1.08;
              letter-spacing: -0.05em;
              font-weight: 700;
            }
            .brand-subtitle {
              max-width: 34rem;
              color: var(--muted);
              font-size: 14px;
              line-height: 1.6;
            }
            .messages {
              display: grid;
              gap: 10px;
              margin: 0 auto 14px;
              width: min(100%, 560px);
            }
            .form-panel {
              width: min(100%, 620px);
              margin: 0 auto;
              padding: clamp(18px, 3vw, 28px);
              border-radius: 20px;
              border: 1px solid var(--border);
              background: var(--panel);
            }
            .panel-intro {
              display: grid;
              gap: 4px;
              margin-bottom: 14px;
            }
            .panel-kicker {
              color: var(--muted);
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.08em;
              text-transform: uppercase;
            }
            .panel-intro h2 {
              font-size: 18px;
              line-height: 1.2;
              letter-spacing: -0.03em;
              font-weight: 600;
            }
            .panel-intro p {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            .auth-form,
            .organization-list,
            .providers,
            .panel-stack {
              display: grid;
              gap: 14px;
            }
            .field {
              display: grid;
              gap: 6px;
            }
            .field span {
              font-size: 14px;
              font-weight: 500;
            }
            input {
              width: 100%;
              height: 44px;
              padding: 0 14px;
              border-radius: 8px;
              border: 1px solid var(--border-strong);
              background: var(--input-bg);
              color: var(--text);
              font-size: 14px;
              transition: border-color 0.18s ease, box-shadow 0.18s ease, background 0.18s ease;
            }
            input::placeholder {
              color: color-mix(in srgb, var(--muted) 86%, transparent);
            }
            input:focus {
              outline: none;
              border-color: color-mix(in srgb, var(--primary) 32%, white);
              box-shadow: 0 0 0 4px color-mix(in srgb, var(--primary) 18%, transparent);
            }
            .primary-action,
            .provider-link,
            .organization-option,
            .secondary-link {
              transition:
                transform 0.18s ease,
                box-shadow 0.18s ease,
                border-color 0.18s ease,
                background 0.18s ease,
                color 0.18s ease;
            }
            .primary-action {
              width: 100%;
              min-height: 44px;
              border: none;
              border-radius: 8px;
              background: var(--primary);
              color: var(--button-text);
              display: inline-flex;
              align-items: center;
              justify-content: center;
              gap: 8px;
              font-size: 14px;
              font-weight: 500;
              cursor: pointer;
            }
            .primary-action:hover,
            .provider-link:hover,
            .organization-option:hover {
              transform: translateY(-1px);
            }
            .primary-action:focus-visible,
            .provider-link:focus-visible,
            .organization-option:focus-visible,
            .secondary-link:focus-visible {
              outline: 3px solid color-mix(in srgb, var(--primary) 22%, transparent);
              outline-offset: 3px;
            }
            .button-loader,
            .provider-loader,
            .organization-loader {
              opacity: 0;
            }
            .primary-action[data-loading="true"] .button-loader,
            .provider-link[data-loading="true"] .provider-loader,
            .organization-option[data-loading="true"] .organization-loader {
              opacity: 1;
            }
            .primary-action[data-loading="true"],
            .provider-link[data-loading="true"],
            .organization-option[data-loading="true"] {
              pointer-events: none;
            }
            .section-divider {
              display: flex;
              align-items: center;
              gap: 14px;
              color: var(--muted);
              font-size: 12px;
              font-weight: 500;
              line-height: 1;
              margin: 6px 0 2px;
            }
            .section-divider::before,
            .section-divider::after {
              content: "";
              flex: 1;
              height: 1px;
              background: var(--border);
            }
            .provider-link,
            .organization-option {
              width: 100%;
              min-height: 44px;
              padding: 0 14px;
              border-radius: 8px;
              border: 1px solid var(--border-strong);
              background: var(--panel);
              color: var(--text);
              display: flex;
              align-items: center;
              justify-content: center;
              gap: 10px;
              text-decoration: none;
            }
            .provider-link[data-loading="true"] .provider-badge,
            .provider-link[data-loading="true"] .provider-label,
            .organization-option[data-loading="true"] .organization-copy {
              opacity: 0.55;
            }
            .provider-badge,
            .provider-loader,
            .organization-loader {
              flex: none;
            }
            .provider-logo {
              width: 22px;
              height: 22px;
              display: block;
            }
            .provider-generic {
              width: 22px;
              height: 22px;
              border-radius: 6px;
              border: 1px solid color-mix(in srgb, var(--primary) 24%, var(--border));
              background: color-mix(in srgb, var(--primary) 12%, var(--panel));
              display: grid;
              place-items: center;
              font-size: 10px;
              font-weight: 700;
              letter-spacing: 0.04em;
              color: color-mix(in srgb, var(--primary) 64%, var(--text));
            }
            .provider-label {
              font-size: 14px;
              font-weight: 500;
              line-height: 1.35;
            }
            .footer-prompt,
            .footer-links {
              margin-top: 8px;
              color: var(--muted);
              font-size: 14px;
              line-height: 1.6;
              text-align: center;
            }
            .secondary-link {
              color: color-mix(in srgb, var(--primary) 80%, var(--text));
              font-weight: 600;
              text-decoration: none;
            }
            .organization-list {
              gap: 12px;
            }
            .organization-option {
              justify-content: space-between;
              text-align: left;
              cursor: pointer;
            }
            .organization-copy {
              display: grid;
              gap: 4px;
              min-width: 0;
            }
            .organization-copy strong {
              font-size: 14px;
              line-height: 1.35;
              font-weight: 500;
            }
            .organization-copy small {
              color: var(--muted);
              font-size: 12px;
              line-height: 1.45;
            }
            .flow-status {
              display: flex;
              align-items: center;
              justify-content: center;
              gap: 8px;
              color: var(--muted);
              font-size: 12px;
              line-height: 1.45;
              opacity: 0;
              transform: translateY(-4px);
              transition: opacity 0.18s ease, transform 0.18s ease;
            }
            .flow-status[data-visible="true"] {
              opacity: 1;
              transform: translateY(0);
            }
            .loader {
              display: inline-block;
              border-radius: 999px;
              border: 2px solid currentColor;
              border-right-color: transparent;
              animation: spin 0.72s linear infinite;
            }
            .loader-sm {
              width: 14px;
              height: 14px;
            }
            .callout {
              display: flex;
              align-items: flex-start;
              gap: 10px;
              padding: 12px 14px;
              border-radius: 10px;
              border: 1px solid var(--border);
              font-size: 13px;
              line-height: 1.55;
            }
            .callout-icon {
              width: 20px;
              height: 20px;
              border-radius: 999px;
              display: grid;
              place-items: center;
              font-size: 11px;
              font-weight: 700;
              flex: none;
            }
            .callout.error {
              border-color: #fecaca;
              background: #fef2f2;
              color: #991b1b;
            }
            .callout.error .callout-icon {
              background: rgba(220, 38, 38, 0.12);
            }
            .callout.info {
              border-color: #bfdbfe;
              background: #eff6ff;
              color: #1d4ed8;
            }
            .callout.info .callout-icon {
              background: rgba(37, 99, 235, 0.12);
            }
            .state-card {
              display: flex;
              align-items: center;
              gap: 12px;
              padding: 16px;
              border-radius: 12px;
              border: 1px solid var(--border);
              background: var(--panel);
            }
            .state-icon {
              width: 36px;
              height: 36px;
              border-radius: 12px;
              border: 1px solid color-mix(in srgb, var(--primary) 20%, var(--border));
              background: color-mix(in srgb, var(--primary) 12%, var(--panel));
              display: grid;
              place-items: center;
              font-size: 11px;
              font-weight: 700;
              color: color-mix(in srgb, var(--primary) 80%, var(--text));
              flex: none;
            }
            .state-copy {
              display: grid;
              gap: 4px;
            }
            .state-copy strong {
              font-size: 15px;
            }
            .state-copy p {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            @keyframes spin {
              from { transform: rotate(0deg); }
              to { transform: rotate(360deg); }
            }
            @media (max-width: 640px) {
              .page-shell {
                padding: 18px 10px;
              }
              .auth-frame {
                padding: 20px 14px;
                border-radius: 24px;
              }
              .logo-shell {
                width: 72px;
                height: 72px;
                border-radius: 18px;
              }
              .form-panel {
                padding: 16px 14px;
                border-radius: 16px;
              }
              h1 {
                font-size: 26px;
              }
            }
            @media (prefers-reduced-motion: reduce) {
              *,
              *::before,
              *::after {
                animation: none !important;
                transition: none !important;
                scroll-behavior: auto !important;
              }
            }
          </style>
        </head>
        <body>
          <div class="page-shell">
            <main class="auth-shell {{(isStackedLayout ? "stacked" : "split")}}">
              <section class="auth-frame">
                <div class="brand-header">
                  <div class="logo-shell">{{logoMarkup}}</div>
                  <h1>{{Html(title)}}</h1>
                  {{subtitleMarkup}}
                </div>
                <div class="messages">
                  {{errorMarkup}}
                  {{infoMarkup}}
                </div>
                <section class="form-panel">
                  <div class="panel-stack">
                    {{content}}
                  </div>
                </section>
              </section>
            </main>
          </div>
          <script>{{RenderClientScript()}}</script>
        </body>
        </html>
        """;
    }

    private static string RenderPrimaryAction(string label, string loadingLabel)
        => $$"""
            <button class="primary-action" type="submit" data-loading-label="{{Html(loadingLabel)}}">
              <span class="button-label">{{Html(label)}}</span>
              <span class="loader loader-sm button-loader" aria-hidden="true"></span>
            </button>
            """;

    private static string RenderPanelIntro(string kicker, string copy)
        => $$"""
            <div class="panel-intro">
              <span class="panel-kicker">{{Html(kicker)}}</span>
              <p>{{Html(copy)}}</p>
            </div>
            """;

    private static string RenderProvidersSection(SqlOSAuthPageViewModel model)
    {
        if (model.Providers.Count == 0)
        {
            return string.Empty;
        }

        var providersMarkup = string.Join("", model.Providers.Select(RenderProviderLink));
        return $$"""
            <div class="section-divider">OR</div>
            <div class="providers">{{providersMarkup}}</div>
            """;
    }

    private static string RenderProviderLink(SqlOSAuthPageProviderLink provider)
    {
        var providerName = Html(provider.DisplayName);
        return $$"""
            <a class="provider-link js-loading-link" href="{{Html(provider.Url)}}" data-loading-label="Connecting to {{providerName}}">
              <span class="provider-badge">{{RenderProviderBadge(provider.DisplayName)}}</span>
              <span class="provider-label">Continue with {{providerName}}</span>
              <span class="loader loader-sm provider-loader" aria-hidden="true"></span>
            </a>
            """;
    }

    private static string RenderOrganizationOptions(IReadOnlyList<SqlOSOrganizationOption> organizations)
    {
        if (organizations.Count == 0)
        {
            return "<p class=\"footer-links\">No organizations were available for this sign-in attempt.</p>";
        }

        return string.Join("", organizations.Select(option =>
        {
            var detail = string.IsNullOrWhiteSpace(option.Slug)
                ? option.Role
                : $"{option.Slug} · {option.Role}";

            return $$"""
                <button class="organization-option" type="submit" name="organizationId" value="{{Html(option.Id)}}" data-loading-label="Opening workspace">
                  <span class="organization-copy">
                    <strong>{{Html(option.Name)}}</strong>
                    <small>{{Html(detail)}}</small>
                  </span>
                  <span class="loader loader-sm organization-loader" aria-hidden="true"></span>
                </button>
                """;
        }));
    }

    private static string RenderFooterPrompt(string prompt, string linkMarkup)
        => string.IsNullOrWhiteSpace(linkMarkup)
            ? string.Empty
            : $"<p class=\"footer-prompt\">{Html(prompt)} {linkMarkup}</p>";

    private static string RenderFooterLinks(params string[] links)
    {
        var activeLinks = links
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .ToArray();

        return activeLinks.Length == 0
            ? string.Empty
            : $"<div class=\"footer-links\">{string.Join("", activeLinks)}</div>";
    }

    private static string BuildCallout(string kind, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var icon = string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase) ? "!" : "i";
        return $$"""
            <div class="callout {{Html(kind)}}">
              <span class="callout-icon">{{icon}}</span>
              <span>{{Html(message)}}</span>
            </div>
            """;
    }

    private static string RenderProviderBadge(string displayName)
    {
        if (displayName.Contains("google", StringComparison.OrdinalIgnoreCase))
        {
            return """
                <svg class="provider-logo" viewBox="0 0 24 24" aria-hidden="true">
                  <path fill="#EA4335" d="M12 10.2v3.9h5.4c-.22 1.26-.93 2.32-1.95 3.03l3.15 2.44c1.84-1.69 2.9-4.18 2.9-7.15 0-.66-.06-1.3-.18-1.92H12z" />
                  <path fill="#34A853" d="M12 22c2.7 0 4.96-.9 6.61-2.44l-3.15-2.44c-.87.59-1.99.94-3.46.94-2.66 0-4.92-1.79-5.73-4.19l-3.25 2.5C4.66 19.78 8.06 22 12 22z" />
                  <path fill="#FBBC05" d="M6.27 13.87c-.21-.59-.33-1.23-.33-1.87s.12-1.28.33-1.87l-3.25-2.5C2.37 8.93 2 10.43 2 12s.37 3.07 1.02 4.37l3.25-2.5z" />
                  <path fill="#4285F4" d="M12 5.94c1.47 0 2.79.51 3.83 1.5l2.87-2.87C16.95 2.94 14.69 2 12 2 8.06 2 4.66 4.22 3.02 7.63l3.25 2.5c.81-2.4 3.07-4.19 5.73-4.19z" />
                </svg>
                """;
        }

        if (displayName.Contains("microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return """
                <svg class="provider-logo" viewBox="0 0 24 24" aria-hidden="true">
                  <rect x="2" y="2" width="9" height="9" fill="#F25022" />
                  <rect x="13" y="2" width="9" height="9" fill="#7FBA00" />
                  <rect x="2" y="13" width="9" height="9" fill="#00A4EF" />
                  <rect x="13" y="13" width="9" height="9" fill="#FFB900" />
                </svg>
                """;
        }

        var monogram = GetMonogram(displayName);
        return $"<span class=\"provider-generic\">{Html(monogram)}</span>";
    }

    private static string RenderClientScript()
        => """
            (() => {
              let activeSubmitter = null;

              document.addEventListener("click", event => {
                const submitter = event.target.closest('button[type="submit"]');
                if (submitter) {
                  activeSubmitter = submitter;
                  return;
                }

                const loadingLink = event.target.closest(".js-loading-link");
                if (!loadingLink) {
                  return;
                }

                if (loadingLink.dataset.loading === "true") {
                  event.preventDefault();
                  return;
                }

                loadingLink.dataset.loading = "true";
                loadingLink.setAttribute("aria-disabled", "true");
              });

              document.querySelectorAll(".auth-form").forEach(form => {
                form.addEventListener("submit", event => {
                  if (form.dataset.loading === "true") {
                    event.preventDefault();
                    return;
                  }

                  form.dataset.loading = "true";
                  const submitter = event.submitter || activeSubmitter || form.querySelector('button[type="submit"]');

                  if (submitter) {
                    submitter.dataset.loading = "true";

                    if (submitter instanceof HTMLButtonElement) {
                      submitter.disabled = true;
                    }

                    const label = submitter.querySelector(".button-label");
                    if (label && submitter.dataset.loadingLabel) {
                      label.textContent = submitter.dataset.loadingLabel;
                    }
                  }

                  const flowStatus = form.querySelector(".flow-status");
                  if (flowStatus) {
                    flowStatus.hidden = false;
                    window.requestAnimationFrame(() => {
                      flowStatus.dataset.visible = "true";
                    });
                  }
                });
              });
            })();
            """;

    private static string GetMonogram(string value)
    {
        var parts = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray()))
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length == 0)
        {
            return "ID";
        }

        if (parts.Length == 1)
        {
            var token = parts[0].ToUpperInvariant();
            return token.Length == 1 ? token : token[..2];
        }

        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    private static string NormalizeMode(string? mode)
        => string.IsNullOrWhiteSpace(mode)
            ? "login"
            : mode.Trim().ToLowerInvariant();

    private static string BuildRequestQuery(string? requestId)
        => string.IsNullOrWhiteSpace(requestId) ? string.Empty : $"?request={Uri.EscapeDataString(requestId)}";

    private static string AuthPath(SqlOSAuthPageViewModel model, string path, string? requestId = null)
    {
        var basePath = model.BasePath.TrimEnd('/');
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return $"{basePath}{normalizedPath}{BuildRequestQuery(requestId)}";
    }

    private static string Css(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static bool IsDarkColor(string? value)
        => TryParseHexColor(value, out var red, out var green, out var blue) &&
           RelativeLuminance(red, green, blue) < 0.42;

    private static string GetContrastingTextColor(string value)
        => TryParseHexColor(value, out var red, out var green, out var blue) &&
           RelativeLuminance(red, green, blue) > 0.52
            ? "#111827"
            : "#ffffff";

    private static bool TryParseHexColor(string? value, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var color = value.Trim();
        if (color.StartsWith('#'))
        {
            color = color[1..];
        }

        if (color.Length == 3)
        {
            color = string.Concat(color.Select(character => new string(character, 2)));
        }

        if (color.Length != 6)
        {
            return false;
        }

        return int.TryParse(color.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red) &&
               int.TryParse(color.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green) &&
               int.TryParse(color.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
    }

    private static double RelativeLuminance(int red, int green, int blue)
    {
        static double Channel(double value)
            => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        var r = Channel(red / 255d);
        var g = Channel(green / 255d);
        var b = Channel(blue / 255d);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }
}

public sealed record SqlOSAuthPageViewModel(
    string Mode,
    SqlOSAuthPageSettingsDto Settings,
    string BasePath,
    string? AuthorizationRequestId,
    string? Email,
    string? DisplayName,
    string? Error,
    string? Info,
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> OrganizationSelection,
    IReadOnlyList<SqlOSAuthPageProviderLink> Providers);

public sealed record SqlOSAuthPageProviderLink(string ConnectionId, string DisplayName, string Url);
