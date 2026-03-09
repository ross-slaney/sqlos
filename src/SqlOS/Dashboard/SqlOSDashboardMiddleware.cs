using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SqlOS.Configuration;

namespace SqlOS.Dashboard;

public sealed class SqlOSDashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _pathPrefix;
    private readonly bool _isDevelopment;
    private readonly SqlOSDashboardOptions _options;

    public SqlOSDashboardMiddleware(
        RequestDelegate next,
        string pathPrefix,
        IHostEnvironment environment,
        SqlOSDashboardOptions options)
    {
        _next = next;
        _pathPrefix = pathPrefix.TrimEnd('/');
        _isDevelopment = environment.IsDevelopment();
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(_pathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!await IsAuthorizedAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var relativePath = path[_pathPrefix.Length..].Trim('/');
        if (!string.IsNullOrEmpty(relativePath))
        {
            await _next(context);
            return;
        }

        if (!path.EndsWith('/'))
        {
            context.Response.Redirect($"{_pathPrefix}/", permanent: false);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(BuildHtml(_pathPrefix));
    }

    private async Task<bool> IsAuthorizedAsync(HttpContext context)
    {
        if (_options.AuthorizationCallback != null)
        {
            return await _options.AuthorizationCallback(context);
        }

        return _isDevelopment;
    }

    private static string BuildHtml(string prefix)
    {
        var authHref = $"{prefix}/admin/auth/";
        var fgaHref = $"{prefix}/admin/fga/";

        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>SqlOS Dashboard</title>
  <style>
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: "Inter", "Segoe UI", system-ui, sans-serif;
      background: linear-gradient(135deg, #f3f5ef 0%, #e7ebdf 100%);
      color: #1e271b;
    }
    .shell {
      min-height: 100vh;
      padding: 48px 28px;
      display: grid;
      place-items: center;
    }
    .panel {
      width: min(960px, 100%);
      border-radius: 28px;
      overflow: hidden;
      background: rgba(255,255,255,.78);
      border: 1px solid rgba(30,39,27,.08);
      box-shadow: 0 28px 80px rgba(30,39,27,.12);
      backdrop-filter: blur(18px);
    }
    .hero {
      padding: 36px;
      background: linear-gradient(120deg, #172115 0%, #24361d 55%, #3b4f2f 100%);
      color: #f7f8f3;
    }
    .hero h1 {
      margin: 0 0 8px;
      font-size: clamp(2.25rem, 5vw, 4.25rem);
      line-height: .94;
      letter-spacing: -.06em;
    }
    .hero p {
      margin: 0;
      max-width: 38rem;
      font-size: 1rem;
      line-height: 1.5;
      color: rgba(247,248,243,.84);
    }
    .grid {
      display: grid;
      gap: 18px;
      padding: 28px;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    }
    .card {
      display: block;
      text-decoration: none;
      color: inherit;
      border-radius: 22px;
      padding: 22px;
      background: #fbfcf8;
      border: 1px solid rgba(30,39,27,.08);
      transition: transform .15s ease, box-shadow .15s ease, border-color .15s ease;
    }
    .card:hover {
      transform: translateY(-2px);
      box-shadow: 0 14px 28px rgba(30,39,27,.09);
      border-color: rgba(59,79,47,.28);
    }
    .eyebrow {
      margin: 0 0 10px;
      font-size: .76rem;
      font-weight: 700;
      letter-spacing: .08em;
      text-transform: uppercase;
      color: #6f7d63;
    }
    .card h2 {
      margin: 0 0 8px;
      font-size: 1.35rem;
      line-height: 1.05;
    }
    .card p {
      margin: 0;
      color: #516048;
      line-height: 1.55;
    }
  </style>
</head>
<body>
  <main class="shell">
    <section class="panel">
      <header class="hero">
        <h1>SqlOS</h1>
        <p>One embedded runtime for authentication, sessions, organizations, SSO, and fine-grained authorization. Choose a module dashboard to administer the shared system.</p>
      </header>
      <div class="grid">
        <a class="card" href="{{WebUtility.HtmlEncode(authHref)}}">
          <div class="eyebrow">Auth Server</div>
          <h2>Users, orgs, sessions, SSO</h2>
          <p>Manage organizations, users, memberships, clients, SAML connections, security settings, sessions, and audit events.</p>
        </a>
        <a class="card" href="{{WebUtility.HtmlEncode(fgaHref)}}">
          <div class="eyebrow">FGA</div>
          <h2>Resources, grants, roles</h2>
          <p>Inspect the authorization graph, grant roles, trace resource access, and maintain the FGA schema and permission model.</p>
        </a>
      </div>
    </section>
  </main>
</body>
</html>
""";
    }
}

