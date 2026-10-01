using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Database;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Infrastructure.Upgrade;

/// <summary>
/// A fresh database seeded by <c>tests/SqlOS.BehaviorLock.UpgradeSeed</c>, which runs in its own
/// process against the released SqlOS package (7.2.1 unless the seed was built with another
/// <c>SqlOSUpgradeFromVersion</c>). The upgrade gate then starts the build under test on
/// <see cref="ConnectionString"/> with the same <see cref="DataProtectionKeysDirectory"/>.
/// Disposing drops the database and deletes the keys.
/// </summary>
public sealed class SeededDatabase : IAsyncDisposable
{
    private static readonly TimeSpan SeedTimeout = TimeSpan.FromMinutes(3);
    private readonly DirectoryInfo _workDirectory;

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);
    private readonly string _manifest;

    private SeededDatabase(string connectionString, DirectoryInfo workDirectory, string manifest)
    {
        ConnectionString = connectionString;
        _workDirectory = workDirectory;
        _manifest = manifest;
    }

    public string ConnectionString { get; }

    public string DataProtectionKeysDirectory => Path.Combine(_workDirectory.FullName, "keys");

    /// <summary>The manifest of the full dataset.</summary>
    public UpgradeManifest Manifest => ManifestAs<UpgradeManifest>();

    /// <summary>The SqlOS version that wrote the data, without build metadata (for example <c>7.2.1</c>).</summary>
    public string SeededVersion => JsonDocument.Parse(_manifest).RootElement.GetProperty("seededWith").GetString()!.Split('+')[0];

    /// <summary>The seed program, built next to the test project in the same configuration.</summary>
    public static string SeedAssemblyPath { get; } = typeof(SeededDatabase).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "UpgradeSeedAssembly")
        .Value!;

    /// <summary>The manifest of the dataset this database was seeded with.</summary>
    public T ManifestAs<T>()
        => JsonSerializer.Deserialize<T>(_manifest, ManifestJson)
            ?? throw new InvalidOperationException("The upgrade seed wrote an empty manifest.");

    public static Task<SeededDatabase> CreateAsync(CancellationToken cancellationToken = default)
        => CreateAsync(UpgradeData.FullDataset, cancellationToken);

    /// <summary>Seeds a fresh database with <paramref name="dataset"/> (an <see cref="UpgradeData"/> dataset).</summary>
    public static async Task<SeededDatabase> CreateAsync(string dataset, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SeedAssemblyPath))
        {
            throw new InvalidOperationException(
                $"The upgrade seed is not built at {SeedAssemblyPath}. Build the behavior-lock project (it builds the seed) or run: dotnet build tests/SqlOS.BehaviorLock.UpgradeSeed");
        }

        var connectionString = await BehaviorLockDatabase.CreateDatabaseAsync("upgrade", cancellationToken);
        var workDirectory = Directory.CreateTempSubdirectory("sqlos-upgrade-");
        try
        {
            var manifestPath = Path.Combine(workDirectory.FullName, "manifest.json");
            await RunSeedAsync(connectionString, Path.Combine(workDirectory.FullName, "keys"), manifestPath, dataset, cancellationToken);
            return new SeededDatabase(connectionString, workDirectory, await File.ReadAllTextAsync(manifestPath, cancellationToken));
        }
        catch
        {
            await BehaviorLockDatabase.DropDatabaseAsync(connectionString, CancellationToken.None);
            workDirectory.Delete(recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Names the manifest's per-run values in the transcript, so it reads <c>{usr:alice}</c> and
    /// <c>{refresh-token:portal}</c> rather than whatever this run generated.
    /// </summary>
    public void RegisterWith(Transcript transcript)
    {
        var manifest = Manifest;
        NameId(transcript, manifest.Alice.Id, "alice");
        NameId(transcript, manifest.Bob.Id, "bob");
        NameId(transcript, manifest.Bob.FgaSubjectId, "bob");
        NameId(transcript, manifest.Carol.Id, "carol");
        NameId(transcript, manifest.OrganizationId, "acme");
        NameId(transcript, manifest.SamlConnectionId, "acme");
        NameId(transcript, manifest.ScimConnectionId, "acme");
        NameId(transcript, manifest.ScimGroupId, "engineering");
        NameId(transcript, manifest.ScimMappingId, "engineering");
        NameId(transcript, manifest.DynamicClientId, "dynamic");
        NameId(transcript, manifest.CalendarConnectionId, "alice");
        foreach (var (email, name) in new[] { (UpgradeData.AliceEmail, "alice"), (UpgradeData.BobEmail, "bob"), (UpgradeData.CarolEmail, "carol") })
        {
            transcript.Scrub(email, "email", name);
            transcript.Scrub(email.ToUpperInvariant(), "email", name.ToUpperInvariant());
        }

        transcript.Scrub(UpgradeData.AlicePassword, "password", "alice");
        transcript.Scrub(UpgradeData.CarolPassword, "password", "carol");
        transcript.Scrub(manifest.Alice.TotpSecret!, "totp-secret", "alice");
        transcript.Scrub(manifest.ScimToken, "scim-token", "acme");
        transcript.Scrub(manifest.SessionCookie, "session-cookie", "alice");
        transcript.Scrub(manifest.PendingDevice.DeviceCode, "device-code", "cli");
        transcript.Scrub(manifest.PendingDevice.UserCode, "user-code", "cli");
        foreach (var token in manifest.RefreshTokens)
        {
            transcript.Scrub(token.RefreshToken, "refresh-token", token.Label);
        }
    }

    /// <summary>Names the <see cref="UpgradeData.DirectorySubjectsDataset"/> manifest's per-run values in the transcript.</summary>
    public void RegisterWith(Transcript transcript, DirectorySubjectsManifest manifest)
    {
        NameId(transcript, manifest.Bob.Id, "bob");
        NameId(transcript, manifest.Bob.ScimSubjectId, "bob");
        NameId(transcript, manifest.Ann.Id, "ann");
        NameId(transcript, manifest.Ann.ScimSubjectId, "ann");
        NameId(transcript, manifest.OrganizationId, "acme");
        NameId(transcript, manifest.ScimConnectionId, "acme");
        NameId(transcript, manifest.ScimGroupId, "engineering");
        NameId(transcript, manifest.ScimMappingId, "engineering");
        foreach (var (email, name) in new[] { (UpgradeData.BobEmail, "bob"), (UpgradeData.AnnEmail, "ann") })
        {
            transcript.Scrub(email, "email", name);
            transcript.Scrub(email.ToUpperInvariant(), "email", name.ToUpperInvariant());
        }

        transcript.Scrub(manifest.ScimToken, "scim-token", "acme");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await BehaviorLockDatabase.DropDatabaseAsync(ConnectionString, CancellationToken.None);
        }
        finally
        {
            _workDirectory.Delete(recursive: true);
        }
    }

    /// <summary>Names a SqlOS ID with its own prefix as the kind: <c>usr_…</c> becomes <c>{usr:alice}</c>.</summary>
    private static void NameId(Transcript transcript, string id, string name)
    {
        var separator = id.IndexOf('_', StringComparison.Ordinal);
        transcript.Scrub(id, separator > 0 ? id[..separator] : "id", name);
    }

    private static async Task RunSeedAsync(string connectionString, string keysDirectory, string manifestPath, string dataset, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(SeedAssemblyPath)!
        };
        foreach (var argument in new[]
                 {
                     SeedAssemblyPath,
                     "--provider", BehaviorLockDatabase.ProviderName,
                     "--data-protection-keys", keysDirectory,
                     "--manifest", manifestPath,
                     "--dataset", dataset
                 })
        {
            start.ArgumentList.Add(argument);
        }

        // Kept out of the argument list, which other processes can read.
        start.Environment["SQLOS_UPGRADE_SEED_CONNECTION_STRING"] = connectionString;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {start.FileName} {SeedAssemblyPath}.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => Append(output, line.Data);
        process.ErrorDataReceived += (_, line) => Append(output, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SeedTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The upgrade seed did not finish within {SeedTimeout}. Output:\n{Snapshot(output)}");
        }

        // WaitForExitAsync returns once the process exits; this drains the redirected streams.
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The upgrade seed exited with code {process.ExitCode}. Output:\n{Snapshot(output)}");
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line == null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}
