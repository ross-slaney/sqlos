using System.Globalization;

namespace SqlOS.Fga.Specifications;

internal static class CursorSerializers
{
    public static string Serialize(string value) => value;
    public static string DeserializeString(string value) => value;

    public static string Serialize(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static int DeserializeInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    public static string Serialize(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static long DeserializeLong(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    public static string Serialize(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    public static decimal DeserializeDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    public static string Serialize(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static double DeserializeDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    public static string Serialize(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);
    public static DateTime DeserializeDateTime(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string Serialize(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset DeserializeDateTimeOffset(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static string Serialize(DateOnly value) => value.ToString("O", CultureInfo.InvariantCulture);
    public static DateOnly DeserializeDateOnly(string value)
        => DateOnly.ParseExact(value, "O", CultureInfo.InvariantCulture);

    public static string Serialize(Guid value) => value.ToString("D");
    public static Guid DeserializeGuid(string value) => Guid.Parse(value);

    public static string Serialize(bool value) => value.ToString();
    public static bool DeserializeBool(string value) => bool.Parse(value);
}
