using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

var sqlosOrigin = (builder.Configuration["SqlOS:Origin"] ?? "http://localhost:5080")
    .TrimEnd('/');
var clientId = builder.Configuration["SqlOS:ClientId"] ?? "example-aspnet";

builder.Services.AddHttpClient("sqlos", client =>
{
    client.BaseAddress = new Uri(sqlosOrigin);
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "SqlOS";
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "sqlos.aspnet.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    })
    .AddOpenIdConnect("SqlOS", options =>
    {
        // SqlOS is an OpenID Connect Provider. The handler reads
        // {Authority}/.well-known/openid-configuration to discover the
        // authorization, token, JWKS, and UserInfo endpoints.
        options.Authority = $"{sqlosOrigin}/sqlos/auth";
        options.ClientId = clientId;

        // example-aspnet is a public PKCE client (token_endpoint_auth_method
        // "none"): there is no client secret, and the code exchange is bound
        // by the PKCE verifier instead.
        options.ResponseType = "code";
        options.UsePkce = true;

        // SqlOS returns the authorization code as a standard query redirect.
        options.ResponseMode = "query";

        // The seeded client registers this exact redirect URI.
        options.CallbackPath = "/signin-sqlos";

        options.SaveTokens = true;

        // The ID token carries sub/sid/amr/org_id plus profile and email
        // claims; UserInfo supplements them (name, preferred_username,
        // updated_at, email_verified).
        options.GetClaimsFromUserInfoEndpoint = true;

        // Keep the raw OIDC claim names (sub, email, name, sid) instead of
        // letting the JWT handler rename them to legacy SOAP ClaimTypes URIs.
        // Everything in this example reads the raw names.
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";

        // Localhost runs over HTTP. Production deployments must use HTTPS
        // and leave this at its secure default of true.
        options.RequireHttpsMetadata = false;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("offline_access");

        // Localhost runs over HTTP. Production deployments should use HTTPS and
        // CookieSecurePolicy.Always for the correlation, nonce, and session cookies.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        options.Events = new OpenIdConnectEvents
        {
            OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("SqlOS.Example.AspNetCoreWeb.Authentication");
                logger.LogWarning(
                    context.Failure,
                    "SqlOS sign-in failed for trace {TraceIdentifier}.",
                    context.HttpContext.TraceIdentifier);
                context.HandleResponse();
                var message = Uri.EscapeDataString(
                    "SqlOS sign-in could not be completed. Start a new sign-in and try again.");
                context.Response.Redirect($"/?error={message}");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddRazorPages();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
