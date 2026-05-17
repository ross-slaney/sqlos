using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Extensions;

public static class SqlOSAccessTokenValidationExtensions
{
    public const string ValidatedTokenItemKey = "SqlOS.AuthServer.ValidatedAccessToken";

    public static IApplicationBuilder UseSqlOSAccessTokenValidation(
        this IApplicationBuilder app,
        string expectedAudience)
        => app.UseSqlOSAccessTokenValidation(options => options.ExpectedAudience = expectedAudience);

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

    public static SqlOSValidatedToken? GetSqlOSValidatedToken(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ValidatedTokenItemKey, out var value)
            ? value as SqlOSValidatedToken
            : null;
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
        options.Realm = string.IsNullOrWhiteSpace(options.Realm) ? "SqlOS API" : options.Realm.Trim();
        options.ResourceMetadataUrl = string.IsNullOrWhiteSpace(options.ResourceMetadataUrl)
            ? null
            : options.ResourceMetadataUrl.Trim();

        return options;
    }

    private async Task WriteUnauthorizedAsync(HttpContext context, string description)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = BuildChallenge(description);
        await context.Response.WriteAsJsonAsync(new
        {
            error = "invalid_token",
            error_description = description
        });
    }

    private string BuildChallenge(string description)
    {
        var parts = new List<string>
        {
            $"Bearer realm=\"{EscapeHeaderValue(_options.Realm)}\"",
            "error=\"invalid_token\"",
            $"error_description=\"{EscapeHeaderValue(description)}\""
        };

        if (!string.IsNullOrWhiteSpace(_options.ResourceMetadataUrl))
        {
            parts.Add($"resource_metadata=\"{EscapeHeaderValue(_options.ResourceMetadataUrl!)}\"");
        }

        return string.Join(", ", parts);
    }

    private static string EscapeHeaderValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
