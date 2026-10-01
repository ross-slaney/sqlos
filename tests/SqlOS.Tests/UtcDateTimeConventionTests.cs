using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;

namespace SqlOS.Tests;

/// <summary>
/// #325: every DateTime SqlOS reads through its EF model has Kind = Utc. The real-SQL regression
/// is <c>UtcTimestampIntegrationTests</c>; these prove the convention covers the whole model.
/// </summary>
[TestClass]
public sealed class UtcDateTimeConventionTests
{
    [TestMethod]
    public void Every_datetime_property_of_every_sqlos_entity_reads_as_utc()
    {
        using var context = CreateHostContext();
        var sqlosEntities = context.Model.GetEntityTypes()
            .Where(entity => entity.ClrType.Assembly == typeof(SqlOSUser).Assembly)
            .ToList();
        var dateTimeProperties = sqlosEntities
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
            .ToList();

        sqlosEntities.Should().HaveCountGreaterThan(60);
        dateTimeProperties.Should().HaveCountGreaterThan(100);
        foreach (var property in dateTimeProperties)
        {
            property.GetValueConverter().Should().BeSameAs(
                SqlOSUtcDateTimeConvention.Converter,
                $"{property.DeclaringType.ClrType.Name}.{property.Name} is read through the SqlOS model");
        }
    }

    [TestMethod]
    public void Host_entities_in_the_same_model_are_left_alone()
    {
        using var context = CreateHostContext();

        var hostProperty = context.Model.FindEntityType(typeof(HostOrder))!.FindProperty(nameof(HostOrder.PlacedAt))!;

        hostProperty.GetValueConverter().Should().BeNull("the convention only touches SqlOS entities");
    }

    [TestMethod]
    public void The_fga_only_model_entry_point_applies_the_convention_too()
    {
        using var context = new FgaOnlyContext(new DbContextOptionsBuilder<FgaOnlyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

        context.Model.FindEntityType(typeof(SqlOSFgaGrant))!.FindProperty(nameof(SqlOSFgaGrant.EffectiveTo))!
            .GetValueConverter().Should().BeSameAs(SqlOSUtcDateTimeConvention.Converter);
    }

    [TestMethod]
    public void Values_are_written_unchanged_and_read_back_as_utc()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var stored = new DateTime(2026, 8, 22, 2, 37, 0, DateTimeKind.Unspecified);
        using (var write = CreateHostContext(databaseName))
        {
            write.Set<SqlOSOrganization>().Add(new SqlOSOrganization { Id = "org_utc", Slug = "utc", Name = "UTC", CreatedAt = stored });
            write.Set<HostOrder>().Add(new HostOrder { Id = 1, PlacedAt = stored });
            write.SaveChanges();
        }

        using var read = CreateHostContext(databaseName);
        var organization = read.Set<SqlOSOrganization>().AsNoTracking().Single();
        var projected = read.Set<SqlOSOrganization>().Select(x => new { x.CreatedAt }).Single();
        var hostOrder = read.Set<HostOrder>().AsNoTracking().Single();

        organization.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        organization.CreatedAt.Ticks.Should().Be(stored.Ticks, "the converter only marks the kind");
        projected.CreatedAt.Kind.Should().Be(DateTimeKind.Utc, "projections read through the same mapping");
        JsonSerializer.Serialize(organization.CreatedAt).Should().Be("\"2026-08-22T02:37:00Z\"");
        hostOrder.PlacedAt.Kind.Should().NotBe(DateTimeKind.Utc, "host entities keep EF's default");
    }

    private static HostContext CreateHostContext(string? databaseName = null)
        => new(new DbContextOptionsBuilder<HostContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class HostContext(DbContextOptions<HostContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HostOrder>().HasKey(order => order.Id);
            modelBuilder.UseSqlOS();
        }
    }

    private sealed class FgaOnlyContext(DbContextOptions<FgaOnlyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplySqlOSFgaModel();
    }

    private sealed class HostOrder
    {
        public int Id { get; set; }

        public DateTime PlacedAt { get; set; }
    }
}
