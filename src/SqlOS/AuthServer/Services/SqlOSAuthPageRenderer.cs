using System.Net;
using System.Text;
using SqlOS.AuthServer.Contracts;

namespace SqlOS.AuthServer.Services;

public static class SqlOSAuthPageRenderer
{
    public static string RenderPage(SqlOSAuthPageViewModel model)
    {
        var title = string.IsNullOrWhiteSpace(model.Settings.PageTitle)
            ? "Sign in"
            : model.Settings.PageTitle;
        var subtitle = model.Settings.PageSubtitle ?? string.Empty;
        var logoMarkup = string.IsNullOrWhiteSpace(model.Settings.LogoBase64)
            ? "<div class=\"logo-fallback\">SqlOS</div>"
            : $"<img class=\"logo\" src=\"{Html(model.Settings.LogoBase64)}\" alt=\"Logo\" />";
        var secondaryPanel = model.Settings.Layout == "stacked"
            ? string.Empty
            : $"<aside class=\"side-panel\"><h2>{Html(title)}</h2><p>{Html(subtitle)}</p><p class=\"muted\">SqlOS AuthPage gives your app a standards-based authorization surface without handing users to a third-party hosted login product.</p></aside>";

        var providersMarkup = model.Providers.Count == 0
            ? "<p class=\"muted\">No OIDC providers are enabled yet.</p>"
            : string.Join("", model.Providers.Select(provider =>
                $"<a class=\"provider-link\" href=\"{Html(provider.Url)}\">Continue with {Html(provider.DisplayName)}</a>"));

        var organizationOptions = model.OrganizationSelection.Count == 0
            ? string.Empty
            : string.Join("", model.OrganizationSelection.Select(option =>
                $"<button type=\"submit\" name=\"organizationId\" value=\"{Html(option.Id)}\">{Html(option.Name)} <span>{Html(option.Role)}</span></button>"));

        var requestIdInput = string.IsNullOrWhiteSpace(model.AuthorizationRequestId)
            ? string.Empty
            : $"<input type=\"hidden\" name=\"requestId\" value=\"{Html(model.AuthorizationRequestId)}\" />";

        var emailValue = Html(model.Email ?? string.Empty);
        var encodedNextMode = Html(model.Mode);
        var errorMarkup = string.IsNullOrWhiteSpace(model.Error)
            ? string.Empty
            : $"<div class=\"callout error\">{Html(model.Error)}</div>";
        var infoMarkup = string.IsNullOrWhiteSpace(model.Info)
            ? string.Empty
            : $"<div class=\"callout info\">{Html(model.Info)}</div>";
        var signupLink = model.Settings.EnablePasswordSignup
            ? $"<a class=\"secondary-link\" href=\"signup{BuildRequestQuery(model.AuthorizationRequestId)}\">Create an account</a>"
            : string.Empty;
        var loginLink = $"<a class=\"secondary-link\" href=\"login{BuildRequestQuery(model.AuthorizationRequestId)}\">Back to sign in</a>";

        var content = model.Mode switch
        {
            "signup" => $$"""
                <form method="post" action="signup/submit">
                  {{requestIdInput}}
                  <input type="hidden" name="mode" value="{{encodedNextMode}}" />
                  <label>Display name<input name="displayName" value="{{Html(model.DisplayName ?? string.Empty)}}" required /></label>
                  <label>Email<input name="email" type="email" value="{{emailValue}}" required /></label>
                  <label>Password<input name="password" type="password" required /></label>
                  <label>Organization name<input name="organizationName" placeholder="Optional for direct signups" /></label>
                  <button type="submit">Create account</button>
                </form>
                <div class="footer-links">{{loginLink}}</div>
                """,
            "password" => $$"""
                <form method="post" action="login/password">
                  {{requestIdInput}}
                  <label>Email<input name="email" type="email" value="{{emailValue}}" required /></label>
                  <label>Password<input name="password" type="password" required /></label>
                  <button type="submit">Continue</button>
                </form>
                <div class="divider">Or continue with</div>
                <div class="providers">{{providersMarkup}}</div>
                <div class="footer-links">{{signupLink}}</div>
                """,
            "organization" => $$"""
                <form method="post" action="login/select-organization" class="org-picker">
                  <input type="hidden" name="pendingToken" value="{{Html(model.PendingToken ?? string.Empty)}}" />
                  {{organizationOptions}}
                </form>
                """,
            "logged-out" => $$"""
                <div class="callout info">You are signed out.</div>
                <div class="footer-links"><a class="secondary-link" href="login">Sign in again</a></div>
                """,
            _ => $$"""
                <form method="post" action="login/identify">
                  {{requestIdInput}}
                  <label>Email<input name="email" type="email" value="{{emailValue}}" required /></label>
                  <button type="submit">Continue</button>
                </form>
                <div class="help-text">Enter your work email first. SqlOS uses home realm discovery to route SSO domains to SAML and everyone else to password or OIDC.</div>
                <div class="footer-links">{{signupLink}}</div>
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
              --accent: {{Css(model.Settings.AccentColor, "#0f172a")}};
              --background: {{Css(model.Settings.BackgroundColor, "#f8fafc")}};
            }
            * { box-sizing: border-box; }
            body { margin: 0; font-family: "Inter", system-ui, sans-serif; background: radial-gradient(circle at top, color-mix(in srgb, var(--primary) 10%, white), var(--background)); color: #0f172a; min-height: 100vh; }
            main { min-height: 100vh; display: grid; grid-template-columns: {{(model.Settings.Layout == "stacked" ? "1fr" : "1.1fr 0.9fr")}}; }
            .card { margin: auto; width: min(540px, calc(100vw - 32px)); background: rgba(255,255,255,0.94); border: 1px solid rgba(15,23,42,0.08); border-radius: 24px; box-shadow: 0 20px 60px rgba(15,23,42,0.12); padding: 32px; display: grid; gap: 20px; }
            .brand { display: flex; align-items: center; gap: 16px; }
            .logo { width: 56px; height: 56px; object-fit: contain; border-radius: 16px; }
            .logo-fallback { width: 56px; height: 56px; border-radius: 16px; display: grid; place-items: center; color: white; background: linear-gradient(135deg, var(--primary), var(--accent)); font-weight: 700; }
            h1, h2, p { margin: 0; }
            .subtitle { color: #475569; }
            form { display: grid; gap: 14px; }
            label { display: grid; gap: 8px; color: #334155; font-size: 14px; }
            input { width: 100%; padding: 12px 14px; border: 1px solid #cbd5e1; border-radius: 14px; font: inherit; }
            input:focus { outline: none; border-color: var(--primary); box-shadow: 0 0 0 3px color-mix(in srgb, var(--primary) 18%, white); }
            button, .provider-link { appearance: none; border: none; border-radius: 14px; padding: 13px 16px; font: inherit; font-weight: 600; cursor: pointer; text-align: center; text-decoration: none; }
            button { background: linear-gradient(135deg, var(--accent), color-mix(in srgb, var(--accent) 78%, white)); color: white; }
            .provider-link { background: white; color: var(--accent); border: 1px solid #cbd5e1; }
            .providers, .org-picker { display: grid; gap: 12px; }
            .org-picker button { display: flex; justify-content: space-between; align-items: center; }
            .divider, .help-text, .muted, .footer-links { color: #64748b; font-size: 14px; }
            .divider { text-align: center; }
            .callout { border-radius: 14px; padding: 12px 14px; }
            .callout.error { background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; }
            .callout.info { background: #eff6ff; color: #1d4ed8; border: 1px solid #bfdbfe; }
            .secondary-link { color: var(--primary); text-decoration: none; font-weight: 600; }
            .side-panel { display: grid; align-content: center; padding: 48px; gap: 16px; color: var(--accent); }
            @media (max-width: 900px) {
              main { grid-template-columns: 1fr; }
              .side-panel { display: none; }
            }
          </style>
        </head>
        <body>
          <main>
            <section class="card">
              <div class="brand">
                {{logoMarkup}}
                <div>
                  <h1>{{Html(title)}}</h1>
                  <p class="subtitle">{{Html(subtitle)}}</p>
                </div>
              </div>
              {{errorMarkup}}
              {{infoMarkup}}
              {{content}}
            </section>
            {{secondaryPanel}}
          </main>
        </body>
        </html>
        """;
    }

    private static string BuildRequestQuery(string? requestId)
        => string.IsNullOrWhiteSpace(requestId) ? string.Empty : $"?request={Uri.EscapeDataString(requestId)}";

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string Css(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

public sealed record SqlOSAuthPageViewModel(
    string Mode,
    SqlOSAuthPageSettingsDto Settings,
    string? AuthorizationRequestId,
    string? Email,
    string? DisplayName,
    string? Error,
    string? Info,
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> OrganizationSelection,
    IReadOnlyList<SqlOSAuthPageProviderLink> Providers);

public sealed record SqlOSAuthPageProviderLink(string ConnectionId, string DisplayName, string Url);
