using Microsoft.AspNetCore.Http;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Extensions;

/// <summary>
/// Request-context helpers for a token the <c>SqlOS</c> authentication scheme already validated.
/// </summary>
public static class SqlOSAccessTokenValidationExtensions
{
    /// <summary>The <see cref="HttpContext.Items"/> key used to store a successfully validated token.</summary>
    public const string ValidatedTokenItemKey = "SqlOS.AuthServer.ValidatedAccessToken";

    /// <summary>
    /// Gets the token validated for the current request by the <c>SqlOS</c> authentication scheme.
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

internal enum SqlOSBearerTicketKind
{
    Missing,
    Invalid,
    InsufficientScope,
    Success
}

internal readonly struct SqlOSBearerTicket
{
    public SqlOSBearerTicketKind Kind { get; private init; }
    public SqlOSValidatedToken? Token { get; private init; }
    public string Failure { get; private init; }

    public static SqlOSBearerTicket Missing()
        => new() { Kind = SqlOSBearerTicketKind.Missing, Failure = "A bearer access token is required." };

    public static SqlOSBearerTicket Invalid()
        => new()
        {
            Kind = SqlOSBearerTicketKind.Invalid,
            Failure = "The bearer access token is invalid, expired, revoked, or was not minted for this resource."
        };

    public static SqlOSBearerTicket InsufficientScope(string description)
        => new() { Kind = SqlOSBearerTicketKind.InsufficientScope, Failure = description };

    public static SqlOSBearerTicket Success(SqlOSValidatedToken token)
        => new() { Kind = SqlOSBearerTicketKind.Success, Token = token, Failure = string.Empty };
}

internal static class SqlOSBearerAuthentication
{
    public static async Task<SqlOSBearerTicket> AuthenticateAsync(
        HttpContext context,
        SqlOSAccessTokenValidationOptions options,
        SqlOSAuthService authService,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return SqlOSBearerTicket.Missing();
        }

        var rawToken = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return SqlOSBearerTicket.Missing();
        }

        var validated = await authService.ValidateAccessTokenAsync(
            rawToken,
            options.ExpectedAudience,
            cancellationToken);

        if (validated == null)
        {
            return SqlOSBearerTicket.Invalid();
        }

        if (SqlOSScopeRequirementPolicy.DescribeUnsatisfied(options.RequiredScopes, validated.Scope) is { } scopeFailure)
        {
            return SqlOSBearerTicket.InsufficientScope(scopeFailure);
        }

        context.Items[SqlOSAccessTokenValidationExtensions.ValidatedTokenItemKey] = validated;
        return SqlOSBearerTicket.Success(validated);
    }

    public static Task WriteUnauthorizedAsync(
        HttpResponse response,
        SqlOSAccessTokenValidationOptions options,
        string description)
        => WriteChallengeAsync(response, options, StatusCodes.Status401Unauthorized, "invalid_token", description);

    public static Task WriteInsufficientScopeAsync(
        HttpResponse response,
        SqlOSAccessTokenValidationOptions options,
        string description)
        => WriteChallengeAsync(response, options, StatusCodes.Status403Forbidden, "insufficient_scope", description);

    private static async Task WriteChallengeAsync(
        HttpResponse response,
        SqlOSAccessTokenValidationOptions options,
        int statusCode,
        string error,
        string description)
    {
        options = SqlOSAccessTokenValidationMiddleware.NormalizeOptions(options, requireAudience: false);
        response.StatusCode = statusCode;
        response.Headers.WWWAuthenticate = BuildChallenge(options, error, description);
        await response.WriteAsJsonAsync(new
        {
            error,
            error_description = description
        });
    }

    internal static string BuildChallenge(SqlOSAccessTokenValidationOptions options, string error, string description)
    {
        var parts = new List<string>
        {
            $"Bearer realm=\"{EscapeHeaderValue(options.Realm)}\"",
            $"error=\"{error}\"",
            $"error_description=\"{EscapeHeaderValue(description)}\""
        };

        if (string.Equals(error, "insufficient_scope", StringComparison.Ordinal) && options.RequiredScopes.Count > 0)
        {
            parts.Add($"scope=\"{EscapeHeaderValue(string.Join(' ', options.RequiredScopes))}\"");
        }

        if (!string.IsNullOrWhiteSpace(options.ResourceMetadataUrl))
        {
            parts.Add($"resource_metadata=\"{EscapeHeaderValue(options.ResourceMetadataUrl)}\"");
        }

        return string.Join(", ", parts);
    }

    private static string EscapeHeaderValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>
/// Shared scope-requirement evaluation for token validation: the token's granted scope
/// (the client application's delegation ceiling) must include every required scope.
/// Per-user, per-resource authorization remains with FGA.
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

internal sealed class SqlOSAccessTokenValidationMiddleware
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

        var ticket = await SqlOSBearerAuthentication.AuthenticateAsync(
            context,
            _options,
            authService,
            context.RequestAborted);

        if (ticket.Kind == SqlOSBearerTicketKind.Success && ticket.Token != null)
        {
            context.User = ticket.Token.Principal;
            await _next(context);
            return;
        }

        if (ticket.Kind == SqlOSBearerTicketKind.InsufficientScope)
        {
            await SqlOSBearerAuthentication.WriteInsufficientScopeAsync(context.Response, _options, ticket.Failure);
            return;
        }

        await SqlOSBearerAuthentication.WriteUnauthorizedAsync(context.Response, _options, ticket.Failure);
    }

    internal static SqlOSAccessTokenValidationOptions ValidateOptions(SqlOSAccessTokenValidationOptions? options)
        => NormalizeOptions(options, requireAudience: true);

    internal static SqlOSAccessTokenValidationOptions NormalizeOptions(
        SqlOSAccessTokenValidationOptions? options,
        bool requireAudience)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (requireAudience && string.IsNullOrWhiteSpace(options.ExpectedAudience))
        {
            throw new InvalidOperationException("SqlOS access-token validation requires a non-empty expected audience.");
        }

        options.ExpectedAudience = options.ExpectedAudience?.Trim() ?? string.Empty;
        options.RequiredScopes = SqlOSScopeRequirementPolicy.Normalize(options.RequiredScopes);
        options.Realm = string.IsNullOrWhiteSpace(options.Realm) ? "SqlOS API" : options.Realm.Trim();
        options.ResourceMetadataUrl = string.IsNullOrWhiteSpace(options.ResourceMetadataUrl)
            ? null
            : options.ResourceMetadataUrl.Trim();

        return options;
    }
}
