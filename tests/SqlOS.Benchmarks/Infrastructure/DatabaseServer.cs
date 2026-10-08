using System.Globalization;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace SqlOS.Benchmarks.Infrastructure;

/// <summary>
/// The database engine under test: a container started for the run (the default), or an existing server
/// passed with <c>--connection</c>. Either way the benchmark gets its own freshly created database.
/// </summary>
internal sealed class DatabaseServer : IAsyncDisposable
{
    public const string DatabaseName = "SqlOSBenchmarks";

    private readonly IContainer? _container;
    private readonly bool _keep;

    private DatabaseServer(DatabaseProvider provider, string serverConnectionString, IContainer? container, bool keep, string description)
    {
        Provider = provider;
        ServerConnectionString = serverConnectionString;
        _container = container;
        _keep = keep;
        Description = description;
    }

    public DatabaseProvider Provider { get; }
    public string ServerConnectionString { get; }
    public string Description { get; }

    /// <summary>The benchmark database, with no command timeout (loads and index builds run for minutes).</summary>
    public string DatabaseConnectionString => Provider == DatabaseProvider.PostgreSql
        ? new NpgsqlConnectionStringBuilder(ServerConnectionString)
        {
            Database = DatabaseName,
            CommandTimeout = 0,
            MaxPoolSize = 32,
        }.ConnectionString
        : new SqlConnectionStringBuilder(ServerConnectionString)
        {
            InitialCatalog = DatabaseName,
            CommandTimeout = 0,
            MaxPoolSize = 32,
            TrustServerCertificate = true,
        }.ConnectionString;

    public static async Task<DatabaseServer> StartAsync(BenchmarkOptions options, Log log, CancellationToken cancellationToken)
    {
        if (options.ConnectionString is not null)
        {
            return new DatabaseServer(options.Provider, options.ConnectionString, container: null, keep: true, "existing server (--connection)");
        }

        var dataDirectory = PrepareDataDirectory(options);
        if (options.Provider == DatabaseProvider.PostgreSql)
        {
            var memory = options.DatabaseMemoryMegabytes;
            var builder = new PostgreSqlBuilder(options.Image ?? "postgres:16")
                .WithCleanUp(!options.KeepContainer)
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithDatabase("postgres")
                // Parallel index builds use dynamic shared memory; Docker's default 64 MB /dev/shm is too small.
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.HostConfig ??= new HostConfig();
                    parameters.HostConfig.ShmSize = 2L * 1024 * 1024 * 1024;
                })
                .WithCommand(PostgresSettings(memory));
            if (dataDirectory is not null)
            {
                builder = builder
                    .WithBindMount(dataDirectory, "/var/lib/postgresql/data")
                    .WithEnvironment("PGDATA", "/var/lib/postgresql/data/pgdata");
            }

