using System.IdentityModel.Tokens.Jwt;

namespace SqlOS.Example.Api.FgaRetail.Services;

public static class RetailSubjectResolver
{
    public static string ResolveSubjectId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // JWT bearer token (AuthServer users)
        var sub = http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
            return sub;

        // API key or agent token (resolved by middleware)
        if (http.Items.TryGetValue("SubjectId", out var subjectId)
            && subjectId is string id
            && !string.IsNullOrWhiteSpace(id))
            return id;

        throw new InvalidOperationException(
            "No subject found. Ensure bearer token, API key, or agent token authentication is configured.");
    }
}
