using System.Reflection;
using System.Security.Cryptography;

namespace SqlOS.BehaviorLock.Infrastructure;

/// <summary>
/// Which SqlOS the suite is running against, and a guard that the loaded assembly really is that
/// build. Switching between <c>-p:SqlOSUnderTest=source</c> and <c>package</c> rebuilds the test
/// output; the guard makes a stale mix fail loudly instead of approving the wrong build.
/// </summary>
public static class SqlOSUnderTest
{
    public static string Mode { get; } = typeof(Host.BehaviorLockHost).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SqlOSUnderTest")
        .Value!;

    public static string BaselineVersion { get; } = typeof(Host.BehaviorLockHost).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SqlOSBaselineVersion")
        .Value!;

    public static bool IsPackage => string.Equals(Mode, "package", StringComparison.Ordinal);

    public static Assembly Assembly => typeof(SqlOS.Configuration.SqlOSOptions).Assembly;

    public static string InformationalVersion
        => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Package mode must load the NuGet package's assembly byte for byte; source mode must not.
    /// </summary>
    public static string VerifyLoadedAssembly()
    {
        var loaded = Assembly.Location;
        var packageAssembly = Path.Combine(
            Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
            "sqlos",
            BaselineVersion,
            "lib",
            "net9.0",
            "SqlOS.dll");
        var matchesPackage = File.Exists(packageAssembly) && Hash(loaded) == Hash(packageAssembly);
        if (IsPackage && !matchesPackage)
        {
            throw new InvalidOperationException(
                $"Package mode expected SqlOS {BaselineVersion} from {packageAssembly}, but loaded {loaded} ({InformationalVersion}). Rebuild with -p:SqlOSUnderTest=package.");
        }

        if (!IsPackage && matchesPackage)
        {
            throw new InvalidOperationException(
                $"Source mode loaded the released SqlOS {BaselineVersion} package assembly. Rebuild with -p:SqlOSUnderTest=source.");
        }

        return $"SqlOS under test: {Mode} ({InformationalVersion}) from {loaded}";
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
