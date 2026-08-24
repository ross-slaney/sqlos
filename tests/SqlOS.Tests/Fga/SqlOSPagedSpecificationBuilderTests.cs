using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Specifications;

namespace SqlOS.Tests.Fga;

[TestClass]
public sealed class SqlOSPagedSpecificationBuilderTests
{
    [TestMethod]
    public void Build_WithoutSort_Throws()
    {
        var act = () => PagedSpec.For<SampleRow>(r => r.Id).Build(10, descending: false);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No sort registered*");
    }

    [TestMethod]
    public void SortByString_IsAliasForStringSortBy()
    {
        var rows = StringRows();
        AssertCursorPaging(rows, b => b.SortByString("name", r => r.Name), r => r.Name, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("name", r => r.Name), r => r.Name, descending: false);
    }

    [TestMethod]
    public void SortBy_String_RoundTripsCursorAscendingAndDescending()
    {
        AssertCursorPaging(StringRows(), b => b.SortBy("name", r => r.Name), r => r.Name, descending: false);
        AssertCursorPaging(StringRows(), b => b.SortBy("name", r => r.Name), r => r.Name, descending: true);
    }

    [TestMethod]
    public void SortBy_Int_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.IntValue = 1),
            Row("b", r => r.IntValue = 2),
            Row("c", r => r.IntValue = 3),
            Row("d", r => r.IntValue = 4)
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.IntValue), r => r.IntValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.IntValue), r => r.IntValue, descending: true);
    }

    [TestMethod]
    public void SortBy_Long_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.LongValue = 10L),
            Row("b", r => r.LongValue = 20L),
            Row("c", r => r.LongValue = 30L),
            Row("d", r => r.LongValue = 40L)
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.LongValue), r => r.LongValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.LongValue), r => r.LongValue, descending: true);
    }

    [TestMethod]
    public void SortBy_Decimal_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.DecimalValue = 1.1m),
            Row("b", r => r.DecimalValue = 2.2m),
            Row("c", r => r.DecimalValue = 3.3m),
            Row("d", r => r.DecimalValue = 4.4m)
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DecimalValue), r => r.DecimalValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DecimalValue), r => r.DecimalValue, descending: true);
    }

    [TestMethod]
    public void SortBy_Double_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.DoubleValue = 1.25),
            Row("b", r => r.DoubleValue = Math.PI),
            Row("c", r => r.DoubleValue = 4.5),
            Row("d", r => r.DoubleValue = 9.75)
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DoubleValue), r => r.DoubleValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DoubleValue), r => r.DoubleValue, descending: true);
    }

    [TestMethod]
    public void SortBy_DateTime_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.DateTimeValue = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row("b", r => r.DateTimeValue = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row("c", r => r.DateTimeValue = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row("d", r => r.DateTimeValue = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc))
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DateTimeValue), r => r.DateTimeValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DateTimeValue), r => r.DateTimeValue, descending: true);
    }

    [TestMethod]
    public void SortBy_DateTimeOffset_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.DateTimeOffsetValue = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Row("b", r => r.DateTimeOffsetValue = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.FromHours(-7))),
            Row("c", r => r.DateTimeOffsetValue = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.FromHours(2))),
            Row("d", r => r.DateTimeOffsetValue = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero))
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DateTimeOffsetValue), r => r.DateTimeOffsetValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DateTimeOffsetValue), r => r.DateTimeOffsetValue, descending: true);
    }

    [TestMethod]
    public void SortBy_DateOnly_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.DateOnlyValue = new DateOnly(2024, 1, 1)),
            Row("b", r => r.DateOnlyValue = new DateOnly(2024, 2, 1)),
            Row("c", r => r.DateOnlyValue = new DateOnly(2024, 3, 1)),
            Row("d", r => r.DateOnlyValue = new DateOnly(2024, 4, 1))
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DateOnlyValue), r => r.DateOnlyValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.DateOnlyValue), r => r.DateOnlyValue, descending: true);
    }

    [TestMethod]
    public void SortBy_Guid_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.GuidValue = Guid.Parse("00000000-0000-0000-0000-000000000001")),
            Row("b", r => r.GuidValue = Guid.Parse("00000000-0000-0000-0000-000000000002")),
            Row("c", r => r.GuidValue = Guid.Parse("00000000-0000-0000-0000-000000000003")),
            Row("d", r => r.GuidValue = Guid.Parse("00000000-0000-0000-0000-000000000004"))
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.GuidValue), r => r.GuidValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.GuidValue), r => r.GuidValue, descending: true);
    }

    [TestMethod]
    public void SortBy_Bool_RoundTripsCursorAscendingAndDescending()
    {
        var rows = new[]
        {
            Row("a", r => r.BoolValue = false),
            Row("b", r => r.BoolValue = false),
            Row("c", r => r.BoolValue = true),
            Row("d", r => r.BoolValue = true)
        };
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.BoolValue), r => r.BoolValue, descending: false);
        AssertCursorPaging(rows, b => b.SortBy("n", r => r.BoolValue), r => r.BoolValue, descending: true);
    }

    [TestMethod]
    [DataRow(DateTimeKind.Utc)]
    [DataRow(DateTimeKind.Local)]
    [DataRow(DateTimeKind.Unspecified)]
    public void SortBy_DateTime_PreservesKindAndTicksThroughCursor(DateTimeKind kind)
    {
        var value = new DateTime(2024, 6, 15, 12, 30, 45, 123, kind).AddTicks(4567);
        var entity = Row("dt1", r => r.DateTimeValue = value);
        var spec = PagedSpec.For<SampleRow>(r => r.Id)
            .SortBy("createdAt", r => r.DateTimeValue)
            .Build(10, descending: false);
        var cursor = spec.BuildCursor(entity);
        var (sortValue, id) = DecodeCursorEnvelope(cursor);

        id.Should().Be("dt1");
        var parsed = DateTime.Parse(sortValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        parsed.Kind.Should().Be(kind);
        parsed.Ticks.Should().Be(value.Ticks);
    }

    [TestMethod]
    public void DuplicateSortValues_AreResolvedByIdTiebreaker()
    {
        var rows = new[]
        {
            Row("a", r => r.IntValue = 5),
            Row("b", r => r.IntValue = 5),
            Row("c", r => r.IntValue = 5),
            Row("d", r => r.IntValue = 6)
        };

        var spec = PagedSpec.For<SampleRow>(r => r.Id)
            .SortBy("n", r => r.IntValue)
            .Build(2, descending: false);
        var firstPage = spec.ApplySort(rows.AsQueryable()).Take(2).ToList();
        firstPage.Select(r => r.Id).Should().Equal("a", "b");

        var cursor = spec.BuildCursor(firstPage[^1]);
        var afterCursor = spec.GetCursorFilter(cursor);
        var secondPage = spec.ApplySort(rows.AsQueryable().Where(afterCursor)).Take(2).ToList();
        secondPage.Select(r => r.Id).Should().Equal("c", "d");

        var seen = firstPage.Concat(secondPage).Select(r => r.Id).ToList();
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(rows.Select(r => r.Id));
    }

    [TestMethod]
    public void Search_NullOrWhitespace_IsNoOp()
    {
        var rows = SearchRows();
        foreach (var search in new[] { null, "", "   " })
        {
            var spec = PagedSpec.For<SampleRow>(r => r.Id)
                .SortBy("name", r => r.Name)
                .Search(search, r => r.Name, r => r.Description)
                .Build(10, descending: false);
            var filter = spec.ToExpression().Compile();
            rows.Where(filter).Select(r => r.Id).Should().Equal(rows.Select(r => r.Id));
        }
    }

    [TestMethod]
    public void Search_OrCombinesStringPropertiesCaseInsensitively()
    {
        var spec = PagedSpec.For<SampleRow>(r => r.Id)
            .SortBy("name", r => r.Name)
            .Search("alpha", r => r.Name, r => r.Description)
            .Build(10, descending: false);
        var filter = spec.ToExpression().Compile();
        SearchRows().Where(filter).Select(r => r.Id).Should().BeEquivalentTo("1", "2");
    }

    [TestMethod]
    public void SortBy_CustomSerializerEscapeHatch_StillWorks()
    {
        var rows = new[]
        {
            Row("a", r => r.Rank = new ColorRank(2)),
            Row("b", r => r.Rank = new ColorRank(1)),
            Row("c", r => r.Rank = new ColorRank(3)),
            Row("d", r => r.Rank = new ColorRank(4))
        };

        AssertCursorPaging(
            rows,
            b => b.SortBy(
                "rank",
                r => r.Rank,
                serialize: rank => rank.Value.ToString(CultureInfo.InvariantCulture),
                deserialize: value => new ColorRank(int.Parse(value, CultureInfo.InvariantCulture))),
            r => r.Rank,
            descending: false);
        AssertCursorPaging(
            rows,
            b => b.SortBy(
                "rank",
                r => r.Rank,
                serialize: rank => rank.Value.ToString(CultureInfo.InvariantCulture),
                deserialize: value => new ColorRank(int.Parse(value, CultureInfo.InvariantCulture))),
            r => r.Rank,
            descending: true);
    }

    [TestMethod]
    public void StringCursor_UsesUnchangedBase64NewlineEnvelope()
    {
        var entity = Row("id-1", r => r.Name = "Ada");
        var spec = PagedSpec.For<SampleRow>(r => r.Id)
            .SortBy("name", r => r.Name)
            .Build(10, descending: false);
        var cursor = spec.BuildCursor(entity);
        Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Should().Be("Ada\nid-1");
    }

    private static void AssertCursorPaging<TKey>(
        IReadOnlyList<SampleRow> rows,
        Func<PagedSpecificationBuilder<SampleRow>, PagedSpecificationBuilder<SampleRow>> configure,
        Func<SampleRow, TKey> key,
        bool descending)
        where TKey : IComparable<TKey>
    {
        var ordered = descending
            ? rows.OrderByDescending(key).ThenByDescending(r => r.Id).ToList()
            : rows.OrderBy(key).ThenBy(r => r.Id).ToList();

        var spec = configure(PagedSpec.For<SampleRow>(r => r.Id)).Build(10, descending: descending);
        var cursor = spec.BuildCursor(ordered[1]);
        var remaining = ordered.Where(spec.GetCursorFilter(cursor).Compile()).ToList();
        remaining.Should().Equal(ordered.Skip(2));
    }

    private static (string SortValue, string Id) DecodeCursorEnvelope(string cursor)
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var parts = decoded.Split('\n', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }

    private static SampleRow[] StringRows() =>
    [
        Row("a", r => r.Name = "alpha"),
        Row("b", r => r.Name = "bravo"),
        Row("c", r => r.Name = "charlie"),
        Row("d", r => r.Name = "delta")
    ];

    private static SampleRow[] SearchRows() =>
    [
        Row("1", r => { r.Name = "Alpha"; r.Description = "zzz"; }),
        Row("2", r => { r.Name = "beta"; r.Description = "ALPHA note"; }),
        Row("3", r => { r.Name = "gamma"; r.Description = "other"; })
    ];

    private static SampleRow Row(string id, Action<SampleRow> configure)
    {
        var row = new SampleRow { Id = id };
        configure(row);
        return row;
    }

    private sealed class SampleRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int IntValue { get; set; }
        public long LongValue { get; set; }
        public decimal DecimalValue { get; set; }
        public double DoubleValue { get; set; }
        public DateTime DateTimeValue { get; set; }
        public DateTimeOffset DateTimeOffsetValue { get; set; }
        public DateOnly DateOnlyValue { get; set; }
        public Guid GuidValue { get; set; }
        public bool BoolValue { get; set; }
        public ColorRank Rank { get; set; }
    }

    private readonly record struct ColorRank(int Value) : IComparable<ColorRank>
    {
        public int CompareTo(ColorRank other) => Value.CompareTo(other.Value);
    }
}
