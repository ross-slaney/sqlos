using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Todo.Api.Configuration;
using SqlOS.Todo.Api.Data;
using SqlOS.Todo.Api.Models;
using SqlOS.Todo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TodoSampleOptions>(builder.Configuration.GetSection("TodoSample"));

var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["ConnectionStrings:DefaultConnection"]
    ?? builder.Configuration["ConnectionStrings__DefaultConnection"];

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'DefaultConnection' was not configured.");
}

builder.Services.AddDbContext<TodoSampleDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped<TodoFgaService>();
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.WriteIndented = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SqlOS Todo API",
        Version = "v1",
        Description = "Hosted-first Todo sample with SqlOS auth, protected-resource metadata, and MCP-oriented client onboarding examples."
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Bearer token minted for the Todo resource. Use /sample/config and /.well-known/oauth-protected-resource to discover the audience and auth server.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });
});

var sampleConfig = builder.Configuration.GetSection("TodoSample").Get<TodoSampleOptions>() ?? new TodoSampleOptions();
var publicOrigin = sampleConfig.PublicOrigin.TrimEnd('/');
var hostedCallbackUrl = $"{publicOrigin}/callback.html";
var localClientRedirectUri = "http://localhost:3100/oauth/callback";
var portableClientUrl = $"{publicOrigin}{sampleConfig.PortableClientPath}";
var cimdTrustedHosts = sampleConfig.CimdTrustedHosts
    .Concat([new Uri(publicOrigin).Host])
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

