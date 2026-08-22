using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Extensions;

/// <summary>
/// Provides ASP.NET Core middleware and request-context helpers for validating SqlOS access tokens.
/// </summary>
public static class SqlOSAccessTokenValidationExtensions
{
    /// <summary>The <see cref="HttpContext.Items"/> key used to store a successfully validated token.</summary>
    public const string ValidatedTokenItemKey = "SqlOS.AuthServer.ValidatedAccessToken";

    /// <summary>
    /// Adds SqlOS bearer access-token validation to the application pipeline for the specified audience.
    /// </summary>
    /// <param name="app">The application pipeline builder.</param>
    /// <param name="expectedAudience">The exact audience required in a valid access token.</param>
    /// <returns>The same <paramref name="app"/> instance.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="expectedAudience"/> is empty or contains only whitespace.</exception>
    public static IApplicationBuilder UseSqlOSAccessTokenValidation(
        this IApplicationBuilder app,
        string expectedAudience)
        => app.UseSqlOSAccessTokenValidation(options => options.ExpectedAudience = expectedAudience);

    /// <summary>
    /// Adds configured SqlOS bearer access-token validation to the application pipeline.
    /// </summary>
    /// <param name="app">The application pipeline builder.</param>
    /// <param name="configure">A callback that configures token validation.</param>
    /// <returns>The same <paramref name="app"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configured <see cref="SqlOSAccessTokenValidationOptions.ExpectedAudience"/> is empty.
    /// </exception>
    /// <remarks>
    /// Successful validation sets <see cref="HttpContext.User"/> and makes the validated token
    /// available through <see cref="GetSqlOSValidatedToken(HttpContext)"/>. Failed validation
    /// short-circuits the request with an HTTP 401 response and a Bearer challenge.
    /// </remarks>
    public static IApplicationBuilder UseSqlOSAccessTokenValidation(
        this IApplicationBuilder app,
        Action<SqlOSAccessTokenValidationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlOSAccessTokenValidationOptions();
        configure(options);
        SqlOSAccessTokenValidationMiddleware.ValidateOptions(options);

        return app.UseMiddleware<SqlOSAccessTokenValidationMiddleware>(options);
    }

    /// <summary>
    /// Gets the token validated for the current request by SqlOS access-token middleware or a
    /// SqlOS-protected route group.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The validated token, or <see langword="null"/> when SqlOS did not validate a token for the request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static SqlOSValidatedToken? GetSqlOSValidatedToken(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ValidatedTokenItemKey, out var value)
            ? value as SqlOSValidatedToken
            : null;
    }
}

/// <summary>
/// Shared scope-requirement evaluation for the validation middleware and the route-group
/// filter: the token's granted scope (the client application's delegation ceiling) must
/// include every required scope. Per-user, per-resource authorization remains with FGA.
/// </summary>
internal static class SqlOSScopeRequirementPolicy
{
    internal static IReadOnlyCollection<string> Normalize(IReadOnlyCollection<string>? requiredScopes)
        => requiredScopes is null || requiredScopes.Count == 0
            ? []
            : requiredScopes
                .Select(scope => scope?.Trim() ?? string.Empty)
                .Where(scope => scope.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>
    /// Returns a failure description when the granted scope does not satisfy the
    /// requirement, or null when it does. A token without a scope claim fails closed:
    /// its grant is unknown, and enforcement must not assume the widest one.
    /// </summary>
    internal static string? DescribeUnsatisfied(IReadOnlyCollection<string> requiredScopes, string? grantedScope)
    {
        if (requiredScopes.Count == 0)
        {
            return null;
        }

        if (grantedScope is null)
        {
            return "The access token carries no granted scope, and this resource requires one.";
        }

        var granted = grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missing = requiredScopes.Where(scope => !granted.Contains(scope, StringComparer.Ordinal)).ToArray();
        return missing.Length == 0
            ? null
            : $"The access token's granted scope does not include: {string.Join(' ', missing)}.";
    }
}

public sealed class SqlOSAccessTokenValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SqlOSAccessTokenValidationOptions _options;

    public SqlOSAccessTokenValidationMiddleware(
        RequestDelegate next,
        SqlOSAccessTokenValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
        _options = ValidateOptions(options);
    }

    public async Task InvokeAsync(HttpContext context, SqlOSAuthService authService)
    {
        if (_options.ShouldValidate is { } shouldValidate && !shouldValidate(context))
        {
            await _next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context, "A bearer access token is required.");
            return;
        }

        var rawToken = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            await WriteUnauthorizedAsync(context, "A bearer access token is required.");
            return;
        }

        var validated = await authService.ValidateAccessTokenAsync(
            rawToken,
            _options.ExpectedAudience,
            context.RequestAborted);

        if (validated == null)
        {
            await WriteUnauthorizedAsync(context, "The bearer access token is invalid, expired, revoked, or was not minted for this resource.");
            return;
        }

        if (SqlOSScopeRequirementPolicy.DescribeUnsatisfied(_options.RequiredScopes, validated.Scope) is { } scopeFailure)
        {
            await WriteInsufficientScopeAsync(context, scopeFailure);
            return;
        }

        context.User = validated.Principal;
        context.Items[SqlOSAccessTokenValidationExtensions.ValidatedTokenItemKey] = validated;

        await _next(context);
    }

    internal static SqlOSAccessTokenValidationOptions ValidateOptions(SqlOSAccessTokenValidationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ExpectedAudience))
        {
            throw new InvalidOperationException("SqlOS access-token validation requires a non-empty expected audience.");
        }

        options.ExpectedAudience = options.ExpectedAudience.Trim();
        options.RequiredScopes = SqlOSScopeRequirementPolicy.Normalize(options.RequiredScopes);
        options.Realm = string.IsNullOrWhiteSpace(options.Realm) ? "SqlOS API" : options.Realm.Trim();
        options.ResourceMetadataUrl = string.IsNullOrWhiteSpace(options.ResourceMetadataUrl)
            ? null
            : options.ResourceMetadataUrl.Trim();

        return options;
    }

    private async Task WriteUnauthorizedAsync(HttpContext context, string description)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = BuildChallenge("invalid_token", description);
        await context.Response.WriteAsJsonAsync(new
        {
            error = "invalid_token",
            error_description = description
        });
    }

    // RFC 6750 §3.1: a token that is valid but lacks the required scope answers 403
    // with an insufficient_scope challenge naming the scope the resource requires.
    private async Task WriteInsufficientScopeAsync(HttpContext context, string description)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.WWWAuthenticate = BuildChallenge("insufficient_scope", description);
        await context.Response.WriteAsJsonAsync(new
        {
            error = "insufficient_scope",
            error_description = description
        });
    }

    private string BuildChallenge(string error, string description)
    {
        var parts = new List<string>
        {
            $"Bearer realm=\"{EscapeHeaderValue(_options.Realm)}\"",
            $"error=\"{error}\"",
            $"error_description=\"{EscapeHeaderValue(description)}\""
        };

        if (string.Equals(error, "insufficient_scope", StringComparison.Ordinal) && _options.RequiredScopes.Count > 0)
        {
            parts.Add($"scope=\"{EscapeHeaderValue(string.Join(' ', _options.RequiredScopes))}\"");
        }

        if (!string.IsNullOrWhiteSpace(_options.ResourceMetadataUrl))
        {
            parts.Add($"resource_metadata=\"{EscapeHeaderValue(_options.ResourceMetadataUrl!)}\"");
        }

        return string.Join(", ", parts);
    }

    private static string EscapeHeaderValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