            var container = builder.Build();
            log.Info($"Starting {options.Image ?? "postgres:16"}{(dataDirectory is null ? "" : $" (data in {dataDirectory})")}...");
            await container.StartAsync(cancellationToken);
            return new DatabaseServer(options.Provider, container.GetConnectionString(), container, options.KeepContainer, $"container {options.Image ?? "postgres:16"}");
        }
        else
        {
            var image = options.Image ?? "mcr.microsoft.com/mssql/server:2022-latest";
            var builder = new MsSqlBuilder(image)
                .WithCleanUp(!options.KeepContainer)
                .WithPassword("SqlOS_Bench_2026!")
                .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", options.DatabaseMemoryMegabytes.ToString(CultureInfo.InvariantCulture));
            if (dataDirectory is not null)
            {
                builder = builder.WithBindMount(dataDirectory, "/var/opt/mssql");
            }

            var container = builder.Build();
            log.Info($"Starting {image}{(dataDirectory is null ? "" : $" (data in {dataDirectory})")}...");
            await container.StartAsync(cancellationToken);
            return new DatabaseServer(options.Provider, container.GetConnectionString(), container, options.KeepContainer, $"container {image}");
        }
    }

    /// <summary>
    /// Set to a number of milliseconds to have PostgreSQL log the actual plan of every statement slower than
    /// that (<c>auto_explain</c>, with analyze, buffers and parameters) into the container log, which the run
    /// then saves beside its results. Diagnostic: the instrumentation itself slows every statement a little.
    /// </summary>
    public const string ExplainEnvironmentVariable = "SQLOS_BENCH_EXPLAIN_MS";

    public static int? ExplainMilliseconds
        => int.TryParse(Environment.GetEnvironmentVariable(ExplainEnvironmentVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0 ? ms : null;

    /// <summary>
    /// Load-only durability settings (fsync, WAL level, commit mode) do not change how reads are planned or
    /// executed; the planner settings are the usual SSD values.
    /// </summary>
    private static string[] PostgresSettings(int memoryMegabytes)
    {
        string Mb(double fraction) => $"{Math.Max(64, (int)(memoryMegabytes * fraction))}MB";
        var settings = new List<string>
        {
            "-c", $"shared_buffers={Mb(0.25)}",
            "-c", $"effective_cache_size={Mb(0.75)}",
            "-c", $"maintenance_work_mem={Mb(0.125)}",
            "-c", "work_mem=64MB",
            "-c", "max_parallel_maintenance_workers=4",
            "-c", "max_parallel_workers=8",
            "-c", "max_wal_size=16GB",
            "-c", "checkpoint_timeout=30min",
            "-c", "wal_level=minimal",
            "-c", "max_wal_senders=0",
            "-c", "synchronous_commit=off",
            "-c", "fsync=off",
            "-c", "full_page_writes=off",
            "-c", "random_page_cost=1.1",
            "-c", "effective_io_concurrency=200",
        };
        if (ExplainMilliseconds is { } explain)
        {
            settings.AddRange(
            [
                "-c", "session_preload_libraries=auto_explain",
                "-c", $"auto_explain.log_min_duration={explain.ToString(CultureInfo.InvariantCulture)}ms",
                "-c", "auto_explain.log_analyze=on",
                "-c", "auto_explain.log_buffers=on",
                "-c", "auto_explain.log_nested_statements=on",
                "-c", "auto_explain.log_parameter_max_length=4000",
            ]);
        }

        return [.. settings];
    }

    /// <summary>Writes the container's log (the engine's own messages, the plans <c>auto_explain</c> logged) to a file; nothing for an existing server.</summary>
    public async Task SaveLogsAsync(string path, CancellationToken cancellationToken)
    {
        if (_container is null)
        {
            return;
        }

        var (stdout, stderr) = await _container.GetLogsAsync(ct: cancellationToken);
        await File.WriteAllTextAsync(path, stdout + stderr, cancellationToken);
    }

    private static string? PrepareDataDirectory(BenchmarkOptions options)
    {
        if (options.DataDirectory is null)
        {
            return null;
        }

        var directory = Path.GetFullPath(Path.Combine(
            options.DataDirectory,
            options.Provider == DatabaseProvider.PostgreSql ? "postgresql" : "sqlserver"));
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // Files from an earlier run belong to the container's user; start beside them instead.
                directory += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            }
        }

        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            // SQL Server runs as uid 10001 inside its container and must be able to write here.
            File.SetUnixFileMode(directory, (UnixFileMode)0x1FF);
        }

        return directory;
    }

    /// <summary>Drops and recreates the benchmark database.</summary>
    public async Task RecreateDatabaseAsync(CancellationToken cancellationToken)
    {
        if (Provider == DatabaseProvider.PostgreSql)
        {
            var admin = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = "postgres", CommandTimeout = 0 }.ConnectionString;
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync(cancellationToken);
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE);", connection))
            {
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\";", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var master = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = "master", CommandTimeout = 0, TrustServerCertificate = true }.ConnectionString;
        await using var sql = new SqlConnection(master);
        await sql.OpenAsync(cancellationToken);
        await using var command = sql.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            CREATE DATABASE [{DatabaseName}];
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null && !_keep)
        {
            await _container.DisposeAsync();
        }
    }
}
