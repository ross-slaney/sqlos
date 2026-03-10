using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SqlOS.Example.Api.Configuration;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.Endpoints;
using SqlOS.Example.Api.FgaRetail.Endpoints;
using SqlOS.Example.Api.FgaRetail.Middleware;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.Api.Middleware;
using SqlOS.Example.Api.Seeding;
using SqlOS.Example.Api.Services;
using SqlOS.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExampleWebOptions>(builder.Configuration.GetSection("ExampleFrontend"));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

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
    await context.Database.EnsureCreatedAsync();
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
app.UseSubjectIdMiddleware();
app.UseSqlOSDashboard("/sqlos");

app.MapAuthServer("/sqlos/auth");
app.MapExampleAuthEndpoints();
app.MapExampleEndpoints();
app.MapChainEndpoints();
app.MapLocationEndpoints();
app.MapInventoryEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

public partial class Program { }
