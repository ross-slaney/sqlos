namespace SqlOS.AuthServer.Configuration;

public sealed class SqlOSAuthPageSeedOptions
{
    public string? LogoBase64 { get; set; }
    public string PrimaryColor { get; set; } = "#2563eb";
    public string AccentColor { get; set; } = "#0f172a";
    public string BackgroundColor { get; set; } = "#f8fafc";
    public string Layout { get; set; } = "split";
    public string PageTitle { get; set; } = "Sign in";
    public string PageSubtitle { get; set; } = "Secure your app-owned AI and MCP experiences with SqlOS.";
    public bool EnablePasswordSignup { get; set; } = true;
    public List<string> EnabledCredentialTypes { get; set; } = ["password"];

    /// <summary>
    /// When null, SqlOS infers <c>headless</c> if <see cref="SqlOSAuthServerOptions.Headless"/> has <c>BuildUiUrl</c> configured; otherwise <c>hosted</c>.
    /// </summary>
    public string? PresentationMode { get; set; }
}

public sealed class SqlOSClientSeedOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Audience { get; set; }
    public string ClientType { get; set; } = "public_pkce";
    public bool RequirePkce { get; set; } = true;
    public List<string> AllowedScopes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public bool IsFirstParty { get; set; }
    public bool IsActive { get; set; } = true;
}
