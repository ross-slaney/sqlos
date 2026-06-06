using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

const int todoPort = 5080;
var todoOrigin = $"http://localhost:{todoPort}";
var todoResource = $"{todoOrigin}/api/todos";
var todoIssuer = $"{todoOrigin}/sqlos/auth";
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
var exampleDatabaseName = builder.Configuration["SqlOS:ExampleDatabaseName"]
    ?? builder.Configuration["SQLOS_EXAMPLE_DATABASE_NAME"];
exampleDatabaseName = string.IsNullOrWhiteSpace(exampleDatabaseName)
    ? "sqlos-example"
    : exampleDatabaseName.Trim();
var todoDatabaseName = builder.Configuration["SqlOS:TodoDatabaseName"]
    ?? builder.Configuration["SQLOS_TODO_DATABASE_NAME"];
todoDatabaseName = string.IsNullOrWhiteSpace(todoDatabaseName)
    ? "sqlos-todo"
    : todoDatabaseName.Trim();
// "Continue with Microsoft" social login secrets for the example app. Set these on the AppHost
// (user-secrets or environment), never in source. They are forwarded to the API as env vars below.
var microsoftOidcClientId = builder.Configuration["SqlOS:Oidc:Microsoft:ClientId"]
    ?? builder.Configuration["AZURE_OIDC_MICROSOFT_CLIENT_ID"];
var microsoftOidcClientSecret = builder.Configuration["SqlOS:Oidc:Microsoft:ClientSecret"]
    ?? builder.Configuration["AZURE_OIDC_MICROSOFT_CLIENT_SECRET"];
var microsoftOidcTenant = builder.Configuration["SqlOS:Oidc:Microsoft:Tenant"]
    ?? builder.Configuration["AZURE_OIDC_MICROSOFT_TENANT"];
var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");

var sql = builder.AddSqlServer("sql", password: sqlPassword, port: 1434)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .WithContainerRuntimeArgs("--platform", "linux/amd64");

var exampleDatabase = sql.AddDatabase("sqlos-example", exampleDatabaseName);
var todoDatabase = sql.AddDatabase("sqlos-todo", todoDatabaseName);

var api = builder.AddProject<Projects.SqlOS_Example_Api>("api")
    .WithReference(exampleDatabase)
    .WaitFor(exampleDatabase)
    .WithEnvironment("ConnectionStrings__DefaultConnection", exampleDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", "http://localhost:5062/sqlos/auth")
    .WithEnvironment("SqlOS__HeadlessFrontendUrl", "http://localhost:3010")
    .WithEnvironment("ExampleFrontend__Origin", "http://localhost:3010")
    .WithEnvironment("ExampleFrontend__CallbackUrl", "http://localhost:3010/auth/callback")
    .WithEnvironment("ExampleFrontend__ClientId", "example-web");

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

var todoApi = builder.AddProject<Projects.SqlOS_Todo_Api>("todo-api")
    .WithReference(todoDatabase)
    .WaitFor(todoDatabase)
    .WithEnvironment("ConnectionStrings__DefaultConnection", todoDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", todoIssuer)
    .WithEnvironment("TodoSample__PublicOrigin", todoOrigin)
    .WithEnvironment("TodoSample__Resource", todoResource)
    .WithEnvironment("TodoSample__EnableHeadless", "false")
    .WithEnvironment("TodoSample__EnableDcr", "false");

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

builder.AddNpmApp("web", "../SqlOS.Example.Web", "dev")
    .WithHttpEndpoint(port: 3010, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WithEnvironment("NEXT_PUBLIC_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("NEXTAUTH_URL", "http://localhost:3010")
    .WithEnvironment("NEXTAUTH_SECRET", "sqlos-example-local-secret")
    .WaitFor(api);

builder.AddNpmApp("angular-web", "../SqlOS.Example.AngularWeb", "dev")
    .WithHttpEndpoint(port: 4200, env: "PORT", isProxied: false)
    .WithEnvironment("NODE_ENV", "development")
    .WaitFor(api);

builder.Build().Run();
