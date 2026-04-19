using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSHeadlessAuthOptions
{
    public bool EnableApi { get; set; } = true;
    public string? HeadlessApiBasePath { get; set; }
    public Func<SqlOSHeadlessUiRouteContext, string>? BuildUiUrl { get; set; }
    public Func<SqlOSHeadlessSignupHookContext, CancellationToken, Task>? OnHeadlessSignupAsync { get; set; }

    public string ResolveApiBasePath(string authBasePath)
    {
        if (string.IsNullOrWhiteSpace(HeadlessApiBasePath))
        {
            return $"{authBasePath.TrimEnd('/')}/headless";
        }

        var normalized = HeadlessApiBasePath.Trim();
        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized.TrimEnd('/') : $"/{normalized.TrimEnd('/')}";
    }
}

public sealed record SqlOSHeadlessUiRouteContext(
    HttpContext HttpContext,
    string? RequestId,
    string View,
    string? Error,
    string? PendingToken,
    string? Email,
    string? DisplayName,
    JsonObject? UiContext);

public sealed record SqlOSHeadlessSignupHookContext(
    HttpContext HttpContext,
    SqlOSAuthorizationRequest? AuthorizationRequest,
    SqlOSUser User,
    SqlOSOrganization? Organization,
    JsonObject CustomFields);