builder.AddSqlOS<TodoSampleDbContext>(options =>
{
    options.DashboardBasePath = "/sqlos";

    var auth = options.AuthServer;
    auth.Issuer = builder.Configuration["SqlOS:Issuer"] ?? $"{publicOrigin}/sqlos/auth";
    auth.PublicOrigin = publicOrigin;
    auth.DefaultAudience = sampleConfig.Resource;

    auth.SeedAuthPage(page =>
    {
        page.PageTitle = "Ship the Todo app first.";
        page.PageSubtitle = "Start with hosted auth, then graduate to headless and public-client onboarding when you need it.";
        page.PrimaryColor = "#0f172a";
        page.AccentColor = "#2563eb";
        page.BackgroundColor = "#f8fafc";
        page.Layout = "split";
        page.EnablePasswordSignup = true;
        page.EnabledCredentialTypes = ["password"];
    });

    auth.SeedClient(client =>
    {
        client.ClientId = sampleConfig.HostedClientId;
        client.Name = sampleConfig.HostedClientName;
        client.Description = "Hosted-first web client for the SqlOS Todo sample.";
        client.Audience = sampleConfig.Resource;
        client.RedirectUris = [hostedCallbackUrl];
        client.AllowedScopes = sampleConfig.AllowedScopes;
        client.ClientType = "public_pkce";
        client.RequirePkce = true;
        client.IsFirstParty = true;
    });

    auth.SeedClient(client =>
    {
        client.ClientId = sampleConfig.LocalClientId;
        client.Name = "Todo Local";
        client.Description = "Local preregistered client for localhost MCP development against the Todo sample.";
        client.Audience = sampleConfig.Resource;
        client.RedirectUris = [localClientRedirectUri];
        client.AllowedScopes = sampleConfig.AllowedScopes;
        client.ClientType = "public_pkce";
        client.RequirePkce = true;
        client.IsFirstParty = false;
    });

    options.Fga.Seed(seed =>
    {
        seed.ResourceType(
            TodoFgaService.TenantResourceTypeId,
            "Tenant",
            "Per-user tenant root for the Todo sample.");
        seed.ResourceType(
            TodoFgaService.TodoResourceTypeId,
            "Todo",
            "Individual todo items in the Todo sample.");
        seed.Permission(
            "perm_tenant_create_todo",
            TodoFgaService.TenantCreateTodoPermission,
            "Create todos",
            TodoFgaService.TenantResourceTypeId);
        seed.Permission(
            "perm_todo_read",
            TodoFgaService.TodoReadPermission,
            "Read todos",
            TodoFgaService.TodoResourceTypeId);
        seed.Permission(
            "perm_todo_write",
            TodoFgaService.TodoWritePermission,
            "Write todos",
            TodoFgaService.TodoResourceTypeId);
        seed.Role(
            "role_tenant_owner",
            TodoFgaService.TenantOwnerRole,
            "Tenant Owner",
            "Single-user owner role for the Todo sample.");
        seed.RolePermission(TodoFgaService.TenantOwnerRole, TodoFgaService.TenantCreateTodoPermission);
        seed.RolePermission(TodoFgaService.TenantOwnerRole, TodoFgaService.TodoReadPermission);
        seed.RolePermission(TodoFgaService.TenantOwnerRole, TodoFgaService.TodoWritePermission);
    });

    auth.EnablePortableMcpClients(registration =>
    {
        foreach (var host in cimdTrustedHosts)
        {
            registration.Cimd.TrustedHosts.Add(host);
        }
    });

    if (sampleConfig.EnableDcr)
    {
        auth.ClientRegistration.Dcr.Enabled = true;
        auth.ClientRegistration.Dcr.AllowHttpsRedirectUris = true;
        auth.ClientRegistration.Dcr.AllowLoopbackRedirectUris = true;
    }

    if (sampleConfig.EnableHeadless)
    {
        auth.UseHeadlessAuthPage(headless =>
        {
            headless.BuildUiUrl = ctx => QueryHelpers.AddQueryString(
                $"{publicOrigin}{sampleConfig.HeadlessUiPath}",
                new Dictionary<string, string?>
                {
                    ["request"] = ctx.RequestId,
                    ["view"] = ctx.View,
                    ["error"] = ctx.Error,
                    ["email"] = ctx.Email,
                    ["pendingToken"] = ctx.PendingToken,
                    ["displayName"] = ctx.DisplayName
                });
        });
    }
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSqlOS();

app.MapGet("/sample/config", (IOptions<TodoSampleOptions> sampleOptions, IOptions<SqlOSAuthServerOptions> authOptions) =>
{
    var sample = sampleOptions.Value;
    var sampleOrigin = sample.PublicOrigin.TrimEnd('/');
    var hostedCallback = $"{sampleOrigin}/callback.html";
    var protectedResourceMetadata = $"{sampleOrigin}/.well-known/oauth-protected-resource";
    return Results.Ok(new
    {
        publicOrigin = sampleOrigin,
        issuer = authOptions.Value.Issuer,
        resource = sample.Resource,
        protectedResourceMetadata,
        hostedClient = new
        {
            clientId = sample.HostedClientId,
            redirectUri = hostedCallback
        },
        localClient = new
        {
            clientId = sample.LocalClientId,
            redirectUri = localClientRedirectUri
        },
        portableClient = new
        {
            clientId = $"{sampleOrigin}{sample.PortableClientPath}",
            redirectUri = sample.PortableClientRedirectUri
        },
        headlessEnabled = sample.EnableHeadless,
        dcrEnabled = authOptions.Value.ClientRegistration.Dcr.Enabled,
        cimdEnabled = authOptions.Value.ClientRegistration.Cimd.Enabled,
        allowedScopes = sample.AllowedScopes
    });
});

app.MapGet("/.well-known/oauth-protected-resource", (IOptions<TodoSampleOptions> sampleOptions, IOptions<SqlOSAuthServerOptions> authOptions) =>
{
    var sample = sampleOptions.Value;
    var payload = new Dictionary<string, object?>
    {
        ["resource"] = sample.Resource,
        ["authorization_servers"] = new[] { authOptions.Value.Issuer },
        ["scopes_supported"] = sample.AllowedScopes,
        ["bearer_methods_supported"] = new[] { "header" },
        ["resource_documentation"] = $"{sample.PublicOrigin.TrimEnd('/')}/"
    };

    return Results.Text(JsonSerializer.Serialize(payload), "application/json");
});

app.MapGet(sampleConfig.PortableClientPath, (IOptions<TodoSampleOptions> sampleOptions) =>
{
    var sample = sampleOptions.Value;
    var sampleOrigin = sample.PublicOrigin.TrimEnd('/');
    var payload = new Dictionary<string, object?>
    {
        ["client_id"] = $"{sampleOrigin}{sample.PortableClientPath}",
        ["client_name"] = sample.PortableClientName,
        ["redirect_uris"] = new[] { sample.PortableClientRedirectUri },
        ["grant_types"] = new[] { "authorization_code", "refresh_token" },
        ["response_types"] = new[] { "code" },
        ["token_endpoint_auth_method"] = "none",
        ["scope"] = string.Join(' ', sample.AllowedScopes),
        ["client_uri"] = sample.PortableClientUri,
        ["software_id"] = sample.PortableSoftwareId,
        ["software_version"] = sample.PortableSoftwareVersion
    };

    return Results.Text(JsonSerializer.Serialize(payload), "application/json");
});

app.MapGet("/api/todos", async (
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoFgaService todoFgaService,
    ISqlOSFgaAuthService fgaAuthService,
    IOptions<TodoSampleOptions> sampleOptions,
    TodoSampleDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    var authResult = await RequireTodoContextAsync(httpContext, authService, todoFgaService, sampleOptions.Value, cancellationToken);
    if (authResult.Error is not null)
    {
        return authResult.Error;
    }

    var todoContext = authResult.Context!;
    var filter = await fgaAuthService.GetAuthorizationFilterAsync<TodoItem>(
        todoContext.SubjectId,
        TodoFgaService.TodoReadPermission);

    var items = await dbContext.TodoItems
        .AsNoTracking()
        .Where(filter)
        .OrderBy(x => x.IsCompleted)
        .ThenBy(x => x.CreatedAt)
        .Select(x => new
        {
            x.Id,
            x.ResourceId,
            x.Title,
            x.IsCompleted,
            x.CreatedAt,
            x.CompletedAt
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(new
    {
        resource = sampleOptions.Value.Resource,
        audience = todoContext.ValidatedToken.Audience,
        userId = todoContext.ValidatedToken.UserId,
        organizationId = todoContext.ValidatedToken.OrganizationId,
        items
    });
});

app.MapPost("/api/todos", async (
    CreateTodoRequest request,
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoFgaService todoFgaService,
    ISqlOSFgaAuthService fgaAuthService,
    IOptions<TodoSampleOptions> sampleOptions,
    TodoSampleDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    var authResult = await RequireTodoContextAsync(httpContext, authService, todoFgaService, sampleOptions.Value, cancellationToken);
    if (authResult.Error is not null)
    {
        return authResult.Error;
    }

    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Title is required." });
    }

    var todoContext = authResult.Context!;
    var access = await fgaAuthService.CheckAccessAsync(
        todoContext.SubjectId,
        TodoFgaService.TenantCreateTodoPermission,
        todoContext.TenantResourceId);
    if (!access.Allowed)
    {
        return CreatePermissionDenied();
    }

    var item = new TodoItem
    {
        Id = Guid.NewGuid(),
        SqlOSUserId = todoContext.SubjectId,
        Title = request.Title.Trim(),
        CreatedAt = DateTime.UtcNow
    };
    item.ResourceId = todoFgaService.CreateTodoResource(item, todoContext.TenantResourceId);

    dbContext.TodoItems.Add(item);
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        item.Id,
        item.ResourceId,
        item.Title,
        item.IsCompleted,
        item.CreatedAt
    });
});

app.MapPost("/api/todos/{id:guid}/toggle", async (
    Guid id,
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoFgaService todoFgaService,
    ISqlOSFgaAuthService fgaAuthService,
    IOptions<TodoSampleOptions> sampleOptions,
    TodoSampleDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    var authResult = await RequireTodoContextAsync(httpContext, authService, todoFgaService, sampleOptions.Value, cancellationToken);
    if (authResult.Error is not null)
    {
        return authResult.Error;
    }

    var item = await dbContext.TodoItems
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (item == null)
    {
        return Results.NotFound();
    }

    var todoContext = authResult.Context!;
    var access = await fgaAuthService.CheckAccessAsync(
        todoContext.SubjectId,
        TodoFgaService.TodoWritePermission,
        item.ResourceId);
    if (!access.Allowed)
    {
        return CreatePermissionDenied();
    }

    item.IsCompleted = !item.IsCompleted;
    item.CompletedAt = item.IsCompleted ? DateTime.UtcNow : null;
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        item.Id,
        item.ResourceId,
        item.Title,
        item.IsCompleted,
        item.CompletedAt
    });
});

