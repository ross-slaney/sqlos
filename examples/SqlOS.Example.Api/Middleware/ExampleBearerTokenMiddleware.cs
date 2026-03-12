using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Services;
using SqlOS.Example.Api.Data;
using SqlOS.Fga.Models;

namespace SqlOS.Example.Api.Middleware;

public sealed class ExampleBearerTokenMiddleware
{
    private readonly RequestDelegate _next;

    public ExampleBearerTokenMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, SqlOSAuthService authService, ExampleAppDbContext dbContext)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var requiresAuth = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/v1/auth/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/demo/", StringComparison.OrdinalIgnoreCase);

        if (!requiresAuth)
        {
            await _next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();

        // Path 1: Bearer JWT (AuthServer users)
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
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
            return;
        }

        // Path 2: X-Api-Key (service accounts)
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues))
        {
            var apiKey = apiKeyValues.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var serviceAccount = await dbContext.Set<SqlOSFgaServiceAccount>()
                    .FirstOrDefaultAsync(sa => sa.ClientId == apiKey, context.RequestAborted);

                if (serviceAccount != null && (serviceAccount.ExpiresAt == null || serviceAccount.ExpiresAt > DateTime.UtcNow))
                {
                    serviceAccount.LastUsedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(context.RequestAborted);
                    context.Items["SubjectId"] = serviceAccount.SubjectId;
                    await _next(context);
                    return;
                }

                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "API key is invalid or expired." });
                return;
            }
        }

        // Path 3: X-Agent-Token (agents)
        if (context.Request.Headers.TryGetValue("X-Agent-Token", out var agentTokenValues))
        {
            var agentToken = agentTokenValues.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(agentToken))
            {
                var agent = await dbContext.Set<SqlOSFgaAgent>()
                    .FirstOrDefaultAsync(a => a.SubjectId == agentToken, context.RequestAborted);

                if (agent != null)
                {
                    agent.LastRunAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(context.RequestAborted);
                    context.Items["SubjectId"] = agent.SubjectId;
                    await _next(context);
                    return;
                }

                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Agent token is invalid." });
                return;
            }
        }

        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Authentication is required. Provide a Bearer token, X-Api-Key, or X-Agent-Token header." });
    }
}

public static class ExampleBearerTokenMiddlewareExtensions
{
    public static IApplicationBuilder UseExampleBearerTokenMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<ExampleBearerTokenMiddleware>();
}
