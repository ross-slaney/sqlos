using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SqlOS.Example.Api.Configuration;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Endpoints;
using SqlOS.Example.Api.FgaRetail.Endpoints;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.Api.Middleware;
using SqlOS.Example.Api.Services;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.Extensions;
using SqlOS.Example.Api.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExampleWebOptions>(builder.Configuration.GetSection("ExampleFrontend"));

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["ConnectionStrings:DefaultConnection"]
    ?? builder.Configuration["ConnectionStrings__DefaultConnection"];

if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Connection string 'DefaultConnection' was not configured.");

builder.Services.AddDbContext<ExampleAppDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.AddSqlOS<ExampleAppDbContext>(options =>
{
    options.DashboardBasePath = "/sqlos";
    var exampleClientId = builder.Configuration["ExampleFrontend:ClientId"] ?? "example-web";
    var exampleCallbackUrl = builder.Configuration["ExampleFrontend:CallbackUrl"] ?? "http://localhost:3000/auth/callback";

    if (Enum.TryParse<SqlOSDashboardAuthMode>(builder.Configuration["SqlOS:Dashboard:AuthMode"], ignoreCase: true, out var authMode))
    {
        options.Dashboard.AuthMode = authMode;
    }

    var dashboardPassword = builder.Configuration["SqlOS:Dashboard:Password"];
    if (!string.IsNullOrWhiteSpace(dashboardPassword))
    {
        options.Dashboard.Password = dashboardPassword;
    }

    var sessionMinutes = builder.Configuration.GetValue<int?>("SqlOS:Dashboard:SessionLifetimeMinutes");
    if (sessionMinutes is > 0)
    {
        options.Dashboard.SessionLifetime = TimeSpan.FromMinutes(sessionMinutes.Value);
    }

    var auth = options.AuthServer;
    auth.Issuer = builder.Configuration["SqlOS:Issuer"] ?? "https://localhost/sqlos/auth";
    auth.DefaultSigningKeyRotationIntervalDays = 90;
    auth.DefaultSigningKeyGraceWindowDays = 7;
    var headlessFrontendUrl = builder.Configuration["SqlOS:HeadlessFrontendUrl"]
        ?? builder.Configuration["ExampleFrontend:Origin"]
        ?? "http://localhost:3000";

    auth.SeedAuthPage(page =>
    {
        page.PageTitle = "Sign in";
        page.PageSubtitle = "Use the hosted SqlOS auth page to sign in to the example application.";
        page.PrimaryColor = "#2563eb";
        page.AccentColor = "#0f172a";
        page.BackgroundColor = "#f8fafc";
        page.Layout = "split";
        page.EnablePasswordSignup = true;
        page.EnabledCredentialTypes = ["password"];
    });
    auth.SeedBrowserClient(
        exampleClientId,
        "Example Web Client",
        exampleCallbackUrl,
        "https://client.example.local/callback",
        "sqlos-expo://auth-callback");
    auth.SeedBrowserClient(
        "example-angular",
        "Example Angular Client",
        "http://localhost:4200/auth/callback");
    auth.SeedBrowserClient(
        "example-expo",
        "Example Expo Client",
        "sqlos-expo://auth-callback");

    auth.UseHeadlessAuthPage(headless =>
    {
        headless.BuildUiUrl = ctx =>
        {
            var origin = ctx.HttpContext.Items["HeadlessFrontendOrigin"] as string
                ?? headlessFrontendUrl;
            var query = new Dictionary<string, string?>
            {
                ["request"] = ctx.RequestId,
                ["view"] = ctx.View,
                ["error"] = ctx.Error,
                ["email"] = ctx.Email,
                ["pendingToken"] = ctx.PendingToken,
                ["displayName"] = ctx.DisplayName,
            };
            return QueryHelpers.AddQueryString(
                $"{origin.TrimEnd('/')}/auth/authorize", query);
        };

        headless.OnHeadlessSignupAsync = async (ctx, cancellationToken) =>
        {
            var logger = ctx.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("HeadlessSignup");
            var dbContext = ctx.HttpContext.RequestServices.GetRequiredService<ExampleAppDbContext>();

            var firstName = ctx.CustomFields?["firstName"]?.GetValue<string>();
            var lastName = ctx.CustomFields?["lastName"]?.GetValue<string>();
            var referralSource = ctx.CustomFields?["referralSource"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(referralSource))
            {
                throw new SqlOSHeadlessValidationException(
                    "Tell us how you heard about SqlOS.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["referralSource"] = "Select a referral source to complete signup."
                    });
            }

            var profile = await dbContext.ExampleUserProfiles
                .FirstOrDefaultAsync(x => x.SqlOSUserId == ctx.User.Id, cancellationToken);

            if (profile == null)
            {
                profile = new ExampleUserProfile
                {
                    SqlOSUserId = ctx.User.Id,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.ExampleUserProfiles.Add(profile);
            }

            profile.DefaultEmail = ctx.User.DefaultEmail ?? string.Empty;
            profile.DisplayName = ctx.User.DisplayName ?? ctx.User.DefaultEmail ?? "Example User";
            profile.OrganizationId = ctx.Organization?.Id;
            profile.OrganizationName = ctx.Organization?.Name;
            profile.ReferralSource = referralSource!;
            profile.UpdatedAt = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Headless signup completed: {Email}, Org={OrgName}, FirstName={First}, LastName={Last}, Referral={Referral}",
                ctx.User.DefaultEmail,
                ctx.Organization?.Name,
                firstName,
                lastName,
                referralSource);
        };
    });

    options.Fga.Seed(seed =>
    {
        seed.ResourceType(
            ExampleFgaService.OrganizationResourceTypeId,
            "Organization",
            "Organization root for example workspace access.");
        seed.ResourceType(
            ExampleFgaService.WorkspaceResourceTypeId,
            "Workspace",
            "Workspace resources in the example application.");
        seed.Permission(
            "perm_workspace_view",
            ExampleFgaService.WorkspaceViewPermission,
            "View workspaces",
            ExampleFgaService.WorkspaceResourceTypeId);
        seed.Permission(
            "perm_workspace_manage",
            ExampleFgaService.WorkspaceManagePermission,
            "Manage workspaces",
            ExampleFgaService.WorkspaceResourceTypeId);
        seed.Role(
            "role_org_member",
            ExampleFgaService.OrgMemberRole,
            "Organization Member");
        seed.Role(
            "role_org_admin",
            ExampleFgaService.OrgAdminRole,
            "Organization Admin");
        seed.RolePermission(ExampleFgaService.OrgMemberRole, ExampleFgaService.WorkspaceViewPermission);
        seed.RolePermission(ExampleFgaService.OrgAdminRole, ExampleFgaService.WorkspaceViewPermission);
        seed.RolePermission(ExampleFgaService.OrgAdminRole, ExampleFgaService.WorkspaceManagePermission);
    });
});

