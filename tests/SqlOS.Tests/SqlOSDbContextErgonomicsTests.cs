using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Configuration;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSDbContextErgonomicsTests
{
    [TestMethod]
    public void SqlOSDbContext_MapsSqlOSFunctions_WithoutApplicationMethods()
    {
        var options = new DbContextOptionsBuilder<RecommendedSetupTestDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SqlOS_RecommendedSetup_Test;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new RecommendedSetupTestDbContext(options);

        // The list filter's function is mapped to SqlOS's own static method: the application's context declares
        // nothing for it, and a filter built on one context instance composes into another's query.
        var function = context.Model.FindDbFunction(SqlOSFgaFunctions.ActiveSubjectsMethod);
        function.Should().NotBeNull();
        function!.Name.Should().Be("fn_ActiveSubjects");
        function.Schema.Should().Be("dbo");
        typeof(RecommendedSetupTestDbContext).GetMethod("IsResourceAccessible").Should().BeNull();
    }

    [TestMethod]
    public void AddSqlOS_WithDbContextOptions_RegistersDbContextAndSqlOSServices()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddSqlOS<RecommendedSetupTestDbContext>(
            db => db.UseInMemoryDatabase(Guid.NewGuid().ToString("N")),
            sqlos => sqlos.Fga.RootResourceId = "recommended_setup_root");

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<RecommendedSetupTestDbContext>();

        context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.InMemory");
        scope.ServiceProvider.GetRequiredService<ISqlOSAuthServerDbContext>().Should().BeSameAs(context);
        scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>().Should().BeSameAs(context);
        app.Services.GetRequiredService<IOptions<SqlOSOptions>>().Value.Fga.RootResourceId.Should().Be("recommended_setup_root");
    }

    [TestMethod]
    public void SqlOSDbContext_CanResolveConfiguredFgaOptionsFromApplicationServices()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddSqlOS<RecommendedSetupTestDbContext>(
            db => db.UseInMemoryDatabase(Guid.NewGuid().ToString("N")),
            sqlos => sqlos.Fga.MaxResourceHierarchyDepth = 3);

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RecommendedSetupTestDbContext>();

        context.GetService<IOptions<SqlOSFgaOptions>>().Value.MaxResourceHierarchyDepth.Should().Be(3);
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

    private sealed class RecommendedSetupTestDbContext(DbContextOptions<RecommendedSetupTestDbContext> options)
        : SqlOSDbContext<RecommendedSetupTestDbContext>(options)
    {
        public DbSet<RecommendedSetupEntity> Entities => Set<RecommendedSetupEntity>();

        protected override void OnApplicationModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RecommendedSetupEntity>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).HasMaxLength(64).IsRequired();
            });
        }
    }

    private sealed class RecommendedSetupEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class DefaultHookTestDbContext(DbContextOptions<DefaultHookTestDbContext> options)
        : SqlOSDbContext<DefaultHookTestDbContext>(options);
}
