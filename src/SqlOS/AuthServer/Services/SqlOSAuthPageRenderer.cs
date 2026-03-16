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
        var logoMarkup = string.IsNullOrWhiteSpace(model.Settings.LogoBase64)
            ? "<div class=\"logo-fallback\">SqlOS</div>"
            : $"<img class=\"logo\" src=\"{Html(model.Settings.LogoBase64)}\" alt=\"Logo\" />";
        var requestIdInput = string.IsNullOrWhiteSpace(model.AuthorizationRequestId)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"requestId\" value=\"{Html(model.AuthorizationRequestId)}\" />";
        var emailValue = Html(model.Email ?? string.Empty);
        var encodedMode = Html(normalizedMode);
        var signupLink = model.Settings.EnablePasswordSignup
            ? $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/signup", model.AuthorizationRequestId))}\">Create an account</a>"
            : string.Empty;
        var loginLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/login", model.AuthorizationRequestId))}\">Back to sign in</a>";
        var signInAgainLink = $"<a class=\"secondary-link\" href=\"{Html(AuthPath(model, "/login"))}\">Sign in again</a>";
        var errorMarkup = BuildCallout("error", model.Error);
        var infoMarkup = BuildCallout("info", model.Info);
        var secondaryPanel = RenderSecondaryPanel(model, normalizedMode, subtitle, isStackedLayout);

        var content = normalizedMode switch
        {
            "signup" => $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/signup/submit"))}}" data-overlay-title="Creating your account" data-overlay-copy="Setting up your profile and credentials.">
                  {{requestIdInput}}
                  <input type="hidden" name="mode" value="{{encodedMode}}" />
                  <label class="field">
                    <span>Display name</span>
                    <input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" placeholder="Jane Doe" autocomplete="name" required />
                  </label>
                  <label class="field">
                    <span>Email address</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="name@company.com" autocomplete="email" required />
                  </label>
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Create a secure password" autocomplete="new-password" required />
                  </label>
                  <label class="field">
                    <span>Organization name</span>
                    <input name="organizationName" placeholder="Optional for a new workspace" autocomplete="organization" />
                  </label>
                  {{RenderPrimaryAction("Create account", "Creating account")}}
                </form>
                {{RenderFooterLinks(loginLink)}}
                """,
            "password" => $$"""
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/password"))}}" data-overlay-title="Signing you in" data-overlay-copy="Verifying your credentials.">
                  {{requestIdInput}}
                  <label class="field">
                    <span>Email address</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="name@company.com" autocomplete="email" required />
                  </label>
                  <label class="field">
                    <span>Password</span>
                    <input name="password" type="password" placeholder="Enter your password" autocomplete="current-password" required />
                  </label>
                  {{RenderPrimaryAction("Continue", "Signing in")}}
                </form>
                {{RenderProvidersSection(model)}}
                {{RenderFooterLinks(signupLink)}}
                """,
            "organization" => $$"""
                <form class="auth-form organization-form" method="post" action="{{Html(AuthPath(model, "/login/select-organization"))}}" data-overlay-title="Opening your workspace" data-overlay-copy="Finishing sign-in for the selected organization.">
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
                <form class="auth-form" method="post" action="{{Html(AuthPath(model, "/login/identify"))}}" data-flow-kind="hrd" data-overlay-title="Checking your workspace" data-overlay-copy="Looking up the best sign-in method for your domain.">
                  {{requestIdInput}}
                  <label class="field">
                    <span>Work email</span>
                    <input name="email" type="email" value="{{emailValue}}" placeholder="name@company.com" autocomplete="email" required />
                  </label>
                  {{RenderPrimaryAction("Continue", "Checking workspace")}}
                  <div class="flow-status" hidden>
                    <span class="loader loader-sm" aria-hidden="true"></span>
                    <span>Looking up your workspace and sign-in method...</span>
                  </div>
                </form>
                <p class="helper-copy">Home realm discovery checks whether your domain should continue with password or SSO.</p>
                {{RenderFooterLinks(signupLink)}}
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
              --primary: {{Css(model.Settings.PrimaryColor, "#2563eb")}};
              --accent: {{Css(model.Settings.AccentColor, "#111827")}};
              --background: {{Css(model.Settings.BackgroundColor, "#f8fafc")}};
              --text: color-mix(in srgb, var(--accent) 90%, #111827);
              --muted: color-mix(in srgb, var(--accent) 46%, #6b7280);
              --border: color-mix(in srgb, var(--accent) 14%, #e5e7eb);
              --border-strong: color-mix(in srgb, var(--accent) 20%, #d1d5db);
              --ring: color-mix(in srgb, var(--primary) 24%, white);
              --card: rgba(255, 255, 255, 0.92);
              --shadow: 0 18px 48px rgba(15, 23, 42, 0.08);
            }
            * { box-sizing: border-box; }
            html, body { min-height: 100%; }
            body {
              margin: 0;
              font-family: Inter, "Segoe UI", system-ui, sans-serif;
              color: var(--text);
              background:
                radial-gradient(circle at top, color-mix(in srgb, var(--primary) 10%, white) 0%, transparent 34%),
                linear-gradient(180deg, color-mix(in srgb, var(--background) 94%, white) 0%, var(--background) 100%);
            }
            body::before {
              content: "";
              position: fixed;
              inset: 0;
              pointer-events: none;
              background:
                linear-gradient(color-mix(in srgb, var(--accent) 3%, transparent) 1px, transparent 1px),
                linear-gradient(90deg, color-mix(in srgb, var(--accent) 3%, transparent) 1px, transparent 1px);
              background-size: 36px 36px;
              opacity: 0.26;
            }
            h1, h2, p, strong, small, span { margin: 0; }
            input, button { font: inherit; }
            [hidden] { display: none !important; }
            .page-shell {
              position: relative;
              min-height: 100vh;
              padding: 28px 16px;
              display: grid;
              place-items: center;
              overflow: hidden;
            }
            .page-glow {
              position: absolute;
              top: 10%;
              left: 50%;
              width: min(560px, 88vw);
              height: 320px;
              transform: translateX(-50%);
              border-radius: 999px;
              background: color-mix(in srgb, var(--primary) 18%, white);
              filter: blur(56px);
              opacity: 0.42;
              pointer-events: none;
            }
            .auth-shell {
              position: relative;
              width: min(960px, 100%);
              display: grid;
              gap: 24px;
              align-items: center;
              justify-content: center;
            }
            .auth-shell.split { grid-template-columns: minmax(0, 440px) minmax(0, 340px); }
            .auth-shell.stacked { max-width: 440px; }
            .auth-card,
            .side-card {
              background: var(--card);
              border: 1px solid var(--border);
              border-radius: 24px;
              box-shadow: var(--shadow);
              backdrop-filter: blur(10px);
            }
            .auth-card {
              display: grid;
              gap: 20px;
              padding: 24px;
              animation: fade-up 0.34s ease-out both;
            }
            .brand-row {
              display: flex;
              align-items: center;
              gap: 14px;
            }
            .logo-shell {
              width: 52px;
              height: 52px;
              border-radius: 16px;
              border: 1px solid color-mix(in srgb, var(--primary) 16%, white);
              background: color-mix(in srgb, var(--primary) 8%, white);
              display: grid;
              place-items: center;
              overflow: hidden;
              flex: none;
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
              background: linear-gradient(145deg, color-mix(in srgb, var(--accent) 92%, black), color-mix(in srgb, var(--accent) 78%, var(--primary)));
              color: white;
              font-size: 12px;
              font-weight: 700;
              letter-spacing: 0.04em;
            }
            .brand-copy {
              display: grid;
              gap: 4px;
              min-width: 0;
            }
            h1 {
              font-size: 28px;
              line-height: 1.1;
              letter-spacing: -0.04em;
            }
            .brand-subtitle {
              color: var(--muted);
              line-height: 1.5;
              font-size: 14px;
            }
            .view-card {
              display: grid;
              gap: 18px;
              padding: 20px;
              border-radius: 20px;
              border: 1px solid var(--border);
              background: rgba(255, 255, 255, 0.8);
            }
            .view-copy {
              display: grid;
              gap: 8px;
            }
            .view-label {
              display: inline-flex;
              align-items: center;
              width: fit-content;
              min-height: 26px;
              padding: 0 10px;
              border-radius: 999px;
              border: 1px solid color-mix(in srgb, var(--primary) 16%, white);
              background: color-mix(in srgb, var(--primary) 10%, white);
              color: color-mix(in srgb, var(--primary) 56%, var(--accent));
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.08em;
              text-transform: uppercase;
            }
            .view-copy h2 {
              font-size: 22px;
              line-height: 1.15;
              letter-spacing: -0.04em;
            }
            .view-copy p {
              color: var(--muted);
              line-height: 1.55;
              font-size: 14px;
            }
            .auth-form,
            .organization-list {
              display: grid;
              gap: 14px;
            }
            .field {
              display: grid;
              gap: 8px;
            }
            .field span {
              color: var(--text);
              font-size: 13px;
              font-weight: 600;
            }
            input {
              width: 100%;
              height: 48px;
              padding: 0 14px;
              border-radius: 12px;
              border: 1px solid var(--border);
              background: white;
              color: var(--text);
              transition: border-color 0.18s ease, box-shadow 0.18s ease;
            }
            input::placeholder { color: color-mix(in srgb, var(--accent) 24%, #94a3b8); }
            input:focus {
              outline: none;
              border-color: color-mix(in srgb, var(--primary) 42%, white);
              box-shadow: 0 0 0 4px var(--ring);
            }
            .primary-action,
            .provider-link,
            .organization-option,
            .secondary-link {
              transition:
                transform 0.18s ease,
                border-color 0.18s ease,
                box-shadow 0.18s ease,
                background 0.18s ease,
                color 0.18s ease;
            }
            .primary-action {
              appearance: none;
              width: 100%;
              height: 46px;
              border: none;
              border-radius: 12px;
              background: linear-gradient(180deg, color-mix(in srgb, var(--accent) 96%, black), color-mix(in srgb, var(--accent) 82%, var(--primary)));
              color: white;
              display: inline-flex;
              align-items: center;
              justify-content: center;
              gap: 10px;
              font-size: 14px;
              font-weight: 600;
              cursor: pointer;
              box-shadow: 0 10px 24px rgba(15, 23, 42, 0.12);
            }
            .primary-action:hover,
            .provider-link:hover,
            .organization-option:hover {
              transform: translateY(-1px);
              box-shadow: 0 14px 28px rgba(15, 23, 42, 0.12);
            }
            .primary-action:focus-visible,
            .provider-link:focus-visible,
            .organization-option:focus-visible,
            .secondary-link:focus-visible {
              outline: 3px solid var(--ring);
              outline-offset: 3px;
            }
            .button-spinner {
              opacity: 0;
            }
            .primary-action[data-loading="true"] .button-spinner,
            .organization-option[data-loading="true"] .organization-spinner {
              opacity: 1;
            }
            .section-divider {
              display: flex;
              align-items: center;
              gap: 12px;
              color: var(--muted);
              font-size: 13px;
              line-height: 1;
            }
            .section-divider::before,
            .section-divider::after {
              content: "";
              flex: 1;
              height: 1px;
              background: var(--border);
            }
            .providers {
              display: grid;
              gap: 10px;
            }
            .provider-link,
            .organization-option {
              width: 100%;
              min-height: 48px;
              padding: 12px 14px;
              border-radius: 12px;
              border: 1px solid var(--border);
              background: white;
              color: var(--text);
              display: flex;
              align-items: center;
              justify-content: space-between;
              gap: 12px;
              text-decoration: none;
              box-shadow: 0 4px 14px rgba(15, 23, 42, 0.04);
            }
            .provider-main,
            .organization-main {
              display: flex;
              align-items: center;
              gap: 12px;
              min-width: 0;
            }
            .provider-icon {
              width: 28px;
              height: 28px;
              border-radius: 8px;
              border: 1px solid color-mix(in srgb, var(--primary) 16%, white);
              background: color-mix(in srgb, var(--primary) 10%, white);
              color: color-mix(in srgb, var(--primary) 58%, var(--accent));
              display: grid;
              place-items: center;
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.04em;
              flex: none;
            }
            .provider-label,
            .organization-main strong {
              font-size: 14px;
              font-weight: 600;
              line-height: 1.35;
            }
            .provider-arrow,
            .organization-arrow {
              width: 9px;
              height: 9px;
              border-top: 1.5px solid color-mix(in srgb, var(--accent) 34%, #6b7280);
              border-right: 1.5px solid color-mix(in srgb, var(--accent) 34%, #6b7280);
              transform: rotate(45deg);
              flex: none;
            }
            .organization-option {
              appearance: none;
              cursor: pointer;
              text-align: left;
            }
            .organization-main {
              flex: 1;
              min-width: 0;
            }
            .organization-copy {
              display: grid;
              gap: 3px;
            }
            .organization-copy small {
              color: var(--muted);
              font-size: 12px;
              line-height: 1.4;
            }
            .organization-meta {
              display: inline-flex;
              align-items: center;
              gap: 12px;
              flex: none;
            }
            .organization-role {
              display: inline-flex;
              align-items: center;
              min-height: 24px;
              padding: 0 8px;
              border-radius: 999px;
              border: 1px solid color-mix(in srgb, var(--primary) 16%, white);
              background: color-mix(in srgb, var(--primary) 10%, white);
              color: color-mix(in srgb, var(--primary) 56%, var(--accent));
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.05em;
              text-transform: uppercase;
            }
            .organization-spinner {
              opacity: 0;
              color: color-mix(in srgb, var(--primary) 56%, var(--accent));
            }
            .helper-copy {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            .flow-status {
              display: flex;
              align-items: center;
              gap: 8px;
              color: var(--muted);
              font-size: 13px;
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
              animation: spin 0.7s linear infinite;
              flex: none;
            }
            .loader-sm {
              width: 14px;
              height: 14px;
            }
            .loader-lg {
              width: 20px;
              height: 20px;
            }
            .callout {
              display: flex;
              align-items: flex-start;
              gap: 10px;
              padding: 12px 14px;
              border-radius: 14px;
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
            .footer-links {
              display: flex;
              align-items: center;
              flex-wrap: wrap;
              gap: 12px;
              min-height: 20px;
            }
            .secondary-link {
              color: color-mix(in srgb, var(--primary) 62%, var(--accent));
              font-size: 13px;
              font-weight: 600;
              text-decoration: none;
            }
            .state-card {
              display: flex;
              align-items: center;
              gap: 12px;
              padding: 14px;
              border-radius: 14px;
              border: 1px solid var(--border);
              background: white;
            }
            .state-icon {
              width: 34px;
              height: 34px;
              border-radius: 10px;
              border: 1px solid color-mix(in srgb, var(--primary) 16%, white);
              background: color-mix(in srgb, var(--primary) 10%, white);
              color: color-mix(in srgb, var(--primary) 56%, var(--accent));
              display: grid;
              place-items: center;
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.08em;
              flex: none;
            }
            .state-copy {
              display: grid;
              gap: 4px;
            }
            .state-copy strong {
              font-size: 14px;
              font-weight: 600;
            }
            .state-copy p {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            .side-card {
              display: grid;
              gap: 16px;
              padding: 22px;
              animation: fade-up 0.34s ease-out 0.06s both;
            }
            .side-eyebrow {
              display: inline-flex;
              align-items: center;
              width: fit-content;
              min-height: 24px;
              padding: 0 10px;
              border-radius: 999px;
              border: 1px solid var(--border);
              background: white;
              color: var(--muted);
              font-size: 11px;
              font-weight: 700;
              letter-spacing: 0.08em;
              text-transform: uppercase;
            }
            .side-copy {
              display: grid;
              gap: 8px;
            }
            .side-copy h2 {
              font-size: 24px;
              line-height: 1.12;
              letter-spacing: -0.04em;
            }
            .side-copy p {
              color: var(--muted);
              font-size: 14px;
              line-height: 1.6;
            }
            .side-list {
              display: grid;
              gap: 10px;
            }
            .side-row {
              padding: 14px;
              border-radius: 14px;
              border: 1px solid var(--border);
              background: white;
              display: grid;
              gap: 4px;
            }
            .side-row strong {
              font-size: 13px;
              font-weight: 600;
            }
            .side-row span {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.55;
            }
            .loading-overlay {
              position: fixed;
              inset: 0;
              z-index: 20;
              display: grid;
              place-items: center;
              padding: 16px;
              background: rgba(15, 23, 42, 0.14);
              backdrop-filter: blur(4px);
              opacity: 0;
              transition: opacity 0.18s ease;
            }
            .loading-overlay[data-visible="true"] {
              opacity: 1;
            }
            .loading-card {
              width: min(320px, 100%);
              display: flex;
              align-items: center;
              gap: 12px;
              padding: 16px;
              border-radius: 16px;
              border: 1px solid var(--border);
              background: rgba(255, 255, 255, 0.96);
              box-shadow: 0 18px 44px rgba(15, 23, 42, 0.14);
            }
            .loading-copy {
              display: grid;
              gap: 4px;
            }
            .loading-copy strong {
              font-size: 14px;
              font-weight: 600;
            }
            .loading-copy p {
              color: var(--muted);
              font-size: 13px;
              line-height: 1.5;
            }
            @keyframes spin {
              from { transform: rotate(0deg); }
              to { transform: rotate(360deg); }
            }
            @keyframes fade-up {
              from {
                opacity: 0;
                transform: translateY(12px);
              }
              to {
                opacity: 1;
                transform: translateY(0);
              }
            }
            @media (max-width: 920px) {
              .auth-shell.split { grid-template-columns: 1fr; max-width: 440px; }
              .side-card { display: none; }
            }
            @media (max-width: 640px) {
              .page-shell { padding: 20px 12px; }
              .auth-card { padding: 18px; }
              .view-card { padding: 16px; }
              h1 { font-size: 24px; }
              .view-copy h2 { font-size: 20px; }
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
            <div class="page-glow"></div>
            <main class="auth-shell {{(isStackedLayout ? "stacked" : "split")}}">
              <section class="auth-card">
                <div class="brand-row">
                  <div class="logo-shell">{{logoMarkup}}</div>
                  <div class="brand-copy">
                    <h1>{{Html(title)}}</h1>
                    {{subtitleMarkup}}
                  </div>
                </div>
                {{errorMarkup}}
                {{infoMarkup}}
                <section class="view-card">
                  <div class="view-copy">
                    <span class="view-label">{{Html(GetModeLabel(normalizedMode))}}</span>
                    <h2>{{Html(GetStepTitle(normalizedMode))}}</h2>
                    <p>{{Html(GetStepDescription(model, normalizedMode))}}</p>
                  </div>
                  {{content}}
                </section>
              </section>
              {{secondaryPanel}}
            </main>
            {{RenderLoadingOverlay()}}
          </div>
          <script>{{RenderClientScript()}}</script>
        </body>
        </html>
        """;
    }

    private static string RenderPrimaryAction(string label, string loadingLabel)
        => $$"""
            <button class="primary-action" type="submit" data-loading-label="{{Html(loadingLabel)}}">
              <span class="loader loader-sm button-spinner" aria-hidden="true"></span>
              <span class="button-label">{{Html(label)}}</span>
            </button>
            """;

    private static string RenderProvidersSection(SqlOSAuthPageViewModel model)
    {
        if (model.Providers.Count == 0)
        {
            return string.Empty;
        }

        var providersMarkup = string.Join("", model.Providers.Select(RenderProviderLink));
        return $$"""
            <div class="section-divider">Or continue with</div>
            <div class="providers">{{providersMarkup}}</div>
            """;
    }

    private static string RenderProviderLink(SqlOSAuthPageProviderLink provider)
    {
        var providerName = Html(provider.DisplayName);
        return $$"""
            <a class="provider-link js-loading-link" href="{{Html(provider.Url)}}" data-loading-title="Redirecting to {{providerName}}" data-loading-copy="Handing off to {{providerName}} for authentication.">
              <span class="provider-main">
                <span class="provider-icon">{{Html(GetMonogram(provider.DisplayName))}}</span>
                <span class="provider-label">Continue with {{providerName}}</span>
              </span>
              <span class="provider-arrow" aria-hidden="true"></span>
            </a>
            """;
    }

    private static string RenderOrganizationOptions(IReadOnlyList<SqlOSOrganizationOption> organizations)
    {
        if (organizations.Count == 0)
        {
            return "<p class=\"helper-copy\">No organizations were available for this sign-in attempt. Return to sign in and try again.</p>";
        }

        return string.Join("", organizations.Select(option =>
        {
            var subtitle = string.IsNullOrWhiteSpace(option.Slug) ? "Workspace access" : option.Slug;
            return $$"""
                <button class="organization-option" type="submit" name="organizationId" value="{{Html(option.Id)}}" data-loading-label="Opening workspace">
                  <span class="organization-main">
                    <span class="organization-copy">
                      <strong>{{Html(option.Name)}}</strong>
                      <small>{{Html(subtitle)}}</small>
                    </span>
                  </span>
                  <span class="organization-meta">
                    <span class="loader loader-sm organization-spinner" aria-hidden="true"></span>
                    <span class="organization-role">{{Html(option.Role)}}</span>
                    <span class="organization-arrow" aria-hidden="true"></span>
                  </span>
                </button>
                """;
        }));
    }

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

    private static string RenderSecondaryPanel(SqlOSAuthPageViewModel model, string normalizedMode, string subtitle, bool isStackedLayout)
    {
        if (isStackedLayout)
        {
            return string.Empty;
        }

        var panelCopy = string.IsNullOrWhiteSpace(subtitle)
            ? GetStepDescription(model, normalizedMode)
            : subtitle;
        var providersSummary = model.Providers.Count == 0
            ? "Keep password-first sign-in simple while still routing configured domains to SSO."
            : $"{CountLabel(model.Providers.Count, "connected provider")} can appear alongside password without changing the flow.";
        var signupSummary = model.Settings.EnablePasswordSignup
            ? "Password signup is enabled, so new users can move through the same hosted surface."
            : "Direct password signup is hidden, which keeps onboarding invitation-led or admin-managed.";

        return $$"""
            <aside class="side-panel side-card">
              <span class="side-eyebrow">Hosted Auth</span>
              <div class="side-copy">
                <h2>{{Html(GetPanelTitle(normalizedMode))}}</h2>
                <p>{{Html(panelCopy)}}</p>
              </div>
              <div class="side-list">
                <div class="side-row">
                  <strong>Home realm discovery</strong>
                  <span>Work domains can route to SSO before the password step, with lightweight loading feedback while that happens.</span>
                </div>
                <div class="side-row">
                  <strong>Identity options</strong>
                  <span>{{Html(providersSummary)}}</span>
                </div>
                <div class="side-row">
                  <strong>Signup behavior</strong>
                  <span>{{Html(signupSummary)}}</span>
                </div>
              </div>
            </aside>
            """;
    }

    private static string RenderLoadingOverlay()
        => """
            <div class="loading-overlay" id="auth-loading-overlay" hidden aria-hidden="true">
              <div class="loading-card">
                <span class="loader loader-lg" aria-hidden="true"></span>
                <div class="loading-copy">
                  <strong id="auth-loading-title">Working...</strong>
                  <p id="auth-loading-copy">Securing your sign-in.</p>
                </div>
              </div>
            </div>
            """;

    private static string RenderClientScript()
        => """
            (() => {
              const overlay = document.getElementById("auth-loading-overlay");
              const overlayTitle = document.getElementById("auth-loading-title");
              const overlayCopy = document.getElementById("auth-loading-copy");
              let overlayTimer = 0;
              let activeSubmitter = null;

              if (!overlay || !overlayTitle || !overlayCopy) {
                return;
              }

              const showOverlay = (title, copy, delay) => {
                window.clearTimeout(overlayTimer);
                overlayTimer = window.setTimeout(() => {
                  overlayTitle.textContent = title || "Working...";
                  overlayCopy.textContent = copy || "Securing your sign-in.";
                  overlay.hidden = false;
                  overlay.setAttribute("aria-hidden", "false");
                  window.requestAnimationFrame(() => {
                    overlay.dataset.visible = "true";
                  });
                }, delay || 0);
              };

              document.addEventListener("click", event => {
                const submitter = event.target.closest('button[type="submit"]');
                if (submitter) {
                  activeSubmitter = submitter;
                  return;
                }

                const loadingLink = event.target.closest("a.js-loading-link");
                if (!loadingLink) {
                  return;
                }

                if (loadingLink.dataset.loading === "true") {
                  event.preventDefault();
                  return;
                }

                loadingLink.dataset.loading = "true";
                showOverlay(
                  loadingLink.dataset.loadingTitle || "Redirecting",
                  loadingLink.dataset.loadingCopy || "Handing off to your identity provider.",
                  40
                );
              });

              document.querySelectorAll(".auth-form").forEach(form => {
                form.addEventListener("submit", event => {
                  if (form.dataset.loading === "true") {
                    event.preventDefault();
                    return;
                  }

                  const submitter = event.submitter || activeSubmitter || form.querySelector('button[type="submit"]');
                  form.dataset.loading = "true";

                  if (submitter) {
                    submitter.dataset.loading = "true";
                    submitter.setAttribute("aria-disabled", "true");
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

                  const delay = form.dataset.flowKind === "hrd" ? 220 : 120;
                  showOverlay(
                    form.dataset.overlayTitle || "Working...",
                    form.dataset.overlayCopy || "Securing your sign-in.",
                    delay
                  );
                });
              });
            })();
            """;

    private static string GetModeLabel(string normalizedMode)
        => normalizedMode switch
        {
            "signup" => "Create account",
            "password" => "Password",
            "organization" => "Workspace",
            "logged-out" => "Signed out",
            _ => "Continue"
        };

    private static string GetStepTitle(string normalizedMode)
        => normalizedMode switch
        {
            "signup" => "Create your account",
            "password" => "Enter your password",
            "organization" => "Choose an organization",
            "logged-out" => "Session ended",
            _ => "Sign in with your work email"
        };

    private static string GetStepDescription(SqlOSAuthPageViewModel model, string normalizedMode)
        => normalizedMode switch
        {
            "signup" => "Set your profile and password to get started. Add an organization name if you are creating a new workspace.",
            "password" when model.Providers.Count > 0 => "Use your password to continue, or choose another connected identity provider below.",
            "password" => "Use the password attached to your work email to continue securely.",
            "organization" when model.OrganizationSelection.Count == 0 => "We could not find an organization for this sign-in attempt. Return to sign in and try again.",
            "organization" => $"We found {CountLabel(model.OrganizationSelection.Count, "workspace")} linked to this account. Choose where you want to continue.",
            "logged-out" => "You have been signed out of the current session.",
            _ => "Start with your email and we will determine whether you should continue with password or SSO."
        };

    private static string GetPanelTitle(string normalizedMode)
        => normalizedMode switch
        {
            "signup" => "Simple account creation, same hosted surface",
            "password" => "A quieter password step with room for social sign-in",
            "organization" => "Workspace selection stays in the same flow",
            "logged-out" => "The end of the session should feel as polished as the start",
            _ => "Closer to an AuthKit-style sign-in surface"
        };

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

    private static string CountLabel(int count, string singular)
        => count == 1 ? $"1 {singular}" : $"{count} {singular}s";

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

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string Css(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
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
