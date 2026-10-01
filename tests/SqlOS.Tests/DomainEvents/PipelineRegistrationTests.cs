using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.Database;
using SqlOS.Domain;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.DomainEvents;

/// <summary>
/// What <c>AddSqlOS</c> registers for the pipeline. The real-SQL registration paths are in
/// <c>DomainEventPipelineIntegrationTests</c>.
/// </summary>
[TestClass]
public sealed class PipelineRegistrationTests
{
    [TestMethod]
    public void AddSqlOS_registers_the_clock_the_projection_the_request_scope_and_the_recorder()
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();

        services.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        services.GetRequiredService<SqlOSAuditProjection>().Should().BeSameAs(SqlOSAuditProjection.Default);
        scope.ServiceProvider.GetRequiredService<SqlOSRequestContextAccessor>().Current.Should().BeSameAs(SqlOSRequestContext.System);
        scope.ServiceProvider.GetRequiredService<IAuditRecorder>().Should().BeOfType<SqlOSAuditRecorder>();
    }

    [TestMethod]
    public void AddSqlOS_attaches_one_interceptor_to_the_hosts_context()
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();

        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors!
            .Count(interceptor => interceptor is SqlOSDomainEventsInterceptor).Should().Be(1);
    }

    [TestMethod]
    public void A_host_clock_registered_before_AddSqlOS_wins()
    {
        var hostClock = new FixedClock();
        using var services = CreateServices(beforeSqlOS: collection => collection.AddSingleton<TimeProvider>(hostClock));

        services.GetRequiredService<TimeProvider>().Should().BeSameAs(hostClock);
    }

    [TestMethod]
    public void A_host_clock_registered_after_AddSqlOS_wins()
    {
        var hostClock = new FixedClock();
        using var services = CreateServices(afterSqlOS: collection => collection.AddSingleton<TimeProvider>(hostClock));

        services.GetRequiredService<TimeProvider>().Should().BeSameAs(hostClock);
    }

    [TestMethod]
    public void The_recorder_stages_failures_on_the_scopes_unit_of_work()
    {
        using var services = CreateServices();
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();

        scope.ServiceProvider.GetRequiredService<IAuditRecorder>().Record(new DepositRejected("acc", "limit"));

        SqlOSUnitOfWorkEvents.TryGet(context, out var buffer).Should().BeTrue();
        buffer!.Pending.Should().Equal(new DepositRejected("acc", "limit"));
        FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<IAuditRecorder>().Record(null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static ServiceProvider CreateServices(
        Action<IServiceCollection>? beforeSqlOS = null,
        Action<IServiceCollection>? afterSqlOS = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestSqlOSInMemoryDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        beforeSqlOS?.Invoke(services);
        services.AddSqlOS<TestSqlOSInMemoryDbContext>(options => options.AuthServer.Issuer = "https://tests.example/sqlos/auth");
        afterSqlOS?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    }
}
