using System.Globalization;
using System.Text.Json;
using SqlOS.Database;

namespace SqlOS.Fga.Paging;

/// <summary>
/// The order values a page sends to the database as JSON (a stream's position, a held row's position), each
/// written so that the engine reads back exactly the value the executor holds once it types the JSON by the
/// column's store type.
/// </summary>
internal static class SqlOSFgaPageValues
{
    public static void Write(Utf8JsonWriter writer, string name, object? value, string storeType, SqlOSDatabaseProviderKind kind)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNull(name);
                break;
            case int i:
                writer.WriteNumber(name, i);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case short s:
                writer.WriteNumber(name, s);
                break;
            case byte b:
                writer.WriteNumber(name, b);
                break;
            case decimal d:
                writer.WriteNumber(name, d);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case float f:
                writer.WriteNumber(name, f);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case Guid g:
                writer.WriteString(name, g.ToString("D"));
                break;
            case DateTime dt:
                writer.WriteString(name, Timestamp(dt, storeType, kind));
                break;
            case DateTimeOffset dto:
                writer.WriteString(name, dto.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
                break;
            case DateOnly d:
                writer.WriteString(name, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly t:
                writer.WriteString(name, t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                writer.WriteString(name, kind == SqlOSDatabaseProviderKind.PostgreSql ? "\\x" + Convert.ToHexString(bytes) : Convert.ToBase64String(bytes));
                break;
            default:
                writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>
    /// A timestamp literal the engine reads as the value it is. PostgreSQL reads a literal that names no zone
    /// in the session's time zone, so a value for an instant column (<c>timestamp with time zone</c>) names
    /// its instant in UTC with <c>Z</c>: a UTC value as it is, a local one converted, an unspecified one
    /// taken as local, which is what Npgsql's compatibility mode (the one SqlOS enables, which also reads
    /// such a column back as local time) does with it. A column without a zone gets the wall clock, on
    /// both engines: SQL Server's datetime types carry no zone, and SqlClient sends the wall clock whatever
    /// the kind.
    /// </summary>
    public static string Timestamp(DateTime value, string storeType, SqlOSDatabaseProviderKind kind)
    {
        if (kind == SqlOSDatabaseProviderKind.PostgreSql && IsInstant(storeType))
        {
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        }

        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
    }

    /// <summary><c>timestamp with time zone</c>, with or without a precision, or its alias <c>timestamptz</c>.</summary>
    private static bool IsInstant(string storeType)
        => storeType.StartsWith("timestamptz", StringComparison.OrdinalIgnoreCase)
           || (storeType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase) && storeType.Contains("with time zone", StringComparison.OrdinalIgnoreCase));
}
