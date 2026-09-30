using SqlOS.BehaviorLock.Host;

// Runs one behavior-lock profile on Kestrel for manual exploration, for example:
//   dotnet run --project tests/SqlOS.BehaviorLock.Host -- \
//     --BehaviorLock:Profile=hosted --BehaviorLock:Provider=SqlServer \
//     --ConnectionStrings:BehaviorLock="Server=...;Database=...;..."
// The test suite does not use this entry point; it builds the same host in-process with TestServer.
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
var profile = configuration["BehaviorLock:Profile"] ?? SqlOS.BehaviorLock.Host.Profiles.HostProfiles.Hosted;
var provider = Enum.Parse<DatabaseProvider>(configuration["BehaviorLock:Provider"] ?? nameof(DatabaseProvider.SqlServer), ignoreCase: true);
var connectionString = configuration.GetConnectionString("BehaviorLock")
    ?? throw new InvalidOperationException("Set ConnectionStrings:BehaviorLock.");

var app = BehaviorLockHost.Build(new BehaviorLockHostOptions
{
    Profile = profile,
    Provider = provider,
    ConnectionString = connectionString,
    UseTestServer = false,
    Args = args,
    MinimumLogLevel = LogLevel.Information
});
app.Run();
