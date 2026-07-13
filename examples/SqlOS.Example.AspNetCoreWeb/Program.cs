using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;

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
    .AddOAuth("SqlOS", options =>
    {
        options.ClientId = clientId;
        // ASP.NET Core's generic OAuth handler requires a non-empty value even
        // for public clients. SqlOS authenticates this client with PKCE and
        // intentionally ignores this placeholder; it is not a client secret.
        options.ClientSecret = "public-pkce-client";
        options.CallbackPath = "/signin-sqlos";
        options.AuthorizationEndpoint = $"{sqlosOrigin}/sqlos/auth/authorize";
        options.TokenEndpoint = $"{sqlosOrigin}/sqlos/auth/token";
        options.UsePkce = true;
        options.SaveTokens = true;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("offline_access");
        options.Scope.Add("todos.read");
        options.Scope.Add("todos.write");

        // Localhost runs over HTTP. Production deployments should use HTTPS and
        // CookieSecurePolicy.Always for both the correlation and session cookies.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                if (string.IsNullOrWhiteSpace(context.AccessToken))
                {
                    throw new InvalidOperationException("SqlOS did not return an access token.");
                }

                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{sqlosOrigin}/api/me");
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", context.AccessToken);

                using var response = await context.Backchannel.SendAsync(
                    request,
                    context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                await using var responseStream =
                    await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
                using var session = await JsonDocument.ParseAsync(
                    responseStream,
                    cancellationToken: context.HttpContext.RequestAborted);

                var identity = context.Identity
                    ?? throw new InvalidOperationException("The OAuth ticket has no identity.");
                var root = session.RootElement;

                AddClaim(identity, ClaimTypes.NameIdentifier, root, "subjectId");
                AddClaim(identity, "org_id", root, "organizationId");
                AddClaim(identity, "client_id", root, "clientId");

                if (root.TryGetProperty("claims", out var claims)
                    && claims.ValueKind == JsonValueKind.Array)
                {
                    foreach (var claim in claims.EnumerateArray())
                    {
                        if (!claim.TryGetProperty("type", out var typeProperty)
                            || !claim.TryGetProperty("value", out var valueProperty))
                        {
                            continue;
                        }

                        var type = typeProperty.GetString();
                        var value = valueProperty.GetString();
                        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        if (!identity.HasClaim(type, value))
                        {
                            identity.AddClaim(new Claim(type, value));
                        }

                        if (type == "email")
                        {
                            AddMappedClaim(identity, ClaimTypes.Email, value);
                            AddMappedClaim(identity, ClaimTypes.Name, value);
                        }
                        else if (type == "sid")
                        {
                            AddMappedClaim(identity, "session_id", value);
                        }
                    }
                }
            },
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

static void AddClaim(
    ClaimsIdentity identity,
    string claimType,
    JsonElement source,
    string propertyName)
{
    if (!source.TryGetProperty(propertyName, out var property)
        || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
        return;
    }

    var value = property.GetString();
    if (!string.IsNullOrWhiteSpace(value) && !identity.HasClaim(claimType, value))
    {
        identity.AddClaim(new Claim(claimType, value));
    }
}

static void AddMappedClaim(ClaimsIdentity identity, string claimType, string value)
{
    if (!identity.HasClaim(claimType, value))
    {
        identity.AddClaim(new Claim(claimType, value));
    }
}
