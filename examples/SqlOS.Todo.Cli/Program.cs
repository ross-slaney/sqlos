using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var noBrowser = args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase)
    || IsTruthy(Environment.GetEnvironmentVariable("SQLOS_TODO_CLI_NO_BROWSER"));
var positional = args.Where(arg => !arg.Equals("--no-browser", StringComparison.OrdinalIgnoreCase)).ToArray();
var command = positional.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var commandArgs = positional.Skip(1).ToArray();
using var http = new HttpClient();

try
{
    switch (command)
    {
        case "login":
            await LoginAsync(http, noBrowser);
            break;
        case "logout":
            TokenStore.Delete();
            Console.WriteLine("Logged out.");
            break;
        case "whoami":
            await PrintJsonAsync(await ApiGetAsync(http, "/api/me"));
            break;
        case "list":
            await ListTodosAsync(http, ReadPageSize(commandArgs));
            break;
        case "add":
            if (commandArgs.Length == 0 || string.IsNullOrWhiteSpace(commandArgs[0]))
            {
                throw new InvalidOperationException("Usage: todo-cli add \"Ship the CLI\"");
            }

            await PrintJsonAsync(await ApiSendAsync(http, HttpMethod.Post, "/api/todos", new { title = string.Join(' ', commandArgs) }));
            break;
        case "toggle":
            if (commandArgs.Length == 0 || string.IsNullOrWhiteSpace(commandArgs[0]))
            {
                throw new InvalidOperationException("Usage: todo-cli toggle <todo-id>");
            }

            await PrintJsonAsync(await ApiSendAsync(http, HttpMethod.Post, $"/api/todos/{Uri.EscapeDataString(commandArgs[0])}/toggle", body: null));
            break;
        default:
            PrintHelp();
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static async Task LoginAsync(HttpClient http, bool noBrowser)
{
    var discovery = await DiscoverAsync(http);
    var scope = string.Join(' ', discovery.AllowedScopes);
    var startResponse = await PostFormAsync(http, discovery.DeviceAuthorizationEndpoint, new Dictionary<string, string?>
    {
        ["client_id"] = discovery.ClientId,
        ["scope"] = scope,
        ["resource"] = discovery.Resource
    });

    await using var startStream = await startResponse.Content.ReadAsStreamAsync();
    var start = await JsonSerializer.DeserializeAsync<DeviceAuthorizationResponse>(startStream, CliJson.Options)
        ?? throw new InvalidOperationException("Device authorization response was empty.");

    Console.WriteLine("Open this URL to sign in:");
    Console.WriteLine(start.VerificationUriComplete);
    Console.WriteLine();
    Console.WriteLine($"Device code: {start.UserCode}");
    if (!noBrowser)
    {
        TryOpenBrowser(start.VerificationUriComplete);
    }

    var interval = Math.Max(1, start.Interval);
    var expiresAt = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresIn);
    while (DateTimeOffset.UtcNow < expiresAt)
    {
        await Task.Delay(TimeSpan.FromSeconds(interval));

        var tokenResponse = await http.PostAsync(
            discovery.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string?>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = discovery.ClientId,
                ["device_code"] = start.DeviceCode,
                ["resource"] = discovery.Resource
            }!));

        var payload = await tokenResponse.Content.ReadAsStringAsync();
        if (tokenResponse.IsSuccessStatusCode)
        {
            var token = JsonSerializer.Deserialize<TokenEndpointResponse>(payload, CliJson.Options)
                ?? throw new InvalidOperationException("Token response was empty.");
            TokenStore.Save(new StoredTokens(
                discovery.ApiBase,
                discovery.Issuer,
                discovery.ClientId,
                discovery.Resource,
                token.AccessToken,
                token.RefreshToken,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, token.ExpiresIn))));
            Console.WriteLine("Signed in. Run `whoami`, `add`, `list`, or `toggle`.");
            return;
        }

        var error = JsonSerializer.Deserialize<OAuthErrorResponse>(payload, CliJson.Options);
        switch (error?.Error)
        {
            case "authorization_pending":
                continue;
            case "slow_down":
                interval = Math.Max(interval + 5, error.Interval ?? interval + 5);
                continue;
            case "access_denied":
                throw new InvalidOperationException("Sign-in was denied in the browser.");
            case "expired_token":
                throw new InvalidOperationException("The device code expired. Run login again.");
            default:
                throw new InvalidOperationException(error?.ErrorDescription ?? payload);
        }
    }

    throw new InvalidOperationException("The device code expired. Run login again.");
}

