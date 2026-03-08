using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Sqlzibar.Example.Api.Data;
using Sqlzibar.Example.Api.Endpoints;
using Sqlzibar.Example.Api.Middleware;
using Sqlzibar.Example.Api.Seeding;
using Sqlzibar.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<RetailDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSqlzibar<RetailDbContext>(o =>
{
    o.RootResourceId = "retail_root";
    o.RootResourceName = "Retail Root";
});

builder.Services.AddScoped<RetailSeedService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Sqlzibar Retail Example API", Version = "v1" });
    c.AddSecurityDefinition("SubjectId", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Subject-Id",
        Description = "The subject ID to use for authorization (e.g. subj_company_admin, subj_chain_mgr_walmart, subj_store_mgr_001)"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "SubjectId" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Create example app's domain tables (EnsureCreated must run before UseSqlzibarAsync
// so it can create tables when the database is empty; UseSqlzibarAsync's raw SQL
// table creation would cause HasTables() to return true, skipping domain tables)
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
    await ctx.Database.EnsureCreatedAsync();
}

// Initialize Sqlzibar schema + TVF + core seed data
await app.UseSqlzibarAsync();

// Seed retail sample data
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<RetailSeedService>().SeedAsync();
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseSubjectIdMiddleware();
app.UseSqlzibarDashboard("/sqlzibar");

app.MapChainEndpoints();
app.MapLocationEndpoints();
app.MapInventoryEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

public partial class Program { }
