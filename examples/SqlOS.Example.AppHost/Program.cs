using Aspire.Hosting;

// Ports are configurable so the browser e2e tests
// (examples/SqlOS.Example.E2eTests) can boot this same app host on alternate
// ports with an ephemeral SQL container while a manually started demo keeps
// running on the defaults. Everything derived (issuer, frontend origins,
// callback URIs, CORS, seeded clients) flows from these values.
var builder = DistributedApplication.CreateBuilder(args);

var apiPort = GetPort(builder.Configuration["Example:ApiPort"], 5062);
var webPort = GetPort(builder.Configuration["Example:WebPort"], 3010);
var angularPort = GetPort(builder.Configuration["Example:AngularPort"], 4200);
var sqlPort = GetPort(builder.Configuration["Example:SqlPort"], 1434);
var ephemeralSql = string.Equals(
    builder.Configuration["Example:EphemeralSql"], "true", StringComparison.OrdinalIgnoreCase);
// The Todo API and the ASP.NET Core web client are independent of the retail
// example; tests that only need the retail stack skip them.
var includeTodoStack = !string.Equals(
    builder.Configuration["Example:IncludeTodoStack"], "false", StringComparison.OrdinalIgnoreCase);

var apiOrigin = $"http://localhost:{apiPort}";
var webOrigin = $"http://localhost:{webPort}";
var angularOrigin = $"http://localhost:{angularPort}";

const int todoPort = 5080;
var todoOrigin = $"http://localhost:{todoPort}";
var todoResource = $"{todoOrigin}/api/todos";
var todoIssuer = $"{todoOrigin}/sqlos/auth";
var todoEnableDcr = builder.Configuration["TodoSample:EnableDcr"] ?? "false";
var emailConnectionString = builder.Configuration["SqlOS:Email:AzureCommunicationServicesConnectionString"]
    ?? builder.Configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"]
    ?? builder.Configuration["AZURE_EMAIL_CONNECTION_STRING"];
var emailFromAddress = builder.Configuration["SqlOS:Email:FromAddress"]
    ?? builder.Configuration["SqlOS:EmailOtp:FromAddress"]
    ?? builder.Configuration["AZURE_EMAIL_SENDER_ADDRESS"];
var enablePhoneOtp = builder.Configuration["SqlOS:PhoneOtp:Enabled"]
    ?? builder.Configuration["TodoSample:EnablePhoneOtp"];
var twilioAccountSid = builder.Configuration["SqlOS:PhoneOtp:TwilioAccountSid"]
    ?? builder.Configuration["TWILIO_ACCOUNT_SID"];
var twilioAuthToken = builder.Configuration["SqlOS:PhoneOtp:TwilioAuthToken"]
    ?? builder.Configuration["TWILIO_AUTH_TOKEN"];
var twilioVerifyServiceSid = builder.Configuration["SqlOS:PhoneOtp:TwilioVerifyServiceSid"]
    ?? builder.Configuration["TWILIO_VERIFY_SERVICE_SID"];
var phoneOtpDefaultRegion = builder.Configuration["SqlOS:PhoneOtp:DefaultRegion"]
    ?? builder.Configuration["TWILIO_DEFAULT_REGION"];
// "Continue with Microsoft" social login secrets for the example app. Set these on the AppHost
// (user-secrets or environment), never in source. They are forwarded to the API as env vars below.
var microsoftOidcClientId = builder.Configuration["SqlOS:Oidc:Microsoft:ClientId"]
    ?? builder.Configuration["AZURE_OIDC_MICROSOFT_CLIENT_ID"];
var microsoftOidcClientSecret = builder.Configuration["SqlOS:Oidc:Microsoft:ClientSecret"]
    ?? builder.Configuration["AZURE_OIDC_MICROSOFT_CLIENT_SECRET"];
var microsoftOidcTenant = builder.Configuration["SqlOS:Oidc:Microsoft:Tenant"]
    ?? builder.Configuration["AZURE_OIDC_MICROSOFT_TENANT"];
var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: sqlPort)
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

if (!ephemeralSql)
{
    // The demo keeps a persistent container with a data volume so accounts
    // survive restarts; tests opt out so they never share state with it.
    sql = sql.WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
}

var exampleDatabase = sql.AddDatabase("sqlos-example");

