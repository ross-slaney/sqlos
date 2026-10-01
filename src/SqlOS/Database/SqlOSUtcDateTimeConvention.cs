using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SqlOS.Database;

/// <summary>
/// The UTC convention (#325): every <see cref="DateTime"/> SqlOS reads through its EF model has
/// <see cref="DateTimeKind.Utc"/>.
/// </summary>
/// <remarks>
/// SqlOS stores UTC in <c>datetime2</c> (SQL Server) and <c>timestamp without time zone</c>
/// (PostgreSQL), which both read back as <see cref="DateTimeKind.Unspecified"/>. JSON then wrote
/// those values without a <c>Z</c>, and browsers read UTC as local time. A converter on every
/// <see cref="DateTime"/> and <see cref="Nullable{DateTime}"/> property of every SqlOS entity marks
/// values read from the database as UTC; values are written unchanged. Only types from the SqlOS
/// assembly are touched, never the host's own entities, and a property that already has a
/// converter keeps it. The convention runs after the SqlOS model is configured, so new entities
/// pick it up without per-property code.
/// </remarks>
internal static class SqlOSUtcDateTimeConvention
{
    internal static readonly ValueConverter<DateTime, DateTime> Converter = new(
        value => value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static ModelBuilder ApplyUtcDateTimeConvention(this ModelBuilder modelBuilder)
    {
        var sqlosAssembly = typeof(SqlOSUtcDateTimeConvention).Assembly;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.ClrType.Assembly != sqlosAssembly)
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                if ((property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    && property.GetValueConverter() is null)
                {
                    property.SetValueConverter(Converter);
                }
            }
        }

        return modelBuilder;
    }
}
