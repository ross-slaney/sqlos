using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Pagination;

namespace SqlOS.Fga.Paging;

/// <summary>
/// The <c>ToPageAsync</c> cursor: the last row's order values, bound to the entity, the order
/// columns and the direction through <see cref="SqlOSCursorCodec"/>.
/// </summary>
internal static class SqlOSFgaPageCursor
{
    public const string NotIssuedMessage = "The cursor is not one this list issued.";

    private static readonly HashSet<Type> SupportedTypes =
    [
        typeof(string),
        typeof(bool),
        typeof(Guid),
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(DateOnly),
        typeof(TimeOnly),
        typeof(TimeSpan),
    ];

    /// <summary>Whether a cursor can carry values of a column of this type.</summary>
    public static bool IsSupported(Type type)
        => type.IsEnum || SupportedTypes.Contains(type);

    public static string SortKey(IEntityType entityType, IReadOnlyList<IProperty> columns, bool descending)
        => $"{entityType.ClrType.Name}|{ColumnList(columns)}|{Direction(descending)}";

    public static string Fingerprint(IEntityType entityType, IReadOnlyList<IProperty> columns, bool descending)
        => SqlOSCursorCodec.Fingerprint(entityType.Name, ColumnList(columns), Direction(descending));

    public static string Encode(
        IEntityType entityType,
        IReadOnlyList<IProperty> columns,
        bool descending,
        IReadOnlyList<object> values)
        => SqlOSCursorCodec.Encode(
            SortKey(entityType, columns, descending),
            Fingerprint(entityType, columns, descending),
            values.Select(Format).ToArray());

    /// <summary>
    /// Reads the order values back. A cursor issued for another entity, order or direction is an
    /// <see cref="ArgumentException"/>; anything that is not a cursor this list issued is a
    /// <see cref="SqlOSCursorException"/>.
    /// </summary>
    public static object[] Decode(
        string cursor,
        IEntityType entityType,
        IReadOnlyList<IProperty> columns,
        bool descending)
    {
        if (!SqlOSCursorCodec.TryRead(cursor, out var sortKey, out var fingerprint, out var texts))
        {
            throw new SqlOSCursorException(NotIssuedMessage);
        }

        if (!string.Equals(sortKey, SortKey(entityType, columns, descending), StringComparison.Ordinal)
            || !string.Equals(fingerprint, Fingerprint(entityType, columns, descending), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The cursor was issued for a different list. This list is {entityType.ClrType.Name} ordered by {ColumnList(columns)} {Direction(descending)}.",
                nameof(cursor));
        }

        if (texts.Count != columns.Count)
        {
            throw new SqlOSCursorException(NotIssuedMessage);
        }

        var values = new object[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            values[index] = Parse(texts[index], columns[index].ClrType)
                ?? throw new SqlOSCursorException(NotIssuedMessage);
        }

        return values;
    }

