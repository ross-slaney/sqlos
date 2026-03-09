namespace SqlOS.Example.Api.FgaRetail.Middleware;

public class SubjectIdMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly string[] SkipPaths = ["/swagger", "/sqlzibar", "/"];

    public SubjectIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip middleware for non-API paths
        if (path == "/" || SkipPaths.Any(p => p != "/" && path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Subject-Id", out var subjectId) ||
            string.IsNullOrWhiteSpace(subjectId))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "X-Subject-Id header is required" });
            return;
        }

        context.Items["SubjectId"] = subjectId.ToString();
        await _next(context);
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