builder.Services.AddScoped<ExampleFgaService>();
builder.Services.AddScoped<RetailSeedService>();
builder.Services.AddHostedService<ExampleRetailSeedHostedService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("example-frontend", policy =>
    {
        var origin = builder.Configuration["ExampleFrontend:Origin"] ?? "http://localhost:3000";
        policy.WithOrigins(origin, "http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SqlOS Example API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Bearer token for example protected endpoints",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});

var app = builder.Build();
app.MapSqlOS();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ExampleAppDbContext>();
    await EnsureLegacyEnsureCreatedDatabasesCanMigrateAsync(context);
    await context.Database.MigrateAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("example-frontend");
app.UseExampleBearerTokenMiddleware();
// --- External API: validating SqlOS JWTs via JWKS endpoint ---
// Use this pattern when your API is a SEPARATE process from the SqlOS host
// and needs to validate tokens issued by SqlOS. Key rotation is handled
// automatically — ASP.NET's JwtBearer handler re-fetches the JWKS endpoint
// when it encounters an unknown kid, so new signing keys are picked up seamlessly.
//
// builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
//     .AddJwtBearer(options =>
//     {
//         options.Authority = "https://your-sqlos-host/sqlos/auth";
//         options.MetadataAddress = "https://your-sqlos-host/sqlos/auth/.well-known/oauth-authorization-server";
//         options.TokenValidationParameters = new TokenValidationParameters
//         {
//             ValidateIssuer = true,
//             ValidIssuer = "https://your-sqlos-host/sqlos/auth",
//             ValidateAudience = true,
//             ValidAudience = "sqlos",
//             ValidateLifetime = true,
//         };
//         options.RefreshInterval = TimeSpan.FromHours(1); // optional: more frequent background JWKS refresh
//     });
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/sqlos/auth/authorize")
        && context.Request.Query.TryGetValue("redirect_uri", out var redirectUri)
        && Uri.TryCreate(redirectUri.ToString(), UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        context.Items["HeadlessFrontendOrigin"] = uri.GetLeftPart(UriPartial.Authority);
    }
    await next();
});

app.MapExampleAuthEndpoints();
app.MapExampleEndpoints();
app.MapDemoEndpoints();
app.MapChainEndpoints();
app.MapLocationEndpoints();
app.MapInventoryEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

static async Task EnsureLegacyEnsureCreatedDatabasesCanMigrateAsync(ExampleAppDbContext context, CancellationToken cancellationToken = default)
{
    if (!context.Database.IsSqlServer())
        return;

    var allMigrations = context.Database.GetMigrations().ToList();
    if (allMigrations.Count == 0)
        return;

    var connection = context.Database.GetDbConnection();
    var openedConnection = connection.State != ConnectionState.Open;
    if (openedConnection)
        await connection.OpenAsync(cancellationToken);

    static async Task<bool> TableExistsAsync(DbConnection connection, string tableName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) 1
            FROM sys.tables
            WHERE name = @tableName
              AND schema_id = SCHEMA_ID('dbo');
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(ct);
        return result is not null && result is not DBNull;
    }

    var hasMigrationHistory = await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken);
    if (hasMigrationHistory)
        return;

    var hasLegacySchema =
        await TableExistsAsync(connection, "SqlOSUsers", cancellationToken) ||
        await TableExistsAsync(connection, "Chains", cancellationToken) ||
        await TableExistsAsync(connection, "Locations", cancellationToken) ||
        await TableExistsAsync(connection, "InventoryItems", cancellationToken);

    if (!hasLegacySchema)
        return;

    var baselineMigrationId = allMigrations[0];
    const string efVersion = "9.0.0";

    await context.Database.ExecuteSqlRawAsync(
        """
        IF OBJECT_ID(N'[dbo].[__EFMigrationsHistory]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[__EFMigrationsHistory] (
                [MigrationId] nvarchar(150) NOT NULL,
                [ProductVersion] nvarchar(32) NOT NULL,
                CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
            );
        END

        IF NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = {0})
        BEGIN
            INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
            VALUES ({0}, {1});
        END
        """,
        baselineMigrationId,
        efVersion);

    if (openedConnection)
        await connection.CloseAsync();
}

public partial class Program { }
