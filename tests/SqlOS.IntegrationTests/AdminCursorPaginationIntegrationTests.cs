using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Pagination;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AdminCursorPaginationIntegrationTests
{
    [TestMethod]
    public async Task AdminLists_WalkCursorWindows_WithoutOffsetOrFullCount()
    {
        await using var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("AdminCursor");
        var connectionString = setupContext.Database.GetConnectionString();
        connectionString.Should().NotBeNullOrWhiteSpace();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var setupAdmin = new SqlOSAdminService(
            setupContext,
            options,
            new SqlOSCryptoService(setupContext, options));

        for (var index = 0; index < 35; index++)
        {
            await setupAdmin.CreateUserAsync(new SqlOSCreateUserRequest(
                $"Cursor User {index:D2}",
                $"cursor-{index:D2}-{Guid.NewGuid():N}@example.test",
                "P@ssword123!"));
        }

        var interceptor = new CommandCaptureInterceptor();
        await using var capturedContext = CreateContext(connectionString!, interceptor);
        var admin = new SqlOSAdminService(
            capturedContext,
            options,
            new SqlOSCryptoService(capturedContext, options));

        interceptor.Commands.Clear();
        var first = Serialize(await admin.ListUsersAsync(pageSize: 10));
        first.GetProperty("data").GetArrayLength().Should().Be(10);
        first.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();
        var firstCursor = first.GetProperty("nextCursor").GetString();
        firstCursor.Should().NotBeNullOrWhiteSpace();
        AssertNoOffsetOrFullCount(interceptor.Commands);

        interceptor.Commands.Clear();
        var second = Serialize(await admin.ListUsersAsync(cursor: firstCursor, pageSize: 10));
        second.GetProperty("data").GetArrayLength().Should().Be(10);
        second.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();
        var secondIds = second.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        var firstIds = first.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        secondIds.Should().NotIntersectWith(firstIds);
        AssertNoOffsetOrFullCount(interceptor.Commands);
        interceptor.Commands.Should().Contain(command =>
            command.Contains("DisplayName", StringComparison.OrdinalIgnoreCase)
            && command.Contains("TOP", StringComparison.OrdinalIgnoreCase));

        interceptor.Commands.Clear();
        var third = Serialize(await admin.ListUsersAsync(cursor: second.GetProperty("nextCursor").GetString(), pageSize: 10));
        third.GetProperty("data").GetArrayLength().Should().Be(10);
        third.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();
        AssertNoOffsetOrFullCount(interceptor.Commands);

        var wrongFilter = async () => await admin.ListUsersAsync(search: "Ada", cursor: firstCursor, pageSize: 10);
        await wrongFilter.Should().ThrowAsync<SqlOSCursorException>();

        interceptor.Commands.Clear();
        capturedContext.Set<SqlOSClientApplication>().AddRange(
            Enumerable.Range(0, 12).Select(index => new SqlOSClientApplication
            {
                Id = $"cli_cursor_{index:D2}",
                ClientId = $"cursor-client-{index:D2}",
                Name = index < 3 ? $"Alpha Cursor {index}" : $"Zeta Cursor {index}",
                Audience = "sqlos",
                RedirectUrisJson = "[]",
                RegistrationSource = "manual",
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            }));
        await capturedContext.SaveChangesAsync();
        interceptor.Commands.Clear();
        var clients = Serialize(await admin.ListClientsAsync(search: "Alpha Cursor", pageSize: 10));
        clients.GetProperty("data").GetArrayLength().Should().Be(3);
        clients.GetProperty("summary").GetProperty("activeCount").GetInt32().Should().Be(3);
        interceptor.Commands.Should().NotContain(command => command.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        interceptor.Commands.Should().Contain(command =>
            command.Contains("LIKE", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CHARINDEX", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Alpha Cursor", StringComparison.Ordinal));

        await setupContext.Database.EnsureDeletedAsync();
    }

    private static void AssertNoOffsetOrFullCount(IReadOnlyList<string> commands)
    {
        commands.Should().NotBeEmpty();
        commands.Should().NotContain(command => command.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        commands.Should().NotContain(command =>
            command.Contains("COUNT(*)", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("Memberships", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement Serialize(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static TestSqlOSDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new TestSqlOSDbContext(builder.Options);
    }

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
