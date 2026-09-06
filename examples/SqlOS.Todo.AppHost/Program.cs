using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

const int todoPort = 5080;
var todoOrigin = $"http://localhost:{todoPort}";
var todoResource = $"{todoOrigin}/api/todos";
var todoIssuer = $"{todoOrigin}/sqlos/auth";
var todoEnableEmailOtp = builder.Configuration["TodoSample:EnableEmailOtp"];
var todoEnableDcr = builder.Configuration["TodoSample:EnableDcr"] ?? "false";
var todoEnablePhoneOtp = builder.Configuration["TodoSample:EnablePhoneOtp"]
    ?? builder.Configuration["SqlOS:PhoneOtp:Enabled"];
var emailConnectionString = builder.Configuration["SqlOS:Email:AzureCommunicationServicesConnectionString"]
    ?? builder.Configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"]
    ?? builder.Configuration["AZURE_EMAIL_CONNECTION_STRING"];
var emailFromAddress = builder.Configuration["SqlOS:Email:FromAddress"]
    ?? builder.Configuration["SqlOS:EmailOtp:FromAddress"]
    ?? builder.Configuration["AZURE_EMAIL_SENDER_ADDRESS"];
var twilioAccountSid = builder.Configuration["SqlOS:PhoneOtp:TwilioAccountSid"]
    ?? builder.Configuration["TWILIO_ACCOUNT_SID"];
var twilioAuthToken = builder.Configuration["SqlOS:PhoneOtp:TwilioAuthToken"]
    ?? builder.Configuration["TWILIO_AUTH_TOKEN"];
var twilioVerifyServiceSid = builder.Configuration["SqlOS:PhoneOtp:TwilioVerifyServiceSid"]
    ?? builder.Configuration["TWILIO_VERIFY_SERVICE_SID"];
var phoneOtpDefaultRegion = builder.Configuration["SqlOS:PhoneOtp:DefaultRegion"]
    ?? builder.Configuration["TWILIO_DEFAULT_REGION"];
var usePostgreSql = string.Equals(
    builder.Configuration["SqlOS:DatabaseProvider"],
    "PostgreSql",
    StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> database;
if (usePostgreSql)
{
    database = builder.AddPostgres("sql")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume()
        .AddDatabase("sqlos-todo");
}
else
{
    var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!");
    database = builder.AddSqlServer("sql", password: sqlPassword, port: 1435)
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume()
        .WithContainerRuntimeArgs("--platform", "linux/amd64")
        .AddDatabase("sqlos-todo");
}

var todoApi = builder.AddProject<Projects.SqlOS_Todo_Api>("todo-api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("ConnectionStrings__DefaultConnection", database.Resource.ConnectionStringExpression)
    .WithEnvironment("SqlOS__Issuer", todoIssuer)
    .WithEnvironment("TodoSample__PublicOrigin", todoOrigin)
    .WithEnvironment("TodoSample__Resource", todoResource)
    .WithEnvironment("TodoSample__EnableHeadless", "false")
    .WithEnvironment("TodoSample__EnableDcr", todoEnableDcr)
    .WithEnvironment("SqlOS__DatabaseProvider", usePostgreSql ? "PostgreSql" : "SqlServer");

builder.AddProject<Projects.SqlOS_Example_AspNetCoreWeb>("aspnet-web")
    .WithEnvironment("SqlOS__Origin", todoOrigin)
    .WithEnvironment("SqlOS__ClientId", "example-aspnet")
    .WaitFor(todoApi);

if (!string.IsNullOrWhiteSpace(todoEnableEmailOtp))
{
    todoApi.WithEnvironment("TodoSample__EnableEmailOtp", todoEnableEmailOtp);
}

if (!string.IsNullOrWhiteSpace(todoEnablePhoneOtp))
{
    todoApi
        .WithEnvironment("TodoSample__EnablePhoneOtp", todoEnablePhoneOtp)
        .WithEnvironment("SqlOS__PhoneOtp__Enabled", todoEnablePhoneOtp);
}

if (!string.IsNullOrWhiteSpace(emailConnectionString) && !string.IsNullOrWhiteSpace(emailFromAddress))
{
    todoApi
        .WithEnvironment("SqlOS__Email__AzureCommunicationServicesConnectionString", emailConnectionString)
        .WithEnvironment("SqlOS__Email__FromAddress", emailFromAddress)
        .WithEnvironment("SqlOS__EmailOtp__AzureCommunicationServicesConnectionString", emailConnectionString)
        .WithEnvironment("SqlOS__EmailOtp__FromAddress", emailFromAddress);
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

builder.Build().Run();
