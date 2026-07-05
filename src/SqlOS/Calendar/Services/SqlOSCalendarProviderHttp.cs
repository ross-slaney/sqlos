using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlOS.Calendar.Contracts;

namespace SqlOS.Calendar.Services;

/// <summary>Shared HTTP/JSON helpers for the calendar provider adapters.</summary>
internal static class SqlOSCalendarProviderHttp
{
    public static async Task<SqlOSCalendarTokenResult> PostTokenFormAsync(
        HttpClient httpClient,
        string tokenEndpoint,
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formValues)
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        using var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(payload.RootElement.TryGetProperty("error_description", out var description)
                ? description.GetString() ?? "The calendar provider token request failed."
                : "The calendar provider token request failed.");
        }

        var accessToken = GetString(payload.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The calendar provider token response did not include an access token.");
        }

        var expiresIn = payload.RootElement.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? seconds
            : 3600;
        var scopes = (GetString(payload.RootElement, "scope") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new SqlOSCalendarTokenResult(
            accessToken!,
            GetString(payload.RootElement, "refresh_token"),
            DateTime.UtcNow.AddSeconds(expiresIn),
            scopes,
            GetString(payload.RootElement, "id_token"));
    }

    public static async Task<JsonDocument> GetJsonAsync(
        HttpClient httpClient,
        string url,
        string accessToken,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadProviderError(payload.RootElement);
            payload.Dispose();
            throw new InvalidOperationException(detail == null ? errorMessage : $"{errorMessage} {detail}");
        }

        return payload;
    }

    public static async Task<JsonDocument> PostJsonAsync(
        HttpClient httpClient,
        string url,
        string accessToken,
        object body,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadProviderError(payload.RootElement);
            payload.Dispose();
            throw new InvalidOperationException(detail == null ? errorMessage : $"{errorMessage} {detail}");
        }

        return payload;
    }

    public static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string FormatUtc(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? TryReadProviderError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            return GetString(error, "message") ?? GetString(error, "code");
        }

        return null;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }
}
