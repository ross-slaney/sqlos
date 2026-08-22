using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Services;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>
/// Reusable hosted OAuth fixture: GET /authorize → login form POST → code redirect → POST /token.
/// Later OIDC work can extend this host instead of building a parallel one.
/// </summary>
public sealed class HostedAuthorizeTokenFixture : IAsyncDisposable
{
    public const string ClientId = "scope-pin-client";
    public const string RedirectUri = "https://client.example.test/callback";
    public const string Password = "P@ssword123!";
    public const string TrustedOrigin = "https://auth.example.test";

    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required string Email { get; init; }
    public required string UserId { get; init; }

    public static async Task<HostedAuthorizeTokenFixture> CreateAsync(
        string databasePrefix = "ScopeTokenPins",
        Action<SqlOS.Configuration.SqlOSOptions>? configure = null)
    {
        await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix);
        var connectionString = bootstrapContext.Database.GetConnectionString()!;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestSqlOSDbContext>(database => database.UseSqlServer(connectionString));
        builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
        {
            options.AuthServer.Issuer = $"{TrustedOrigin}/sqlos/auth";
            options.AuthServer.PublicOrigin = TrustedOrigin;
            options.AuthServer.BasePath = "/sqlos/auth";
            options.AuthServer.SeedBrowserClient(ClientId, "Scope Pin Client", RedirectUri);
            options.AuthServer.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["password"];
                page.EnablePasswordSignup = true;
            });
            configure?.Invoke(options);
        });
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.MapAuthServer("/sqlos/auth");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
            var email = $"scope-pin-{Guid.NewGuid():N}@example.test";
            var user = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateUserAsync(new SqlOSCreateUserRequest("Scope Pin User", email, Password));
            await app.StartAsync();

            var client = app.GetTestClient();
            client.BaseAddress = new Uri(TrustedOrigin);
            return new HostedAuthorizeTokenFixture
            {
                App = app,
                Client = client,
                Email = email,
                UserId = user.Id
            };
        }
    }

    public async Task SetClientAllowedScopesAsync(params string[] scopes)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var client = await db.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == ClientId);
        client.AllowedScopesJson = JsonSerializer.Serialize(scopes);
        await db.SaveChangesAsync();
    }

    public async Task<JsonDocument> AuthorizeLoginAndExchangeAsync(string scope)
    {
        var started = await StartAuthorizeAsync(scope);
        var code = await SubmitPasswordLoginAsync(started);
        return await ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
    }

    public async Task<HostedAuthorizeStart> StartAuthorizeAsync(string scope, string? state = null, string? nonce = null)
    {
        var codeVerifier = CreateCodeVerifier();
        var authorizeUrl = BuildAuthorizeUrl(scope, state, nonce, prompt: null, maxAge: null, codeVerifier);

        using var authorize = await Client.GetAsync(authorizeUrl);
        var html = await authorize.Content.ReadAsStringAsync();
        if (authorize.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"GET /authorize returned {(int)authorize.StatusCode}: {html}");
        }

        return new HostedAuthorizeStart(
            ExtractInputValue(html, "requestId"),
            codeVerifier,
            ExtractInputValue(html, "__RequestVerificationToken"),
            ExtractCookie(authorize, "sqlos_auth_page_csrf_"));
    }

    public async Task<string> SubmitPasswordLoginAsync(HostedAuthorizeStart started)
        => (await SubmitPasswordLoginWithSessionAsync(started)).Code;

    /// <summary>
    /// Same as <see cref="SubmitPasswordLoginAsync"/>, but also captures the
    /// <c>sqlos_auth_page</c> session cookie so a later authorize request can
    /// exercise silent SSO reuse, <c>prompt</c>, and <c>max_age</c> semantics.
    /// </summary>
    public async Task<HostedLoginResult> SubmitPasswordLoginWithSessionAsync(HostedAuthorizeStart started)
    {
        using var login = new HttpRequestMessage(HttpMethod.Post, "/sqlos/auth/login/password")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = started.RequestId,
                ["email"] = Email,
                ["password"] = Password,
                ["__RequestVerificationToken"] = started.AntiforgeryToken
            })
        };
        login.Headers.TryAddWithoutValidation("Cookie", started.AntiforgeryCookie);
        login.Headers.TryAddWithoutValidation("Origin", TrustedOrigin);
        using var response = await Client.SendAsync(login);
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location is null)
        {
            throw new InvalidOperationException(
                $"Password login did not redirect with an authorization code. Status {(int)response.StatusCode}: {body}");
        }

        var location = response.Headers.Location;
        var query = QueryHelpers.ParseQuery(location.IsAbsoluteUri ? location.Query : location.OriginalString);
        var code = query["code"].ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException($"Authorization redirect omitted code: {location}");
        }

        return new HostedLoginResult(code, ExtractCookie(response, "sqlos_auth_page="));
    }

    /// <summary>
    /// Replays a captured auth-page session cookie against a fresh authorize request.
    /// Returns the raw response so callers can assert silent SSO (302 with a code),
    /// a re-challenge (200 login page), or an error redirect.
    /// </summary>
    public async Task<HostedSessionAuthorize> AuthorizeWithSessionAsync(
        string scope,
        string authPageCookie,
        string? prompt = null,
        string? maxAge = null,
        string? nonce = null)
    {
        var codeVerifier = CreateCodeVerifier();
        var authorizeUrl = BuildAuthorizeUrl(scope, state: null, nonce, prompt, maxAge, codeVerifier);
        using var request = new HttpRequestMessage(HttpMethod.Get, authorizeUrl);
        request.Headers.TryAddWithoutValidation("Cookie", authPageCookie);
        var response = await Client.SendAsync(request);
        return new HostedSessionAuthorize(response, codeVerifier);
    }

    private static string BuildAuthorizeUrl(
        string scope,
        string? state,
        string? nonce,
        string? prompt,
        string? maxAge,
        string codeVerifier)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["state"] = state ?? $"state-{Guid.NewGuid():N}",
            ["scope"] = scope,
            ["code_challenge"] = CreateCodeChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",
            ["view"] = "password"
        };
        if (!string.IsNullOrWhiteSpace(nonce))
        {
            query["nonce"] = nonce;
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            query["prompt"] = prompt;
        }

        if (maxAge != null)
        {
            query["max_age"] = maxAge;
        }

        return QueryHelpers.AddQueryString("/sqlos/auth/authorize", query);
    }

    public async Task<JsonDocument> ExchangeAuthorizationCodeAsync(string code, string codeVerifier)
    {
        using var response = await Client.PostAsync(
            "/sqlos/auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = codeVerifier
            }));
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"POST /token code grant returned {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> RefreshAsync(string refreshToken)
    {
        using var response = await Client.PostAsync(
            "/sqlos/auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId
            }));
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"POST /token refresh grant returned {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await using (var scope = App.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
        }

        await App.StopAsync();
        await App.DisposeAsync();
    }

    private static string ExtractInputValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $@"name=""{Regex.Escape(name)}"" value=""([^""]*)""",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Hosted authorize HTML did not include '{name}'.");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string ExtractCookie(HttpResponseMessage response, string prefix)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            throw new InvalidOperationException("Authorize response did not set cookies.");
        }

        return values.Select(value => value.Split(';', 2)[0])
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string CreateCodeVerifier()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    private static string CreateCodeChallenge(string verifier)
        => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
}

public sealed record HostedAuthorizeStart(
    string RequestId,
    string CodeVerifier,
    string AntiforgeryToken,
    string AntiforgeryCookie);

public sealed record HostedLoginResult(
    string Code,
    string AuthPageCookie);

public sealed record HostedSessionAuthorize(
    HttpResponseMessage Response,
    string CodeVerifier) : IDisposable
{
    public void Dispose() => Response.Dispose();
}
