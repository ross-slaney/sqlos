using System.Reflection;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>
/// Builds an entity the way a stored row reads back: through its private constructor, with the
/// given column values. Tests use it for states SqlOS's own methods never produce today but that
/// rows written by an earlier version can hold (an unverified account linked to a SAML identity
/// before #420), and for rows a test needs with exact values. Everything else arranges state through
/// the aggregates' methods, as SqlOS does.
/// </summary>
internal static class TestRows
{
    public static T Create<T>(object columns)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(columns);
        var entity = (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;
        foreach (var column in columns.GetType().GetProperties())
        {
            var property = typeof(T).GetProperty(column.Name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"{typeof(T).Name} has no column '{column.Name}'.", nameof(columns));
            property.SetValue(entity, column.GetValue(columns));
        }

        return entity;
    }
}