app.MapDelete("/api/todos/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoFgaService todoFgaService,
    ISqlOSFgaAuthService fgaAuthService,
    IOptions<TodoSampleOptions> sampleOptions,
    TodoSampleDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    var authResult = await RequireTodoContextAsync(httpContext, authService, todoFgaService, sampleOptions.Value, cancellationToken);
    if (authResult.Error is not null)
    {
        return authResult.Error;
    }

    var item = await dbContext.TodoItems
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (item == null)
    {
        return Results.NotFound();
    }

    var todoContext = authResult.Context!;
    var access = await fgaAuthService.CheckAccessAsync(
        todoContext.SubjectId,
        TodoFgaService.TodoWritePermission,
        item.ResourceId);
    if (!access.Allowed)
    {
        return CreatePermissionDenied();
    }

    await todoFgaService.RemoveTodoResourceAsync(item.ResourceId, cancellationToken);
    dbContext.TodoItems.Remove(item);
    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/me", async (
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoFgaService todoFgaService,
    IOptions<TodoSampleOptions> sampleOptions,
    CancellationToken cancellationToken) =>
{
    var authResult = await RequireTodoContextAsync(httpContext, authService, todoFgaService, sampleOptions.Value, cancellationToken);
    if (authResult.Error is not null)
    {
        return authResult.Error;
    }

    var todoContext = authResult.Context!;
    return Results.Ok(new
    {
        todoContext.ValidatedToken.UserId,
        todoContext.ValidatedToken.OrganizationId,
        todoContext.ValidatedToken.ClientId,
        audience = todoContext.ValidatedToken.Audience,
        tenantResourceId = todoContext.TenantResourceId,
        claims = todoContext.ValidatedToken.Principal.Claims.Select(x => new { x.Type, x.Value })
    });
});

