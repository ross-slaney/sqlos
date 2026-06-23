using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSDbContextErgonomicsTests
{
    [TestMethod]
    public void SqlOSDbContext_RegistersInheritedTvfOnRelationalContext()
    {
        var options = new DbContextOptionsBuilder<EasyModeTestDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SqlOS_EasyMode_Test;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new EasyModeTestDbContext(options);

        var tvfMethod = typeof(EasyModeTestDbContext).GetMethod(
            nameof(ISqlOSFgaDbContext.IsResourceAccessible),
            [typeof(string), typeof(string), typeof(string)]);

        tvfMethod.Should().NotBeNull();

        var dbFunction = context.Model.FindDbFunction(tvfMethod!);
        dbFunction.Should().NotBeNull();
        dbFunction!.Name.Should().Be("fn_IsResourceAccessible");
        dbFunction.Schema.Should().Be("dbo");
    }

    [TestMethod]
    public void AddSqlOS_WithDbContextOptions_RegistersDbContextAndSqlOSServices()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddSqlOS<EasyModeTestDbContext>(
            db => db.UseInMemoryDatabase(Guid.NewGuid().ToString("N")),
            sqlos => sqlos.Fga.RootResourceId = "easy_mode_root");

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<EasyModeTestDbContext>();

        context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.InMemory");
        scope.ServiceProvider.GetRequiredService<ISqlOSAuthServerDbContext>().Should().BeSameAs(context);
        scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>().Should().BeSameAs(context);
        app.Services.GetRequiredService<IOptions<SqlOSOptions>>().Value.Fga.RootResourceId.Should().Be("easy_mode_root");
    }

    [TestMethod]
    public void SqlOSDbContext_AllowsDefaultApplicationModelHook()
    {
        var options = new DbContextOptionsBuilder<DefaultHookTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        using var context = new DefaultHookTestDbContext(options);

        context.Model.GetEntityTypes().Should().NotBeEmpty();
    }

    [TestMethod]
    public void SqlOSDbContext_ConstrainsTypeParameterToDerivedContext()
    {
        var typeParameter = typeof(SqlOSDbContext<>).GetGenericArguments().Should().ContainSingle().Subject;
        var constraint = typeParameter.GetGenericParameterConstraints().Should().ContainSingle().Subject;

        constraint.IsGenericType.Should().BeTrue();
        constraint.GetGenericTypeDefinition().Should().Be(typeof(SqlOSDbContext<>));
    }

    private sealed class EasyModeTestDbContext(DbContextOptions<EasyModeTestDbContext> options)
        : SqlOSDbContext<EasyModeTestDbContext>(options)
    {
        public DbSet<EasyModeEntity> Entities => Set<EasyModeEntity>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EasyModeEntity>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).HasMaxLength(64).IsRequired();
            });
        }
    }

    private sealed class EasyModeEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class DefaultHookTestDbContext(DbContextOptions<DefaultHookTestDbContext> options)
        : SqlOSDbContext<DefaultHookTestDbContext>(options);
}
