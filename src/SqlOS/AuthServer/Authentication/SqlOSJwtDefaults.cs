namespace SqlOS.AuthServer.Authentication;

/// <summary>
/// Well-known names for the same-process SqlOS JWT authentication scheme.
/// </summary>
public static class SqlOSJwtDefaults
{
    /// <summary>The authentication scheme registered by <c>AddSqlOS</c>. Audience is the declared <c>app.Api</c> resource when one exists.</summary>
    public const string AuthenticationScheme = "SqlOS";

    /// <summary>Scheme for the declared <c>app.Mcp</c> resource. Call <c>RequireAuthorization(McpPolicy)</c> on the host-mapped MCP endpoint.</summary>
    public const string McpAuthenticationScheme = "SqlOS.Mcp";

    /// <summary>Authorization policy that authenticates with <see cref="McpAuthenticationScheme"/>.</summary>
    public const string McpPolicy = "SqlOS.Mcp";

    internal const string ChallengeErrorItemKey = "SqlOS.Jwt.ChallengeError";
    internal const string ChallengeDescriptionItemKey = "SqlOS.Jwt.ChallengeDescription";
}
