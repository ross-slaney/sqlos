using Microsoft.AspNetCore.Http;
using SqlOS.Configuration;

namespace SqlOS.AuthServer.Configuration;

public class SqlOSAuthServerOptions
{
    public string Schema { get; set; } = "dbo";
    public string BasePath { get; set; } = "/sqlos/auth";
    public string Issuer { get; set; } = "https://localhost/sqlos/auth";
    public string? PublicOrigin { get; set; }
    public string DefaultAudience { get; set; } = "sqlos";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan TemporaryTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan SessionAbsoluteLifetime { get; set; } = TimeSpan.FromDays(30);
    public bool RequireVerifiedEmailForPasswordLogin { get; set; }
    public bool EnableLocalPasswordAuth { get; set; } = true;
    public bool EnableSaml { get; set; } = true;
    public int DefaultSigningKeyRotationIntervalDays { get; set; } = 90;
    public int DefaultSigningKeyGraceWindowDays { get; set; } = 7;
    public int DefaultSigningKeyRetiredCleanupDays { get; set; } = 30;
    public SqlOSDashboardOptions Dashboard { get; set; } = new();
    public SqlOSHeadlessAuthOptions Headless { get; } = new();
    public SqlOSAuthPageSeedOptions? AuthPageSeed { get; private set; }
    public List<SqlOSClientSeedOptions> ClientSeeds { get; } = [];

    public SqlOSAuthServerOptions UseHeadlessAuthPage(Action<SqlOSHeadlessAuthOptions> configure)
    {
        configure(Headless);
        return this;
    }

    public SqlOSAuthServerOptions SeedAuthPage(Action<SqlOSAuthPageSeedOptions> configure)
    {
        var seed = AuthPageSeed ?? new SqlOSAuthPageSeedOptions();
        configure(seed);
        AuthPageSeed = seed;
        return this;
    }

    public SqlOSAuthServerOptions SeedClient(Action<SqlOSClientSeedOptions> configure)
    {
        var seed = new SqlOSClientSeedOptions();
        configure(seed);
        ClientSeeds.Add(seed);
        return this;
    }

    public SqlOSAuthServerOptions SeedBrowserClient(string clientId, string name, params string[] redirectUris)
    {
        SeedClient(client =>
        {
            client.ClientId = clientId;
            client.Name = name;
            client.RedirectUris = redirectUris
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Select(static uri => uri.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            client.ClientType = "public_pkce";
            client.RequirePkce = true;
            client.IsFirstParty = true;
        });

        return this;
    }
}
