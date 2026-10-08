using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The order values a page sends back to the database as JSON, typed by the column's store type on the
/// engine's side: a timestamp must come back as the instant or the wall clock it was, whatever the session's
/// time zone.
/// </summary>
[TestClass]
public class SqlOSFgaPageValuesTests
{
    private static readonly DateTime TenUtc = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void PostgreSql_AnInstantColumn_GetsItsInstantInUtc()
    {
        const string instant = "timestamp with time zone";
        SqlOSFgaPageValues.Timestamp(TenUtc, instant, SqlOSDatabaseProviderKind.PostgreSql).Should().Be("2026-01-01T10:00:00.0000000Z", "a UTC value names its zone, so the session's does not apply");
        SqlOSFgaPageValues.Timestamp(TenUtc.ToLocalTime(), instant, SqlOSDatabaseProviderKind.PostgreSql).Should().Be("2026-01-01T10:00:00.0000000Z", "a local value (what Npgsql's compatibility mode reads an instant column as) is the same instant");
        SqlOSFgaPageValues.Timestamp(DateTime.SpecifyKind(TenUtc.ToLocalTime(), DateTimeKind.Unspecified), instant, SqlOSDatabaseProviderKind.PostgreSql).Should().Be("2026-01-01T10:00:00.0000000Z", "an unspecified value is taken as local, as Npgsql's compatibility mode takes it");
        SqlOSFgaPageValues.Timestamp(TenUtc, "timestamp(3) with time zone", SqlOSDatabaseProviderKind.PostgreSql).Should().EndWith("Z");
        SqlOSFgaPageValues.Timestamp(TenUtc, "timestamptz", SqlOSDatabaseProviderKind.PostgreSql).Should().EndWith("Z");
    }

    [TestMethod]
    public void AColumnWithoutAZone_GetsTheWallClock_OnBothEngines()
    {
        var unspecified = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Unspecified);
        SqlOSFgaPageValues.Timestamp(unspecified, "timestamp without time zone", SqlOSDatabaseProviderKind.PostgreSql).Should().Be("2026-01-01T10:00:00.0000000");
        SqlOSFgaPageValues.Timestamp(TenUtc, "timestamp without time zone", SqlOSDatabaseProviderKind.PostgreSql).Should().Be("2026-01-01T10:00:00.0000000", "a plain timestamp holds a wall clock, and a zone would be ignored anyway");
        SqlOSFgaPageValues.Timestamp(unspecified, "datetime2", SqlOSDatabaseProviderKind.SqlServer).Should().Be("2026-01-01T10:00:00.0000000");
        SqlOSFgaPageValues.Timestamp(TenUtc, "datetime2", SqlOSDatabaseProviderKind.SqlServer).Should().Be("2026-01-01T10:00:00.0000000", "SqlClient sends the wall clock whatever the kind");
        SqlOSFgaPageValues.Timestamp(TenUtc, "datetime", SqlOSDatabaseProviderKind.SqlServer).Should().NotEndWith("Z");
    }

    [TestMethod]
    public void EveryValue_IsWrittenUnderItsName()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            SqlOSFgaPageValues.Write(writer, "c0", TenUtc, "timestamp with time zone", SqlOSDatabaseProviderKind.PostgreSql);
            SqlOSFgaPageValues.Write(writer, "c1", 12.5m, "numeric(10,2)", SqlOSDatabaseProviderKind.PostgreSql);
            SqlOSFgaPageValues.Write(writer, "c2", "item000", "character varying(64)", SqlOSDatabaseProviderKind.PostgreSql);
            SqlOSFgaPageValues.Write(writer, "c3", null, "integer", SqlOSDatabaseProviderKind.PostgreSql);
            SqlOSFgaPageValues.Write(writer, "c4", new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)), "datetimeoffset", SqlOSDatabaseProviderKind.SqlServer);
            SqlOSFgaPageValues.Write(writer, "c5", new byte[] { 1, 255 }, "bytea", SqlOSDatabaseProviderKind.PostgreSql);
            writer.WriteEndObject();
        }

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        var values = json.RootElement;
        values.GetProperty("c0").GetString().Should().Be("2026-01-01T10:00:00.0000000Z");
        values.GetProperty("c1").GetDecimal().Should().Be(12.5m);
        values.GetProperty("c2").GetString().Should().Be("item000");
        values.GetProperty("c3").ValueKind.Should().Be(JsonValueKind.Null);
        values.GetProperty("c4").GetString().Should().Be("2026-01-01T10:00:00.0000000+02:00", "an offset value carries its offset");
        values.GetProperty("c5").GetString().Should().Be("\\x01FF", "PostgreSQL reads bytes as hex");
    }
}
