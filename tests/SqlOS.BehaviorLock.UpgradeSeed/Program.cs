using System.Reflection;
using System.Text.Json;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.UpgradeSeed;

SeedArguments arguments;
try
{
    arguments = SeedArguments.Parse(args, Environment.GetEnvironmentVariable(SeedArguments.ConnectionStringVariable));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(SeedArguments.Usage);
    return 2;
}

var sqlosVersion = typeof(SqlOS.Configuration.SqlOSOptions).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
Console.WriteLine($"Seeding the {arguments.Dataset} upgrade dataset with SqlOS {sqlosVersion} ({arguments.Provider}).");

try
{
    Directory.CreateDirectory(arguments.DataProtectionKeysDirectory);
    await using var app = BehaviorLockHost.Build(new BehaviorLockHostOptions
    {
        Profile = HostProfiles.Upgrade,
        Provider = arguments.Provider,
        ConnectionString = arguments.ConnectionString,
        DataProtectionKeysDirectory = arguments.DataProtectionKeysDirectory
    });
    await app.StartAsync();
    var seeder = new UpgradeSeeder(app, sqlosVersion, Console.Out);
    object manifest = arguments.Dataset == UpgradeData.DirectorySubjectsDataset
        ? await seeder.SeedDirectorySubjectsAsync()
        : await seeder.SeedAsync();
    await File.WriteAllTextAsync(
        arguments.ManifestPath,
        JsonSerializer.Serialize(manifest, manifest.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    await app.StopAsync();
    Console.WriteLine($"Wrote {arguments.ManifestPath}.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"The upgrade seed failed: {ex}");
    return 1;
}