await EnsureDatabaseAsync(app);
app.Run();

static async Task EnsureDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<TodoSampleDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

static async Task<SqlOSValidatedToken?> TryValidateTodoTokenAsync(
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoSampleOptions sampleOptions,
    CancellationToken cancellationToken)
{
    var authorization = httpContext.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var rawToken = authorization["Bearer ".Length..].Trim();
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        return null;
    }

    return await authService.ValidateAccessTokenAsync(rawToken, sampleOptions.Resource, cancellationToken);
}

static async Task<TodoRequestAuthResult> RequireTodoContextAsync(
    HttpContext httpContext,
    SqlOSAuthService authService,
    TodoFgaService todoFgaService,
    TodoSampleOptions sampleOptions,
    CancellationToken cancellationToken)
{
    var validated = await TryValidateTodoTokenAsync(httpContext, authService, sampleOptions, cancellationToken);
    if (validated == null)
    {
        return TodoRequestAuthResult.Failure(CreateTodoChallenge(httpContext, sampleOptions));
    }

    if (string.IsNullOrWhiteSpace(validated.UserId))
    {
        return TodoRequestAuthResult.Failure(Results.BadRequest(new { error = "Token must include a user subject." }));
    }

    var fgaContext = await todoFgaService.EnsureUserTenantAccessAsync(validated, cancellationToken);
    return TodoRequestAuthResult.Success(new TodoRequestContext(validated, fgaContext.SubjectId, fgaContext.TenantResourceId));
}

static IResult CreateTodoChallenge(HttpContext httpContext, TodoSampleOptions sampleOptions)
{
    var resourceMetadataUrl = $"{sampleOptions.PublicOrigin.TrimEnd('/')}/.well-known/oauth-protected-resource";
    httpContext.Response.Headers.WWWAuthenticate = $"Bearer realm=\"SqlOS Todo API\", resource_metadata=\"{resourceMetadataUrl}\", scope=\"todos.read todos.write\"";
    return Results.Json(new
    {
        error = "invalid_token",
        error_description = "Present a bearer token minted for the Todo resource.",
        resource_metadata = resourceMetadataUrl
    }, statusCode: StatusCodes.Status401Unauthorized);
}

static IResult CreatePermissionDenied()
    => Results.Json(new { error = "Permission denied" }, statusCode: StatusCodes.Status403Forbidden);

public sealed record CreateTodoRequest(string Title);
public sealed record TodoRequestContext(SqlOSValidatedToken ValidatedToken, string SubjectId, string TenantResourceId);
public sealed record TodoRequestAuthResult(TodoRequestContext? Context, IResult? Error)
{
    public static TodoRequestAuthResult Success(TodoRequestContext context) => new(context, null);
    public static TodoRequestAuthResult Failure(IResult error) => new(null, error);
}
public partial class Program { }
