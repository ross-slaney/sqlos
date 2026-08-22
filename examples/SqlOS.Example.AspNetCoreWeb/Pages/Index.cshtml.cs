using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SqlOS.Example.AspNetCoreWeb.Pages;

public sealed class IndexModel(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<IndexModel> logger) : PageModel
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true
    };

    [BindProperty(SupportsGet = true)]
    public string? Error { get; set; }

    public string? ApiResponse { get; private set; }
    public string? TokenExpiresAt { get; private set; }
    public int? ApiStatusCode { get; private set; }
    public bool ApiSucceeded => ApiStatusCode is >= 200 and < 300;
    public string DashboardUrl { get; } =
        $"{(configuration["SqlOS:Origin"] ?? "http://localhost:5080").TrimEnd('/')}/sqlos";

    private string ClientId { get; } = configuration["SqlOS:ClientId"] ?? "example-aspnet";

    public IActionResult OnGetLogin()
        => Challenge(
            new AuthenticationProperties { RedirectUri = Url.Page("/Index") ?? "/" },
            "SqlOS");

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var accessToken = await GetFreshAccessTokenAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            ApiResponse = "The authentication cookie did not contain an access token.";
            return;
        }

        var client = httpClientFactory.CreateClient("sqlos");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        ApiStatusCode = (int)response.StatusCode;
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(payload))
        {
            ApiResponse = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            ApiResponse = JsonSerializer.Serialize(document.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            ApiResponse = payload;
        }
    }

    private async Task<string?> GetFreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        var expiresAt = await HttpContext.GetTokenAsync("expires_at");
        TokenExpiresAt = expiresAt;

        if (string.IsNullOrWhiteSpace(accessToken)
            || !DateTimeOffset.TryParse(
                expiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiration)
            || expiration > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return accessToken;
        }

        var refreshToken = await HttpContext.GetTokenAsync("refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return accessToken;
        }

        var client = httpClientFactory.CreateClient("sqlos");
        using var response = await client.PostAsync(
            "/sqlos/auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "SqlOS access-token refresh returned HTTP {StatusCode}.",
                (int)response.StatusCode);
            return accessToken;
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var tokenResponse = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
        var root = tokenResponse.RootElement;
        var refreshedAccessToken = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(refreshedAccessToken))
        {
            logger.LogWarning("SqlOS refresh response did not include an access token.");
            return accessToken;
        }

        var refreshedRefreshToken = root.TryGetProperty("refresh_token", out var refreshProperty)
            ? refreshProperty.GetString()
            : refreshToken;
        var tokenType = root.TryGetProperty("token_type", out var typeProperty)
            ? typeProperty.GetString()
            : "Bearer";
        var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresProperty)
            ? expiresProperty.GetInt32()
            : 600;
        var refreshedExpiresAt = DateTimeOffset.UtcNow
            .AddSeconds(expiresInSeconds)
            .ToString("O", CultureInfo.InvariantCulture);

        var authentication = await HttpContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        if (authentication.Succeeded
            && authentication.Principal is { } principal
            && authentication.Properties is { } properties)
        {
            var tokens = properties.GetTokens().ToList();
            StoreToken(tokens, "access_token", refreshedAccessToken);
            StoreToken(tokens, "refresh_token", refreshedRefreshToken);
            StoreToken(tokens, "token_type", tokenType);
            StoreToken(tokens, "expires_at", refreshedExpiresAt);
            properties.StoreTokens(tokens);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                properties);
        }
        else
        {
            logger.LogWarning("The ASP.NET Core cookie could not be renewed after token refresh.");
        }

        TokenExpiresAt = refreshedExpiresAt;
        return refreshedAccessToken;
    }

    private static void StoreToken(
        List<AuthenticationToken> tokens,
        string name,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        tokens.RemoveAll(token => string.Equals(token.Name, name, StringComparison.Ordinal));
        tokens.Add(new AuthenticationToken { Name = name, Value = value });
    }

    public async Task<IActionResult> OnPostLogoutAsync(CancellationToken cancellationToken)
    {
        var refreshToken = await HttpContext.GetTokenAsync("refresh_token");
        var sessionId = User.FindFirst("sid")?.Value;

        try
        {
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                var client = httpClientFactory.CreateClient("sqlos");
                using var response = await client.PostAsJsonAsync(
                    "/sqlos/auth/logout",
                    new { refreshToken, sessionId },
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "SqlOS token revocation returned HTTP {StatusCode} during logout.",
                        (int)response.StatusCode);
                }
            }
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "SqlOS token revocation failed during logout.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "SqlOS token revocation timed out during logout.");
        }
        finally
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        return RedirectToPage();
    }
}
