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

        // Conformance mode: registers the OpenID Foundation conformance suite's
        // static clients so tests/SqlOS.Conformance can run the OIDCC Basic OP
        // and Config OP certification plans against this host. See
        // tests/SqlOS.Conformance/README.md.
        if (string.Equals(builder.Configuration["AppX:Conformance:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var suiteBaseUrl = (builder.Configuration["AppX:Conformance:SuiteBaseUrl"]
                ?? "https://localhost.emobix.co.uk:8443").TrimEnd('/');
            var alias = builder.Configuration["AppX:Conformance:Alias"] ?? "sqlos";
            var callback = $"{suiteBaseUrl}/test/a/{alias}/callback";
            string[] conformanceRedirects = [callback, $"{callback}?dummy1=lorem&dummy2=ipsum"];

            foreach (var (clientId, secretKey, tokenEndpointAuthMethod) in new (string, string, string?)[]
                     {
                         ("conformance-client-one", "AppX:Conformance:ClientOneSecret", null),
                         ("conformance-client-two", "AppX:Conformance:ClientTwoSecret", null),
                         // The Basic certification plan's oidcc-server-client-secret-post
                         // module needs a client registered for body-secret auth.
                         ("conformance-client-post", "AppX:Conformance:ClientPostSecret", "client_secret_post")
                     })
            {
                var secret = builder.Configuration[secretKey]
                    ?? "conformance-local-secret-0123456789abcdef0123456789abcdef";
                auth.SeedClient(client =>
                {
                    client.ClientId = clientId;
                    client.Name = $"OIDF conformance {clientId}";
                    client.Description = "Static client for the OpenID Foundation conformance suite.";
                    client.Audience = "conformance";
                    client.RedirectUris = [.. conformanceRedirects];
                    client.AllowedScopes = ["openid", "profile", "email"];
                    client.ClientType = "confidential";
                    client.TokenEndpointAuthMethod = tokenEndpointAuthMethod;
                    client.RequirePkce = false;
                    // First-party: the certification plans drive dozens of logins;
                    // the consent interstitial is exercised by App Y instead.
                    client.IsFirstParty = true;
                    client.ClientSecretResolver = () => secret;
                });
            }
        }
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
// SqlOS provisions its own tables from SqlOSBootstrapHostedService during
// StartAsync, so the conformance user can only be seeded once the app has
// started — not before app.Run().
await app.StartAsync();
await EnsureConformanceUserAsync(app);
await app.WaitForShutdownAsync();

static async Task EnsureDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppXDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

// The conformance suite's browser automation needs a deterministic account.
static async Task EnsureConformanceUserAsync(WebApplication app)
{
    if (!string.Equals(app.Configuration["AppX:Conformance:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var email = app.Configuration["AppX:Conformance:UserEmail"] ?? "conformance-user@x.test";
    var password = app.Configuration["AppX:Conformance:UserPassword"] ?? "Conformance123!";
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppXDbContext>();
    var exists = await dbContext.Set<SqlOS.AuthServer.Models.SqlOSUserEmail>()
        .AnyAsync(x => x.Email == email);
    if (!exists)
    {
        var admin = scope.ServiceProvider.GetRequiredService<SqlOS.AuthServer.Services.SqlOSAdminService>();
        await admin.CreateUserAsync(new SqlOS.AuthServer.Contracts.SqlOSCreateUserRequest(
            "Conformance User", email, password));
    }
}
