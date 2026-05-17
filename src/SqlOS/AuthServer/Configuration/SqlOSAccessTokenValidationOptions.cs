using Microsoft.AspNetCore.Http;

namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSAccessTokenValidationOptions
{
    public string ExpectedAudience { get; set; } = string.Empty;

    public Func<HttpContext, bool>? ShouldValidate { get; set; }

    public string Realm { get; set; } = "SqlOS API";

    public string? ResourceMetadataUrl { get; set; }
}
