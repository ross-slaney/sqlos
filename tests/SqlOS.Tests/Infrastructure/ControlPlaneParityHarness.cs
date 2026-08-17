using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;

namespace SqlOS.Tests.Infrastructure;

internal static class DashboardAdminContracts
{
    public const string Clients = "/sqlos/admin/auth/api/clients";
    public static string ClientCredentials(string clientId)
        => $"/sqlos/admin/auth/api/clients/{clientId}/credentials";
    public const string OidcConnections = "/sqlos/admin/auth/api/oidc-connections";
    public const string MfaSettings = "/sqlos/admin/auth/api/settings/mfa";
    public const string AuthPageSettings = "/sqlos/admin/auth/api/settings/auth-page";
    public const string MachineClients = "/sqlos/admin/auth/api/machine-clients";
    public static string OrganizationScimConnections(string organizationId)
        => $"/sqlos/admin/auth/api/organizations/{organizationId}/scim-connections";
    public static string MachineClientRevoke(string clientId)
        => $"{MachineClients}/{Uri.EscapeDataString(clientId)}/revoke";
    public static string MachineClientEmergencyDisable(string clientId)
        => $"{MachineClients}/{Uri.EscapeDataString(clientId)}/emergency-disable";
    public static string MachineClientEmergencyEnable(string clientId)
        => $"{MachineClients}/{Uri.EscapeDataString(clientId)}/emergency-enable";
}

internal sealed record ParityProjection(
    string Capability,
    IReadOnlyDictionary<string, string?> Configuration,
    string Owner,
    bool IsEditable,
    bool IsEnabled,
    bool SecretIsRedacted);

