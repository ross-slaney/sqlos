using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using SqlOS.BehaviorLock.Host;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// Accounts the database already holds when a run starts, so the tables a sign-in reads are the
/// size a deployment's are: a user, a verified primary email and a password credential each. A
/// query that reads one account's rows by an unindexed column scans every account's, which a
/// nearly empty database hides. They are written straight into the tables, many thousands per
/// statement, because creating them through SqlOS would hash a password per account. Nothing
/// signs them in. Both builds read these three tables as 7.2.1 created them, so the same rows
/// suit both.
/// </summary>
internal static class BackgroundAccounts
{
    public static async Task SeedAsync(
        BenchmarkDatabaseServer server,
        string connectionString,
        string schema,
        int count,
        CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        // One real PBKDF2 hash for every row: the shape a stored password has, computed once.
        var hash = new PasswordHasher<object>().HashPassword(new object(), BenchmarkHost.Password);
        await using var connection = await server.OpenAsync(connectionString, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1800;
        command.CommandText = server.Provider == DatabaseProvider.PostgreSql
            ? PostgreSqlSeed(schema)
            : SqlServerSeed(schema);
        Add(command, "@count", count);
        Add(command, "@hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
        Console.WriteLine($"Seeded {count:N0} background accounts in {clock.Elapsed.TotalSeconds:F0}s");
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string SqlServerSeed(string schema) => $"""
        SET NOCOUNT ON;
        DECLARE @now datetime2(7) = SYSUTCDATETIME();
        SELECT TOP (@count) RIGHT('0000000' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS varchar(10)), 8) AS [Key]
        INTO #accounts
        FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b;
        INSERT INTO [{schema}].[SqlOSUsers] ([Id], [DisplayName], [DefaultEmail], [IsActive], [CreatedAt], [UpdatedAt])
        SELECT 'usr_bg' + [Key], 'Background ' + [Key], 'bg' + [Key] + '@accounts.example.test', 1, @now, @now FROM #accounts;
        INSERT INTO [{schema}].[SqlOSUserEmails] ([Id], [UserId], [Email], [NormalizedEmail], [IsPrimary], [IsVerified], [VerifiedAt], [CreatedAt])
        SELECT 'uem_bg' + [Key], 'usr_bg' + [Key], 'bg' + [Key] + '@accounts.example.test', 'bg' + [Key] + '@accounts.example.test', 1, 1, @now, @now FROM #accounts;
        INSERT INTO [{schema}].[SqlOSCredentials] ([Id], [UserId], [Type], [SecretHash], [SecretVersion], [LastUsedAt], [CreatedAt], [RevokedAt])
        SELECT 'cred_bg' + [Key], 'usr_bg' + [Key], 'password', @hash, 1, NULL, @now, NULL FROM #accounts;
        DROP TABLE #accounts;
        UPDATE STATISTICS [{schema}].[SqlOSUsers];
        UPDATE STATISTICS [{schema}].[SqlOSUserEmails];
        UPDATE STATISTICS [{schema}].[SqlOSCredentials];
        """;

    private static string PostgreSqlSeed(string schema) => $"""
        CREATE TEMPORARY TABLE accounts AS
            SELECT lpad(n::text, 8, '0') AS key, now() AT TIME ZONE 'UTC' AS at
            FROM generate_series(1, @count) AS n;
        INSERT INTO "{schema}"."SqlOSUsers" ("Id", "DisplayName", "DefaultEmail", "IsActive", "CreatedAt", "UpdatedAt")
            SELECT 'usr_bg' || key, 'Background ' || key, 'bg' || key || '@accounts.example.test', TRUE, at, at FROM accounts;
        INSERT INTO "{schema}"."SqlOSUserEmails" ("Id", "UserId", "Email", "NormalizedEmail", "IsPrimary", "IsVerified", "VerifiedAt", "CreatedAt")
            SELECT 'uem_bg' || key, 'usr_bg' || key, 'bg' || key || '@accounts.example.test', 'bg' || key || '@accounts.example.test', TRUE, TRUE, at, at FROM accounts;
        INSERT INTO "{schema}"."SqlOSCredentials" ("Id", "UserId", "Type", "SecretHash", "SecretVersion", "LastUsedAt", "CreatedAt", "RevokedAt")
            SELECT 'cred_bg' || key, 'usr_bg' || key, 'password', @hash, 1, NULL, at, NULL FROM accounts;
        DROP TABLE accounts;
        ANALYZE "{schema}"."SqlOSUsers";
        ANALYZE "{schema}"."SqlOSUserEmails";
        ANALYZE "{schema}"."SqlOSCredentials";
        """;
}