static async Task<JsonDocument> ApiGetAsync(HttpClient http, string path)
{
    await EnsureAccessTokenAsync(http);
    using var request = new HttpRequestMessage(HttpMethod.Get, $"{TokenStore.LoadRequired().ApiBase.TrimEnd('/')}{path}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenStore.LoadRequired().AccessToken);
    using var response = await http.SendAsync(request);
    return await ReadJsonOrThrowAsync(response);
}

static async Task<JsonDocument> ApiSendAsync(HttpClient http, HttpMethod method, string path, object? body)
{
    await EnsureAccessTokenAsync(http);
    using var request = new HttpRequestMessage(method, $"{TokenStore.LoadRequired().ApiBase.TrimEnd('/')}{path}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenStore.LoadRequired().AccessToken);
    if (body != null)
    {
        request.Content = new StringContent(JsonSerializer.Serialize(body, CliJson.Options), Encoding.UTF8, "application/json");
    }

    using var response = await http.SendAsync(request);
    return await ReadJsonOrThrowAsync(response);
}

static async Task EnsureAccessTokenAsync(HttpClient http)
{
    var tokens = TokenStore.LoadRequired();
    if (tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
    {
        return;
    }

    if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
    {
        throw new InvalidOperationException("Not signed in. Run `login` first.");
    }

    var discovery = await DiscoverAsync(http, tokens.ApiBase);
    var response = await PostFormAsync(http, discovery.TokenEndpoint, new Dictionary<string, string?>
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = tokens.RefreshToken,
        ["client_id"] = tokens.ClientId,
        ["resource"] = tokens.Resource
    });

    await using var stream = await response.Content.ReadAsStreamAsync();
    var refreshed = await JsonSerializer.DeserializeAsync<TokenEndpointResponse>(stream, CliJson.Options)
        ?? throw new InvalidOperationException("Refresh token response was empty.");
    TokenStore.Save(tokens with
    {
        AccessToken = refreshed.AccessToken,
        RefreshToken = refreshed.RefreshToken,
        AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, refreshed.ExpiresIn))
    });
}

static async Task<Discovery> DiscoverAsync(HttpClient http, string? apiBaseOverride = null)
{
    var apiBase = apiBaseOverride?.TrimEnd('/')
        ?? Environment.GetEnvironmentVariable("SQLOS_TODO_API_ORIGIN")?.TrimEnd('/')
        ?? "http://localhost:5080";
    using var sampleResponse = await http.GetAsync($"{apiBase}/sample/config");
    using var sampleJson = await ReadJsonOrThrowAsync(sampleResponse);
    var root = sampleJson.RootElement;
    var issuer = root.GetProperty("issuer").GetString() ?? throw new InvalidOperationException("Sample config missing issuer.");
    var resource = root.GetProperty("resource").GetString() ?? throw new InvalidOperationException("Sample config missing resource.");
    var clientId = root.GetProperty("cliClient").GetProperty("clientId").GetString()
        ?? throw new InvalidOperationException("Sample config missing cliClient.clientId.");
    var scopes = root.GetProperty("allowedScopes")
        .EnumerateArray()
        .Select(x => x.GetString())
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Cast<string>()
        .ToArray();

    using var metadataResponse = await http.GetAsync($"{issuer.TrimEnd('/')}/.well-known/oauth-authorization-server");
    using var metadataJson = await ReadJsonOrThrowAsync(metadataResponse);
    var metadata = metadataJson.RootElement;
    var tokenEndpoint = metadata.GetProperty("token_endpoint").GetString()
        ?? throw new InvalidOperationException("Authorization server metadata missing token_endpoint.");
    var deviceEndpoint = metadata.GetProperty("device_authorization_endpoint").GetString()
        ?? throw new InvalidOperationException("Authorization server metadata missing device_authorization_endpoint.");

    return new Discovery(apiBase, issuer, clientId, resource, scopes, tokenEndpoint, deviceEndpoint);
}