// launchProfileName: null keeps the launchSettings port (5062) from being
// claimed by the Aspire proxy, so the configured port is the only one bound.
var api = builder.AddProject<Projects.SqlOS_Example_Api>("api", launchProfileName: null)
    .WithHttpEndpoint(port: apiPort, isProxied: false)
    .WithReference(exampleDatabase)
    .WaitFor(exampleDatabase)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ASPNETCORE_URLS", apiOrigin)
    .WithEnvironment("ConnectionStrings__DefaultConnection", exampleDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", $"{apiOrigin}/sqlos/auth")
    .WithEnvironment("SqlOS__HeadlessFrontendUrl", webOrigin)
    .WithEnvironment("ExampleFrontend__Origin", webOrigin)
    .WithEnvironment("ExampleFrontend__CallbackUrl", $"{webOrigin}/auth/callback")
    .WithEnvironment("ExampleFrontend__ClientId", "example-web")
    .WithEnvironment("ExampleFrontend__AngularOrigin", angularOrigin);

if (!string.IsNullOrWhiteSpace(emailConnectionString) && !string.IsNullOrWhiteSpace(emailFromAddress))
{
    api
        .WithEnvironment("SqlOS__Email__AzureCommunicationServicesConnectionString", emailConnectionString)
        .WithEnvironment("SqlOS__Email__FromAddress", emailFromAddress)
        .WithEnvironment("SqlOS__EmailOtp__AzureCommunicationServicesConnectionString", emailConnectionString)
        .WithEnvironment("SqlOS__EmailOtp__FromAddress", emailFromAddress);
}

if (!string.IsNullOrWhiteSpace(enablePhoneOtp))
{
    api.WithEnvironment("SqlOS__PhoneOtp__Enabled", enablePhoneOtp);
}

if (!string.IsNullOrWhiteSpace(twilioAccountSid))
{
    api.WithEnvironment("SqlOS__PhoneOtp__TwilioAccountSid", twilioAccountSid);
}

if (!string.IsNullOrWhiteSpace(twilioAuthToken))
{
    api.WithEnvironment("SqlOS__PhoneOtp__TwilioAuthToken", twilioAuthToken);
}

if (!string.IsNullOrWhiteSpace(twilioVerifyServiceSid))
{
    api.WithEnvironment("SqlOS__PhoneOtp__TwilioVerifyServiceSid", twilioVerifyServiceSid);
}

if (!string.IsNullOrWhiteSpace(phoneOtpDefaultRegion))
{
    api.WithEnvironment("SqlOS__PhoneOtp__DefaultRegion", phoneOtpDefaultRegion);
}

if (!string.IsNullOrWhiteSpace(microsoftOidcClientId) && !string.IsNullOrWhiteSpace(microsoftOidcClientSecret))
{
    api
        .WithEnvironment("SqlOS__Oidc__Microsoft__ClientId", microsoftOidcClientId)
        .WithEnvironment("SqlOS__Oidc__Microsoft__ClientSecret", microsoftOidcClientSecret);

    if (!string.IsNullOrWhiteSpace(microsoftOidcTenant))
    {
        api.WithEnvironment("SqlOS__Oidc__Microsoft__Tenant", microsoftOidcTenant);
    }
}

if (includeTodoStack)
{
var todoDatabase = sql.AddDatabase("sqlos-todo");

var todoApi = builder.AddProject<Projects.SqlOS_Todo_Api>("todo-api")
    .WithReference(todoDatabase)
    .WaitFor(todoDatabase)
    .WithEnvironment("ConnectionStrings__DefaultConnection", todoDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", todoIssuer)
    .WithEnvironment("TodoSample__PublicOrigin", todoOrigin)
    .WithEnvironment("TodoSample__Resource", todoResource)
    .WithEnvironment("TodoSample__EnableHeadless", "false")
    .WithEnvironment("TodoSample__EnableDcr", todoEnableDcr);

builder.AddProject<Projects.SqlOS_Example_AspNetCoreWeb>("aspnet-web")
    .WithEnvironment("SqlOS__Origin", todoOrigin)
    .WithEnvironment("SqlOS__ClientId", "example-aspnet")
    .WaitFor(todoApi);

if (!string.IsNullOrWhiteSpace(emailConnectionString) && !string.IsNullOrWhiteSpace(emailFromAddress))
{
    todoApi
        .WithEnvironment("SqlOS__Email__AzureCommunicationServicesConnectionString", emailConnectionString)
        .WithEnvironment("SqlOS__Email__FromAddress", emailFromAddress)
        .WithEnvironment("SqlOS__EmailOtp__AzureCommunicationServicesConnectionString", emailConnectionString)
        .WithEnvironment("SqlOS__EmailOtp__FromAddress", emailFromAddress);
}

if (!string.IsNullOrWhiteSpace(enablePhoneOtp))
{
    todoApi
        .WithEnvironment("TodoSample__EnablePhoneOtp", enablePhoneOtp)
        .WithEnvironment("SqlOS__PhoneOtp__Enabled", enablePhoneOtp);
}

if (!string.IsNullOrWhiteSpace(twilioAccountSid))
{
    todoApi.WithEnvironment("SqlOS__PhoneOtp__TwilioAccountSid", twilioAccountSid);
}

if (!string.IsNullOrWhiteSpace(twilioAuthToken))
{
    todoApi.WithEnvironment("SqlOS__PhoneOtp__TwilioAuthToken", twilioAuthToken);
}

if (!string.IsNullOrWhiteSpace(twilioVerifyServiceSid))
{
    todoApi.WithEnvironment("SqlOS__PhoneOtp__TwilioVerifyServiceSid", twilioVerifyServiceSid);
}

if (!string.IsNullOrWhiteSpace(phoneOtpDefaultRegion))
{
    todoApi.WithEnvironment("SqlOS__PhoneOtp__DefaultRegion", phoneOtpDefaultRegion);
}
}

builder.AddNpmApp("web", "../SqlOS.Example.Web", "dev")
    .WithHttpEndpoint(port: webPort, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("NEXT_PUBLIC_API_URL", apiOrigin)
    .WithEnvironment("NEXTAUTH_URL", webOrigin)
    .WithEnvironment("NEXTAUTH_SECRET", "sqlos-example-local-secret")
    // Each stack compiles into its own Next.js dist directory; two dev servers
    // sharing one .next (demo + e2e tests) corrupt each other's builds.
    .WithEnvironment("NEXT_DIST_DIR", webPort == 3010 ? ".next" : $".next-{webPort}")
    .WaitFor(api);

builder.AddNpmApp("angular-web", "../SqlOS.Example.AngularWeb", "dev")
    .WithHttpEndpoint(port: angularPort, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("SQLOS_API_URL", apiOrigin)
    .WaitFor(api);

builder.Build().Run();

static int GetPort(string? configured, int fallback) =>
    int.TryParse(configured, out var port) ? port : fallback;
