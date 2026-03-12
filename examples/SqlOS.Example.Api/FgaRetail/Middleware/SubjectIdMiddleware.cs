using System.IdentityModel.Tokens.Jwt;

namespace SqlOS.Example.Api.FgaRetail.Middleware;

public static class SubjectIdExtensions
{
    public static string GetSubjectId(this HttpContext context)
    {
        // JWT bearer token (AuthServer users)
        var sub = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
            return sub;

        // API key or agent token (resolved by middleware)
        if (context.Items.TryGetValue("SubjectId", out var subjectId)
            && subjectId is string id
            && !string.IsNullOrWhiteSpace(id))
            return id;

        throw new InvalidOperationException(
            "No subject found. Ensure bearer token, API key, or agent token authentication is configured.");
    }
}
