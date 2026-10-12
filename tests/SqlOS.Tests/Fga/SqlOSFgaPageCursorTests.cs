using System.Linq.Expressions;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Paging;
using SqlOS.Pagination;

namespace SqlOS.Tests.Fga;

[TestClass]
public class SqlOSFgaPageCursorTests
{
    private PageOrderModelContext _context = null!;
    private IEntityType _products = null!;

    [TestInitialize]
    public void Setup()
    {
        _context = PageOrderModelContext.CreateProducts(_ => { });
        _products = _context.Model.FindEntityType(typeof(PagedProduct))!;
    }

    [TestCleanup]
    public void Cleanup() => _context.Dispose();

    public static IEnumerable<object[]> Values()
    {
        yield return [typeof(string), "Ünïcode, comma | pipe"];
        yield return [typeof(string), string.Empty];
        yield return [typeof(int), int.MinValue];
        yield return [typeof(long), long.MaxValue];
        yield return [typeof(decimal), 12345.6789m];
        yield return [typeof(decimal), -0.01m];
        yield return [typeof(double), 0.1 + 0.2];
        yield return [typeof(double), -1.7976931348623157E+308];
        yield return [typeof(DateTime), new DateTime(2026, 10, 12, 8, 30, 15, DateTimeKind.Utc).AddTicks(1234567)];
        yield return [typeof(DateTime), new DateTime(2026, 10, 12, 8, 30, 15, DateTimeKind.Unspecified)];
        yield return [typeof(DateTimeOffset), new DateTimeOffset(2026, 10, 12, 8, 30, 15, TimeSpan.FromHours(-7)).AddTicks(42)];
        yield return [typeof(Guid), Guid.Parse("8c5f3c9e-2a49-4d0e-9a0e-5b3d7a1c4e21")];
        yield return [typeof(bool), true];
        yield return [typeof(bool), false];
        yield return [typeof(PagedProductStatus), PagedProductStatus.Archived];
        yield return [typeof(DateOnly), new DateOnly(2026, 2, 28)];
        yield return [typeof(TimeOnly), new TimeOnly(23, 59, 58, 999)];
        yield return [typeof(TimeSpan), TimeSpan.FromMilliseconds(-1234.5)];
        yield return [typeof(byte), (byte)255];
        yield return [typeof(short), (short)-12];
        yield return [typeof(float), 3.25f];
    }

    [TestMethod]
    [DynamicData(nameof(Values), DynamicDataSourceType.Method)]
    public void FormatAndParse_RoundTrip(Type type, object value)
    {
        var text = SqlOSFgaPageCursor.Format(value);

        var parsed = SqlOSFgaPageCursor.Parse(text, type);

        parsed.Should().Be(value);
        if (value is DateTime dateTime)
        {
            ((DateTime)parsed!).Kind.Should().Be(dateTime.Kind);
        }
    }

