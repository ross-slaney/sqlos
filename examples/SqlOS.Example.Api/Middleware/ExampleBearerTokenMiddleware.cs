using SqlOS.AuthServer.Services;

namespace SqlOS.Example.Api.Middleware;

public sealed class ExampleBearerTokenMiddleware
{
    private readonly RequestDelegate _next;

    public ExampleBearerTokenMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, SqlOSAuthService authService)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var requiresAuth = path.StartsWith("/api/workspaces", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/hello", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/me", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/auth/session", StringComparison.OrdinalIgnoreCase);

        if (!requiresAuth)
        {
            await _next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Bearer token is required." });
            return;
        }

        var token = authorization["Bearer ".Length..].Trim();
        var validated = await authService.ValidateAccessTokenAsync(token, context.RequestAborted);
        if (validated == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Bearer token is invalid." });
            return;
        }

        context.User = validated.Principal;
        await _next(context);
    }
}

public static class ExampleBearerTokenMiddlewareExtensions
{
    public static IApplicationBuilder UseExampleBearerTokenMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<ExampleBearerTokenMiddleware>();
}