static async Task<HttpResponseMessage> PostFormAsync(HttpClient http, string url, Dictionary<string, string?> values)
{
    var response = await http.PostAsync(
        url,
        new FormUrlEncodedContent(values
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new KeyValuePair<string, string>(x.Key, x.Value!))));
    if (response.IsSuccessStatusCode)
    {
        return response;
    }

    var payload = await response.Content.ReadAsStringAsync();
    var error = JsonSerializer.Deserialize<OAuthErrorResponse>(payload, CliJson.Options);
    throw new InvalidOperationException(error?.ErrorDescription ?? payload);
}

static async Task<JsonDocument> ReadJsonOrThrowAsync(HttpResponseMessage response)
{
    var payload = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(payload);
    }

    return JsonDocument.Parse(payload);
}

static async Task PrintJsonAsync(JsonDocument document)
{
    await using var stdout = Console.OpenStandardOutput();
    await JsonSerializer.SerializeAsync(stdout, document.RootElement, CliJson.Options);
    Console.WriteLine();
}

// GET /api/todos returns one page ({ data, nextCursor, ... }); follow nextCursor to the last page.
static async Task ListTodosAsync(HttpClient http, int? pageSize)
{
    string? cursor = null;
    var printed = 0;
    do
    {
        var query = new List<string>();
        if (pageSize != null)
        {
            query.Add($"pageSize={pageSize}");
        }

        if (cursor != null)
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        using var page = await ApiGetAsync(http, query.Count == 0 ? "/api/todos" : $"/api/todos?{string.Join('&', query)}");
        foreach (var item in page.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            var title = item.GetProperty("title").GetString();
            var completed = item.GetProperty("isCompleted").GetBoolean();
            Console.WriteLine($"{(completed ? "x" : " ")} {id}  {title}");
            printed++;
        }

        cursor = page.RootElement.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;
    }
    while (cursor != null);

    if (printed == 0)
    {
        Console.WriteLine("No todos.");
    }
}

static int? ReadPageSize(string[] commandArgs)
{
    var index = Array.FindIndex(commandArgs, arg => arg.Equals("--page-size", StringComparison.OrdinalIgnoreCase));
    if (index < 0)
    {
        return null;
    }

    return index + 1 < commandArgs.Length && int.TryParse(commandArgs[index + 1], out var pageSize) && pageSize > 0
        ? pageSize
        : throw new InvalidOperationException("Usage: todo-cli list [--page-size <n>]");
}

static void TryOpenBrowser(string url)
{
    try
    {
        var fileName = OperatingSystem.IsMacOS()
            ? "open"
            : OperatingSystem.IsWindows()
                ? "cmd"
                : "xdg-open";
        var arguments = OperatingSystem.IsWindows()
            ? $"/c start {url}"
            : url;
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false });
    }
    catch
    {
        // The URL is printed above; browser launch is best-effort for terminal environments.
    }
}

static bool IsTruthy(string? value)
    => value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

static void PrintHelp()
{
    Console.WriteLine("""
    SqlOS Todo CLI

    Commands:
      login [--no-browser]
      logout
      whoami
      list [--page-size <n>]
      add "<text>"
      toggle <todo-id>

    Environment:
      SQLOS_TODO_API_ORIGIN=http://localhost:5080
      SQLOS_TODO_CLI_NO_BROWSER=1   print the sign-in URL without launching a browser
      SQLOS_TODO_CLI_HOME=<dir>     store tokens under <dir> instead of ~/.sqlos/todo-cli
    """);
}

public sealed record Discovery(
    string ApiBase,
    string Issuer,
    string ClientId,
    string Resource,
    string[] AllowedScopes,
    string TokenEndpoint,
    string DeviceAuthorizationEndpoint);

public sealed record DeviceAuthorizationResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

public sealed record TokenEndpointResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope);

public sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonPropertyName("interval")] int? Interval);

public sealed record StoredTokens(
    string ApiBase,
    string Issuer,
    string ClientId,
    string Resource,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt);

public static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public static class TokenStore
{
    // SQLOS_TODO_CLI_HOME lets tests and multi-profile setups keep tokens away
    // from the real ~/.sqlos/todo-cli file.
    private static readonly string DirectoryPath = Environment.GetEnvironmentVariable("SQLOS_TODO_CLI_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sqlos",
            "todo-cli");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "tokens.json");

    public static StoredTokens LoadRequired()
        => Load() ?? throw new InvalidOperationException("Not signed in. Run `login` first.");

    public static StoredTokens? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<StoredTokens>(json, CliJson.Options);
    }

    public static void Save(StoredTokens tokens)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(tokens, CliJson.Options));
    }

    public static void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