    [TestMethod]
    public void Format_UsesInvariantRoundTripForms()
    {
        SqlOSFgaPageCursor.Format(PagedProductStatus.Published).Should().Be("1");
        SqlOSFgaPageCursor.Format(true).Should().Be("true");
        SqlOSFgaPageCursor.Format(12.5m).Should().Be("12.5");
        SqlOSFgaPageCursor.Format(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)).Should().Be("2026-01-02T03:04:05.0000000Z");
        SqlOSFgaPageCursor.Format(Guid.Parse("8c5f3c9e-2a49-4d0e-9a0e-5b3d7a1c4e21")).Should().Be("8c5f3c9e-2a49-4d0e-9a0e-5b3d7a1c4e21");
    }

    [TestMethod]
    public void EncodeAndDecode_RoundTripTheLastRowsOrderValues()
    {
        var columns = Columns("Status", "CreatedAt", "Id");
        object[] values = [PagedProductStatus.Published, new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Unspecified), Guid.NewGuid()];

        var cursor = SqlOSFgaPageCursor.Encode(_products, columns, descending: true, values);

        SqlOSFgaPageCursor.Decode(cursor, _products, columns, descending: true).Should().Equal(values);
    }

    [TestMethod]
    public void LongNonAsciiOrderValues_RoundTrip()
    {
        // 2,000 CJK characters are 6,000 UTF-8 bytes. Escaped as \uXXXX they were 12,000 characters
        // of JSON, a cursor past the admin cursors' 4,096-character limit that Decode refused.
        var columns = Columns("Name", "Id");
        object[] values = [new string('名', 2_000), Guid.NewGuid()];

        var cursor = SqlOSFgaPageCursor.Encode(_products, columns, descending: false, values);

        cursor.Length.Should().BeGreaterThan(SqlOSCursorCodec.MaxEncodedLength).And.BeLessThan(9_000);
        SqlOSFgaPageCursor.Decode(cursor, _products, columns, descending: false).Should().Equal(values);
    }

    [TestMethod]
    public void Encode_RefusesACursorDecodeWouldRefuse()
    {
        var columns = Columns("Name", "Id");

        var act = () => SqlOSFgaPageCursor.Encode(_products, columns, descending: false, [new string('x', 50_000), Guid.NewGuid()]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("The next-page cursor for PagedProduct ordered by Name,Id would be * characters, over the 65536 a cursor may have*");
    }

    [TestMethod]
    public void Decode_RejectsACursorForAnotherOrderDirectionOrEntity()
    {
        var cursor = SqlOSFgaPageCursor.Encode(_products, Columns("Price", "Id"), descending: false, [1.5m, Guid.NewGuid()]);
        var otherEntity = _context.Model.FindEntityType(typeof(PagedLocation))!;

        var otherOrder = () => SqlOSFgaPageCursor.Decode(cursor, _products, Columns("Rank", "Id"), descending: false);
        var otherDirection = () => SqlOSFgaPageCursor.Decode(cursor, _products, Columns("Price", "Id"), descending: true);
        var otherList = () => SqlOSFgaPageCursor.Decode(cursor, otherEntity, [otherEntity.FindProperty("Id")!], descending: false);

        otherOrder.Should().Throw<ArgumentException>().WithMessage("The cursor was issued for a different list*");
        otherDirection.Should().Throw<ArgumentException>().WithMessage("The cursor was issued for a different list*");
        otherList.Should().Throw<ArgumentException>().WithMessage("The cursor was issued for a different list*");
    }

    [TestMethod]
    public void Decode_RejectsAnythingThisListDidNotIssue()
    {
        var columns = Columns("Price", "Id");
        var tampered = SqlOSCursorCodec.Encode(
            SqlOSFgaPageCursor.SortKey(_products, columns, descending: false),
            SqlOSFgaPageCursor.Fingerprint(_products, columns, descending: false),
            ["not-a-number", Guid.NewGuid().ToString()]);
        var wrongCount = SqlOSCursorCodec.Encode(
            SqlOSFgaPageCursor.SortKey(_products, columns, descending: false),
            SqlOSFgaPageCursor.Fingerprint(_products, columns, descending: false),
            ["1.5"]);

        foreach (var cursor in new[] { "garbage!", "eyJ2IjoyfQ", tampered, wrongCount })
        {
            var act = () => SqlOSFgaPageCursor.Decode(cursor, _products, columns, descending: false);
            act.Should().Throw<SqlOSCursorException>().WithMessage(SqlOSFgaPageCursor.NotIssuedMessage);
        }
    }

    [TestMethod]
    public void Keyset_ForOneColumn()
    {
        Shape(["Id"], [Guid.Empty], descending: false).Should().Be("(p.Id.CompareTo(@v) > 0)");
        Shape(["Rank"], [3], descending: true).Should().Be("(p.Rank < @v)");
    }

    [TestMethod]
    public void Keyset_ForTwoColumns()
    {
        Shape(["Price", "Id"], [1.5m, Guid.Empty], descending: false)
            .Should().Be("((p.Price > @v) OrElse ((p.Price == @v) AndAlso (p.Id.CompareTo(@v) > 0)))");
        Shape(["Name", "Id"], ["b", Guid.Empty], descending: true)
            .Should().Be("((p.Name.CompareTo(@v) < 0) OrElse ((p.Name == @v) AndAlso (p.Id.CompareTo(@v) < 0)))");
    }

    [TestMethod]
    public void Keyset_ForThreeColumns()
        => Shape(["Status", "CreatedAt", "Id"], [PagedProductStatus.Published, DateTime.UnixEpoch, Guid.Empty], descending: false)
            .Should().Be("(((Convert(p.Status, Int32) > Convert(@v, Int32)) OrElse ((p.Status == @v) AndAlso (p.CreatedAt > @v))) OrElse (((p.Status == @v) AndAlso (p.CreatedAt == @v)) AndAlso (p.Id.CompareTo(@v) > 0)))");

    [TestMethod]
    public void Keyset_SelectsExactlyTheRowsAfterTheCursor_InBothDirections()
    {
        var rows = Enumerable.Range(0, 40)
            .Select(index => new PagedProduct
            {
                Id = Guid.NewGuid(),
                Rank = index % 4,
                Name = ((char)('a' + (index % 5))).ToString(),
                Status = (PagedProductStatus)(index % 3)
            })
            .ToList();
        var parameter = Expression.Parameter(typeof(PagedProduct), "p");

        foreach (var descending in new[] { false, true })
        {
            var ordered = (descending
                    ? rows.OrderByDescending(row => row.Status).ThenByDescending(row => row.Rank).ThenByDescending(row => row.Id)
                    : rows.OrderBy(row => row.Status).ThenBy(row => row.Rank).ThenBy(row => row.Id))
                .ToList();
            var cursorRow = ordered[17];
            var keyset = SqlOSFgaPageCursor.Keyset(
                [Member(parameter, "Status"), Member(parameter, "Rank"), Member(parameter, "Id")],
                [cursorRow.Status, cursorRow.Rank, cursorRow.Id],
                descending);
            var after = Expression.Lambda<Func<PagedProduct, bool>>(keyset, parameter).Compile();

            ordered.Where(after).Should().Equal(ordered.Skip(18));
        }
    }

    private IReadOnlyList<IProperty> Columns(params string[] names)
        => names.Select(name => _products.FindProperty(name)!).ToArray();

    private static string Shape(string[] columns, object[] values, bool descending)
    {
        var parameter = Expression.Parameter(typeof(PagedProduct), "p");
        var keyset = SqlOSFgaPageCursor.Keyset(columns.Select(column => Member(parameter, column)).ToArray(), values, descending);
        return Regex.Replace(keyset.ToString(), @"value\([^)]*\)\.Value", "@v");
    }

    private static Expression Member(ParameterExpression parameter, string name)
        => Expression.Property(parameter, name);
}
