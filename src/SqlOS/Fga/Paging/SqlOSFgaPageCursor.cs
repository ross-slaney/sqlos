using System.Globalization;
using SqlOS.Fga.Interfaces;
using SqlOS.Pagination;

namespace SqlOS.Fga.Paging;

/// <summary>
/// The page cursor: the last row's order values (its sort columns, then its key), encoded with
/// <see cref="SqlOSCursorCodec"/> and bound to the query's shape, so a cursor is only accepted by the query
/// that produced it.
/// </summary>
internal static class SqlOSFgaPageCursor
{
    public static string Encode<T>(SqlOSFgaPageQuery<T> query, object?[] position) where T : class, IHasResourceId
        => SqlOSCursorCodec.Encode(query.SortKey, query.Fingerprint, position.Select((v, i) => Format(v, query.OrderColumns[i].Property.ClrType)).ToList());

    public static object?[] Decode<T>(SqlOSFgaPageQuery<T> query, string cursor) where T : class, IHasResourceId
    {
        var values = SqlOSCursorCodec.Decode(cursor, query.SortKey, query.Fingerprint);
        if (values.Count != query.OrderColumns.Count)
        {
            throw new SqlOSCursorException("The cursor is invalid.");
        }

        var position = new object?[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            try
            {
                position[i] = Parse(values[i], query.OrderColumns[i].Property.ClrType);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new SqlOSCursorException("The cursor is invalid.");
            }
        }

        return position;
    }

    internal static string Format(object? value, Type type)
        => value switch
        {
            null or DBNull => throw new InvalidOperationException("A page's order columns are non-nullable."),
            DateTime d => d.Ticks.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset o => $"{o.Ticks.ToString(CultureInfo.InvariantCulture)}@{o.Offset.Ticks.ToString(CultureInfo.InvariantCulture)}",
            DateOnly d => d.DayNumber.ToString(CultureInfo.InvariantCulture),
            TimeOnly t => t.Ticks.ToString(CultureInfo.InvariantCulture),
            TimeSpan t => t.Ticks.ToString(CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToBase64String(bytes),
            bool b => b ? "1" : "0",
            string s => s,
            Guid g => g.ToString("D"),
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString("R", CultureInfo.InvariantCulture) is { } r && (value is float or double) ? r : f.ToString(null, CultureInfo.InvariantCulture) ?? "",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException($"Cannot encode a {type.Name} in a cursor."),
        };

    internal static object Parse(string value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            return Enum.ToObject(type, long.Parse(value, CultureInfo.InvariantCulture));
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.String => value,
            TypeCode.Int32 => int.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Int64 => long.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Int16 => short.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Byte => byte.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.SByte => sbyte.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.UInt32 => uint.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.UInt64 => ulong.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.UInt16 => ushort.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Decimal => decimal.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Double => double.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Single => float.Parse(value, CultureInfo.InvariantCulture),
            TypeCode.Boolean => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
            TypeCode.DateTime => new DateTime(long.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
            TypeCode.Char => value.Length == 1 ? value[0] : throw new FormatException(),
            _ when type == typeof(Guid) => Guid.Parse(value),
            _ when type == typeof(DateTimeOffset) => ParseOffset(value),
            _ when type == typeof(DateOnly) => DateOnly.FromDayNumber(int.Parse(value, CultureInfo.InvariantCulture)),
            _ when type == typeof(TimeOnly) => new TimeOnly(long.Parse(value, CultureInfo.InvariantCulture)),
            _ when type == typeof(TimeSpan) => new TimeSpan(long.Parse(value, CultureInfo.InvariantCulture)),
            _ when type == typeof(byte[]) => Convert.FromBase64String(value),
            _ => throw new FormatException($"Cannot decode a {type.Name} from a cursor."),
        };
    }

    private static DateTimeOffset ParseOffset(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            throw new FormatException();
        }

        return new DateTimeOffset(long.Parse(value[..at], CultureInfo.InvariantCulture), new TimeSpan(long.Parse(value[(at + 1)..], CultureInfo.InvariantCulture)));
    }
}
