using System.IdentityModel.Tokens.Jwt;
using SqlOS.AuthServer.Services;

namespace SqlOS.Example.Api.FgaRetail.Middleware;

public class SubjectIdMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly string[] NonRetailPrefixes = ["/swagger", "/sqlos", "/api/v1/auth", "/api/workspaces", "/api/me", "/"];

    public SubjectIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, SqlOSAuthService authService)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (!IsRetailPath(path))
        {
            await _next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Subject-Id", out var subjectId) &&
            !string.IsNullOrWhiteSpace(subjectId))
        {
            context.Items["SubjectId"] = subjectId.ToString();
            await _next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            var validated = await authService.ValidateAccessTokenAsync(token, context.RequestAborted);
            var bearerSubjectId = validated?.Principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (!string.IsNullOrWhiteSpace(bearerSubjectId))
            {
                context.User = validated!.Principal;
                context.Items["SubjectId"] = bearerSubjectId;
                await _next(context);
                return;
            }
        }

        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Retail endpoints require X-Subject-Id or a valid bearer token." });
    }

    private static bool IsRetailPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return false;
        }

        if (NonRetailPrefixes.Any(prefix => prefix != "/" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return path.StartsWith("/api/chains", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/locations", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/locations/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/inventory", StringComparison.OrdinalIgnoreCase);
    }
}

public static class SubjectIdMiddlewareExtensions
{
    public static IApplicationBuilder UseSubjectIdMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<SubjectIdMiddleware>();

    public static string GetSubjectId(this HttpContext context)
        => context.Items["SubjectId"] as string
           ?? throw new InvalidOperationException("SubjectId not found. Ensure SubjectIdMiddleware is registered.");
}