    internal static string Format(object value)
        => value switch
        {
            string text => text,
            bool flag => flag ? "true" : "false",
            Guid guid => guid.ToString("D"),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            Enum enumValue => Format(Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()), CultureInfo.InvariantCulture)),
            IFormattable formattable when IsSupported(value.GetType()) => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"A cursor cannot carry a value of type {value.GetType().Name}.", nameof(value)),
        };

    /// <summary>Parses a formatted value, or returns <see langword="null"/> when it does not parse.</summary>
    internal static object? Parse(string text, Type type)
    {
        const NumberStyles integer = NumberStyles.AllowLeadingSign;
        const NumberStyles real = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;

        if (type.IsEnum)
        {
            var underlying = Parse(text, Enum.GetUnderlyingType(type));
            return underlying == null ? null : Enum.ToObject(type, underlying);
        }

        return type switch
        {
            _ when type == typeof(string) => text,
            _ when type == typeof(bool) => text switch { "true" => true, "false" => false, _ => null },
            _ when type == typeof(Guid) => Guid.TryParseExact(text, "D", out var guid) ? guid : null,
            _ when type == typeof(byte) => byte.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(sbyte) => sbyte.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(short) => short.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(ushort) => ushort.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(int) => int.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(uint) => uint.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(long) => long.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(ulong) => ulong.TryParse(text, integer, culture, out var value) ? value : null,
            _ when type == typeof(float) => float.TryParse(text, real, culture, out var value) ? value : null,
            _ when type == typeof(double) => double.TryParse(text, real, culture, out var value) ? value : null,
            _ when type == typeof(decimal) => decimal.TryParse(text, real, culture, out var value) ? value : null,
            _ when type == typeof(DateTime) => DateTime.TryParseExact(text, "O", culture, DateTimeStyles.RoundtripKind, out var value) ? value : null,
            _ when type == typeof(DateTimeOffset) => DateTimeOffset.TryParseExact(text, "O", culture, DateTimeStyles.None, out var value) ? value : null,
            _ when type == typeof(DateOnly) => DateOnly.TryParseExact(text, "O", culture, DateTimeStyles.None, out var value) ? value : null,
            _ when type == typeof(TimeOnly) => TimeOnly.TryParseExact(text, "O", culture, DateTimeStyles.None, out var value) ? value : null,
            _ when type == typeof(TimeSpan) => TimeSpan.TryParseExact(text, "c", culture, out var value) ? value : null,
            _ => null,
        };
    }

    /// <summary>
    /// The rows after the cursor: <c>c1 &gt; v1 || (c1 == v1 &amp;&amp; c2 &gt; v2) || ... || (c1 == v1 &amp;&amp; ... &amp;&amp; cn &gt; vn)</c>,
    /// with <c>&lt;</c> for a descending order. The columns are non-nullable, so no null handling is needed.
    /// The values become query parameters.
    /// </summary>
    internal static Expression Keyset(IReadOnlyList<Expression> columns, IReadOnlyList<object> values, bool descending)
    {
        if (columns.Count == 0 || columns.Count != values.Count)
        {
            throw new ArgumentException("A keyset needs one value per column.", nameof(values));
        }

        Expression? keyset = null;
        Expression? equalPrefix = null;
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var value = QueryParameter(values[index], column.Type);
            var after = Compare(column, value, descending);
            var term = equalPrefix == null ? after : Expression.AndAlso(equalPrefix, after);
            keyset = keyset == null ? term : Expression.OrElse(keyset, term);
            var equal = Expression.Equal(column, value);
            equalPrefix = equalPrefix == null ? equal : Expression.AndAlso(equalPrefix, equal);
        }

        return keyset!;
    }

    /// <summary>
    /// <c>column &gt; value</c> (<c>&lt;</c> when descending). Strings, Guid and bool compare through
    /// <c>CompareTo</c> and enums through their underlying type, which EF Core translates to the
    /// plain comparison.
    /// </summary>
    internal static Expression Compare(Expression column, Expression value, bool descending)
    {
        var type = column.Type;
        if (type == typeof(string) || type == typeof(Guid) || type == typeof(bool))
        {
            var compareTo = Expression.Call(column, type.GetMethod(nameof(IComparable<object>.CompareTo), [type])!, value);
            var zero = Expression.Constant(0);
            return descending ? Expression.LessThan(compareTo, zero) : Expression.GreaterThan(compareTo, zero);
        }

        if (type.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(type);
            column = Expression.Convert(column, underlying);
            value = Expression.Convert(value, underlying);
        }

        return descending ? Expression.LessThan(column, value) : Expression.GreaterThan(column, value);
    }

    /// <summary>A value EF Core sends as a query parameter rather than a literal.</summary>
    internal static Expression QueryParameter(object value, Type type)
    {
        var box = Activator.CreateInstance(typeof(StrongBox<>).MakeGenericType(type), value)!;
        return Expression.Field(Expression.Constant(box), nameof(StrongBox<object>.Value));
    }

    private static string ColumnList(IReadOnlyList<IProperty> columns)
        => string.Join(",", columns.Select(column => column.Name));

    private static string Direction(bool descending)
        => descending ? "desc" : "asc";
}
