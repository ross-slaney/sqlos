using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The keyset predicates SqlOS reads out of a page query and turns into the position every stream seeks to,
/// and the predicates it leaves alone (tested row by row instead).
/// </summary>
[TestClass]
public class SqlOSFgaKeysetTests
{
    private sealed class Row
    {
        public int Rank { get; set; }

        public string Id { get; set; } = string.Empty;

        public DateTime At { get; set; }

        public Guid Key { get; set; }
    }

    private static bool Read(Expression<Func<Row, bool>> filter, string[] order, bool descending, out object?[] after)
        => SqlOSFgaKeyset.TryRead(filter, order, descending, out after);

    [TestMethod]
    public void OneColumn_IsAStrictComparison_WrittenEitherWay()
    {
        Read(r => r.Rank > 5, ["Rank"], false, out var after).Should().BeTrue();
        after.Should().Equal(5);

        Read(r => 5 < r.Rank, ["Rank"], false, out after).Should().BeTrue("the value may be on the left");
        after.Should().Equal(5);

        var last = "item042";
        Read(r => string.Compare(r.Id, last) > 0, ["Id"], false, out after).Should().BeTrue("strings compare through string.Compare");
        after.Should().Equal("item042");

        Read(r => r.Id.CompareTo(last) > 0, ["Id"], false, out after).Should().BeTrue("or through CompareTo");
        after.Should().Equal("item042");

        Read(r => 0 > r.Id.CompareTo(last), ["Id"], true, out after).Should().BeTrue("zero on the left mirrors the operator");
        after.Should().Equal("item042");

        Read(r => r.Rank < 5, ["Rank"], true, out after).Should().BeTrue("descending seeks before the position");
        after.Should().Equal(5);
    }

    [TestMethod]
    public void MoreColumns_TheTiebreakingForm_AndTheNestedForm()
    {
        var at = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var key = Guid.NewGuid();

        Read(r => r.Rank > 5 || (r.Rank == 5 && string.Compare(r.Id, "b") > 0), ["Rank", "Id"], false, out var after).Should().BeTrue();
        after.Should().Equal(5, "b");

        Read(r => r.Rank >= 5 && (r.Rank > 5 || r.Id.CompareTo("b") > 0), ["Rank", "Id"], false, out after).Should().BeTrue("the form SqlOS itself seeks with");
        after.Should().Equal(5, "b");

        Read(r => r.Rank > 1 || (r.Rank == 1 && (r.At > at || (r.At == at && r.Key.CompareTo(key) > 0))), ["Rank", "At", "Key"], false, out after).Should().BeTrue();
        after.Should().Equal(1, at, key);

        Read(r => r.Rank < 5 || (r.Rank == 5 && string.Compare(r.Id, "b") < 0), ["Rank", "Id"], true, out after).Should().BeTrue();
        after.Should().Equal(5, "b");
    }

    [TestMethod]
    public void WhatIsNotAKeyset_StaysAPredicate()
    {
        Read(r => r.Rank > 5, ["Rank", "Id"], false, out _).Should().BeFalse("the order has two columns and the predicate one");
        Read(r => r.Rank >= 5, ["Rank"], false, out _).Should().BeFalse("a page starts strictly after its position");
        Read(r => r.Rank > r.Rank, ["Rank"], false, out _).Should().BeFalse("the value must not depend on the row");
        Read(r => r.Rank == 5, ["Rank"], false, out _).Should().BeFalse();
        Read(r => r.Rank > 5 || (r.Rank == 6 && string.Compare(r.Id, "b") > 0), ["Rank", "Id"], false, out _).Should().BeFalse("the tiebreak must repeat the value");
        Read(r => r.Rank > 5 || (r.Id == "a" && string.Compare(r.Id, "b") > 0), ["Rank", "Id"], false, out _).Should().BeFalse("the tiebreak must be on the same column");
        Read(r => r.Rank > 5 || (r.Rank == 5 && string.Compare(r.Id, "b") < 0), ["Rank", "Id"], false, out _).Should().BeFalse("the directions must agree with the order");
        Read(r => r.Rank < 5 || (r.Rank == 5 && string.Compare(r.Id, "b") < 0), ["Rank", "Id"], false, out _).Should().BeFalse("a descending keyset on an ascending page");
        Read(r => r.Id.StartsWith("a"), ["Id"], false, out _).Should().BeFalse();

        string? missing = null;
        Read(r => string.Compare(r.Id, missing) > 0, ["Id"], false, out _).Should().BeFalse("a null position is no position");
    }
}
