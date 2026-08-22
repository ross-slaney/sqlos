using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.SignInWithX.AppX;

// App X: the identity side of "Sign in with X". This host does nothing except
// run SqlOS as an OpenID Provider — hosted sign-in/sign-up pages, OIDC
// discovery, ID tokens, UserInfo, and the consent screen for App Y. App Y
// (examples/SqlOS.SignInWithX.AppY) is a Next.js app that federates against it
// with Auth.js using nothing but the discovery document.
var builder = WebApplication.CreateBuilder(args);

var publicOrigin = builder.Configuration["AppX:PublicOrigin"] ?? "http://localhost:5100";
var appYOrigin = builder.Configuration["AppX:AppYOrigin"] ?? "http://localhost:3020";
var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost,1436;Database=sqlos-appx;User Id=sa;Password=LocalDevPassword123!;TrustServerCertificate=True";

builder.AddSqlOS<AppXDbContext>(
    db => db.UseSqlServer(connectionString),
    options =>
    {
        options.DashboardBasePath = "/sqlos";
        var auth = options.AuthServer;
        auth.Issuer = $"{publicOrigin}/sqlos/auth";
        auth.PublicOrigin = publicOrigin;

        auth.SeedAuthPage(page =>
        {
            page.PageTitle = "Sign in with X";
            page.PageSubtitle = "One X account signs you in to every app that trusts X.";
            page.PrimaryColor = "#111827";
            page.AccentColor = "#7c3aed";
            page.BackgroundColor = "#faf5ff";
            page.Layout = "split";
            page.EnablePasswordSignup = true;
            page.EnabledCredentialTypes = ["password"];
        });

        auth.SeedAuthEmails(email =>
        {
            email.ApplicationName = "X";
            email.PrimaryColor = "#111827";
            email.AccentColor = "#7c3aed";
            email.BackgroundColor = "#faf5ff";
        });

        // App Y is deliberately NOT first-party: the first sign-in shows X's
        // consent screen listing the scope display names below, and the
        // remembered grant makes later sign-ins silent.
        auth.SeedClient(client =>
        {
            client.ClientId = "app-y";
            client.Name = "App Y";
            client.Description = "Next.js + Auth.js relying party that signs users in with X over standard OIDC.";
            client.Audience = "app-y";
            client.RedirectUris = [$"{appYOrigin}/api/auth/callback/sqlos"];
            client.AllowedScopes = ["openid", "profile", "email"];
            client.ClientType = "public_pkce";
            client.RequirePkce = true;
            client.IsFirstParty = false;
        });

        // Human-readable scope names for the consent screen.
        auth.SeedScopeDisplayName("openid", "Sign you in", "Confirm your identity to the app with an ID token.");
        auth.SeedScopeDisplayName("profile", "See your name", "Share your display name and username.");
        auth.SeedScopeDisplayName("email", "See your email address", "Share your email address and whether it is verified.");
    });

var app = builder.Build();

app.MapSqlOS();

app.MapGet("/", (HttpContext http) => Results.Content($$"""
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>X — identity provider</title></head>
    <body style="font-family: system-ui; max-width: 40rem; margin: 4rem auto; line-height: 1.6;">
      <h1>This is X</h1>
      <p>X is a SqlOS host running in OpenID Provider mode. Other apps add a
         <strong>"Sign in with X"</strong> button by pointing any standard OIDC
         library at X's discovery document — no SqlOS SDK required on their side.</p>
      <ul>
        <li><a href="/sqlos/auth/.well-known/openid-configuration">OIDC discovery document</a></li>
        <li><a href="/sqlos/auth/.well-known/jwks.json">JWKS</a></li>
        <li><a href="{{appYOrigin}}">App Y (the relying party)</a></li>
        <li><a href="/sqlos">SqlOS dashboard</a></li>
      </ul>
    </body>
    </html>
    """, "text/html"));

await EnsureDatabaseAsync(app);
app.Run();

static async Task EnsureDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppXDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}
