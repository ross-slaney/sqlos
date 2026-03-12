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
using SqlOS.Example.Api.Seeding;
using SqlOS.Example.Api.Services;
using SqlOS.Extensions;

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

builder.Services.AddSqlOS<ExampleAppDbContext>(options =>
{
    options.DashboardBasePath = "/sqlos";
    options.UseAuthServer(auth =>
    {
        auth.BasePath = "/sqlos/auth";
        auth.Issuer = builder.Configuration["SqlOS:Issuer"] ?? "https://localhost/sqlos/auth";
    });
    options.UseFGA(fga =>
    {
        fga.DashboardPathPrefix = "/sqlos/admin/fga";
    });
});

builder.Services.AddScoped<ExampleSeedService>();
builder.Services.AddScoped<ExampleFgaService>();
builder.Services.AddScoped<RetailSeedService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("example-frontend", policy =>
    {
        var origin = builder.Configuration["ExampleFrontend:Origin"] ?? "http://localhost:3000";
        policy.WithOrigins(origin)
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

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ExampleAppDbContext>();
    await EnsureLegacyEnsureCreatedDatabasesCanMigrateAsync(context);
    await context.Database.MigrateAsync();
}

await app.UseSqlOSAsync();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ExampleSeedService>().SeedAsync();
    await scope.ServiceProvider.GetRequiredService<RetailSeedService>().SeedAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("example-frontend");
app.UseExampleBearerTokenMiddleware();
app.UseSqlOSDashboard("/sqlos");

app.MapAuthServer("/sqlos/auth");
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