/// <summary>
/// Small production-backed host used by three-control-plane parity tests. It deliberately calls
/// real startup reconcilers, public administration services, and the same HTTP routes used by
/// the dashboard; it does not reimplement configuration behavior for tests.
/// </summary>
internal sealed class ControlPlaneParityHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IServiceScope _scope;

    private ControlPlaneParityHarness(WebApplication app, IServiceScope scope)
    {
        _app = app;
        _scope = scope;
        Client = app.GetTestClient();
        Context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
        Options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlOSAuthServerOptions>>().Value;
        Admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
        ClientAuthentication = scope.ServiceProvider.GetRequiredService<SqlOSClientAuthenticationService>();
        Settings = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
        Oidc = scope.ServiceProvider.GetRequiredService<SqlOSOidcAuthService>();
        Scim = scope.ServiceProvider.GetRequiredService<SqlOSScimService>();
        Machines = scope.ServiceProvider.GetRequiredService<SqlOSMachineClientAdminService>();
        ClientCredentials = scope.ServiceProvider.GetRequiredService<SqlOSClientCredentialsService>();
        Crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
    }

    public HttpClient Client { get; }
    public TestSqlOSInMemoryDbContext Context { get; }
    public SqlOSAuthServerOptions Options { get; }
    public SqlOSAdminService Admin { get; }
    public SqlOSClientAuthenticationService ClientAuthentication { get; }
    public SqlOSSettingsService Settings { get; }
    public SqlOSOidcAuthService Oidc { get; }
    public SqlOSScimService Scim { get; }
    public SqlOSMachineClientAdminService Machines { get; }
    public SqlOSClientCredentialsService ClientCredentials { get; }
    public SqlOSCryptoService Crypto { get; }

    public static async Task<ControlPlaneParityHarness> CreateAsync(Action<SqlOSAuthServerOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        var databaseName = $"parity-{Guid.NewGuid():N}";
        builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(database => database.UseInMemoryDatabase(databaseName));
        builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
        {
            options.AuthServer.Issuer = "https://auth.parity.test/sqlos/auth";
            options.AuthServer.PublicOrigin = "https://auth.parity.test";
            options.AuthServer.EnableScim = true;
            configure?.Invoke(options.AuthServer);
        });
        builder.Services.RemoveAll<IHostedService>();

        var app = builder.Build();
        app.MapSqlOS();
        await app.StartAsync();
        var scope = app.Services.CreateScope();
        return new ControlPlaneParityHarness(app, scope);
    }

    public async Task ReconcileStartupAsync()
    {
        await Settings.EnsureDefaultMfaSettingsAsync();
        await Settings.UpsertSeededMfaSettingsAsync();
        await Admin.UpsertSeededClientsAsync();
        await Machines.UpsertSeededMachineClientsAsync();
        await Admin.UpsertSeededOidcConnectionsAsync();
        await Admin.UpsertSeededScimConnectionsAsync();
    }

    public async Task<JsonElement> PostDashboardAsync(string route, object payload)
    {
        using var response = await Client.PostAsJsonAsync(route, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    public async Task<JsonElement> PutDashboardAsync(string route, object payload)
    {
        using var response = await Client.PutAsJsonAsync(route, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    public async Task<ParityProjection> ProjectClientAsync(string clientId)
    {
        var item = await Context.Set<SqlOSClientApplication>().AsNoTracking().SingleAsync(x => x.ClientId == clientId);
        var now = DateTime.UtcNow;
        var activeCredentialCount = await Context.Set<SqlOSClientCredential>().AsNoTracking()
            .CountAsync(x => x.ClientApplicationId == item.Id
                && x.RevokedAt == null
                && (x.ExpiresAt == null || x.ExpiresAt > now));
        var response = await Client.GetFromJsonAsync<JsonElement>($"{DashboardAdminContracts.Clients}?search={Uri.EscapeDataString(clientId)}&page=1&pageSize=10");
        var dashboardItem = response.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("clientId").GetString() == clientId);
        return new ParityProjection("oauth_client", new Dictionary<string, string?>
        {
            ["clientId"] = item.ClientId,
            ["name"] = item.Name,
            ["audience"] = item.Audience,
            ["redirectUris"] = CanonicalJsonArray(item.RedirectUrisJson),
            ["scopes"] = CanonicalJsonArray(item.AllowedScopesJson),
            ["pkce"] = item.RequirePkce.ToString(),
            ["clientType"] = item.ClientType,
            ["tokenEndpointAuthMethod"] = item.TokenEndpointAuthMethod,
            ["activeCredentialCount"] = activeCredentialCount.ToString()
        }, item.ConfigurationOwner, ReadEditable(dashboardItem), item.IsActive, true);
    }

    public async Task<ParityProjection> ProjectOidcAsync(string displayName)
    {
        var item = await Context.Set<SqlOSOidcConnection>().AsNoTracking().SingleAsync(x => x.DisplayName == displayName);
        var response = await Client.GetFromJsonAsync<JsonElement>(DashboardAdminContracts.OidcConnections);
        var dashboardItem = response.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("displayName").GetString() == displayName);
        return new ParityProjection("oidc_connection", new Dictionary<string, string?>
        {
            ["displayName"] = item.DisplayName,
            ["provider"] = item.ProviderType.ToString(),
            ["clientId"] = item.ClientId,
            ["discoveryUrl"] = item.DiscoveryUrl,
            ["callbacks"] = CanonicalJsonArray(item.AllowedCallbackUrisJson),
            ["scopes"] = CanonicalJsonArray(item.ScopesJson)
        }, item.ConfigurationOwner, ReadEditable(dashboardItem), item.IsEnabled,
            !string.IsNullOrWhiteSpace(item.ClientSecretEncrypted));
    }

    public async Task<ParityProjection> ProjectScimAsync(string organizationId)
    {
        var item = await Context.Set<SqlOSScimConnection>().AsNoTracking().Include(x => x.Organization).SingleAsync(x => x.OrganizationId == organizationId);
        var response = await Client.GetFromJsonAsync<JsonElement>(DashboardAdminContracts.OrganizationScimConnections(organizationId));
        var dashboardItem = response.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("id").GetString() == item.Id);
        return new ParityProjection("scim_connection", new Dictionary<string, string?>
        {
            ["organization"] = item.Organization?.Slug,
            ["displayName"] = item.DisplayName
        }, item.ConfigurationOwner, ReadEditable(dashboardItem), item.IsEnabled,
            item.TokenHash != null && item.TokenPrefix != null);
    }

    public async Task<ParityProjection> ProjectMachineClientAsync(string clientId)
    {
        var account = await Context.Set<SqlOS.Fga.Models.SqlOSFgaServiceAccount>().AsNoTracking()
            .Include(x => x.Subject)
            .SingleAsync(x => x.ClientId == clientId);
        var client = await Context.Set<SqlOSClientApplication>().AsNoTracking().SingleAsync(x => x.ClientId == clientId);
        var grantCount = await Context.Set<SqlOS.Fga.Models.SqlOSFgaGrant>().AsNoTracking()
            .CountAsync(x => x.SubjectId == account.SubjectId);
        var response = await Client.GetFromJsonAsync<JsonElement>(DashboardAdminContracts.MachineClients);
        var dashboardItem = response.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("clientId").GetString() == clientId);
        return new ParityProjection("machine_client", new Dictionary<string, string?>
        {
            ["clientId"] = client.ClientId,
            ["displayName"] = account.Subject!.DisplayName,
            ["audience"] = client.Audience,
            ["scopes"] = CanonicalJsonArray(client.AllowedScopesJson),
            ["organization"] = account.Subject.OrganizationId,
            ["grantCount"] = grantCount.ToString(),
            ["emergencyDisabled"] = (client.DisabledAt != null
                && string.Equals(client.DisabledReason, SqlOSMachineClientAdminService.EmergencyDisabledReason, StringComparison.Ordinal)).ToString()
        }, account.ConfigurationOwner, ReadEditable(dashboardItem),
            client.IsActive && client.DisabledAt == null && (account.ExpiresAt == null || account.ExpiresAt > DateTime.UtcNow),
            true);
    }

    public async Task<ParityProjection> ProjectMfaAsync()
    {
        var item = await Context.Set<SqlOSMfaSettings>().AsNoTracking().SingleAsync(x => x.Id == "default");
        var dashboardItem = await Client.GetFromJsonAsync<JsonElement>(DashboardAdminContracts.MfaSettings);
        return new ParityProjection("mfa_settings", new Dictionary<string, string?>
        {
            ["totp"] = item.TotpEnabled.ToString(),
            ["selfEnrollment"] = item.UserSelfEnrollmentEnabled.ToString(),
            ["recoveryCodes"] = item.RecoveryCodesEnabled.ToString(),
            ["requireAll"] = item.RequireForAllUsers.ToString(),
            ["requirePrivileged"] = item.RequireForOwnersAndAdmins.ToString(),
            ["roles"] = CanonicalJsonArray(item.RequiredRolesJson),
            ["factors"] = CanonicalJsonArray(item.AvailableFactorsJson)
        }, item.ConfigurationOwner, ReadEditable(dashboardItem), item.Enabled, true);
    }

    public async Task<ParityProjection> ProjectAuthPageAsync()
    {
        var item = await Context.Set<SqlOSAuthPageSettings>().AsNoTracking().SingleAsync(x => x.Id == "default");
        var dashboardItem = await Client.GetFromJsonAsync<JsonElement>(DashboardAdminContracts.AuthPageSettings);
        return new ParityProjection(
            "auth_page_settings",
            new Dictionary<string, string?>
            {
                ["primary"] = item.PrimaryColor,
                ["accent"] = item.AccentColor,
                ["background"] = item.BackgroundColor,
                ["layout"] = item.Layout
            },
            dashboardItem.GetProperty("managedByStartupSeed").GetBoolean()
                ? SqlOSConfigurationOwners.Code
                : SqlOSConfigurationOwners.Dashboard,
            !dashboardItem.GetProperty("managedByStartupSeed").GetBoolean(),
            true,
            true);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _scope.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static string CanonicalJsonArray(string json)
        => JsonSerializer.Serialize((JsonSerializer.Deserialize<string[]>(json) ?? []).Order(StringComparer.Ordinal));

    private static bool ReadEditable(JsonElement item)
        => item.GetProperty("ownership").GetProperty("isEditable").GetBoolean();
}
