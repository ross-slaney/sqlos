using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Pagination;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCursorPaginationTests
{
    [TestMethod]
    public void Codec_RoundTripsStringTimestampAndCompositeKeys()
    {
        var keys = new[] { "Acme", "2026-01-02T03:04:05.0000000", "org_1" };
        var encoded = SqlOSCursorCodec.Encode("auth.organizations", "fp-a", keys);
        encoded.Should().NotContain("Acme");
        encoded.Should().NotContain("org_1");
        SqlOSCursorCodec.Decode(encoded, "auth.organizations", "fp-a")
            .Should()
            .Equal(keys);
    }

    [TestMethod]
    public void Codec_RejectsMalformedTamperedWrongVersionWrongFilterAndOversizedCursors()
    {
        var valid = SqlOSCursorCodec.Encode("auth.users", "fp", ["Ada", "usr_1"]);

        var malformed = () => SqlOSCursorCodec.Decode("not-a-cursor!", "auth.users", "fp");
        malformed.Should().Throw<SqlOSCursorException>().Which.Error.Should().Be("invalid_cursor");
        malformed.Should().Throw<SqlOSCursorException>().Which.Message.Should().NotContain("usr_1");

        var wrongSort = () => SqlOSCursorCodec.Decode(valid, "auth.organizations", "fp");
        wrongSort.Should().Throw<SqlOSCursorException>().WithMessage("*does not match this list*");

        var wrongFilter = () => SqlOSCursorCodec.Decode(valid, "auth.users", "other");
        wrongFilter.Should().Throw<SqlOSCursorException>().WithMessage("*does not match the current filters*");

        var oversized = new string('A', SqlOSCursorCodec.MaxEncodedLength + 1);
        var tooBig = () => SqlOSCursorCodec.Decode(oversized, "auth.users", "fp");
        tooBig.Should().Throw<SqlOSCursorException>();
    }

    [TestMethod]
    public void PageSize_EnforcesDefaultMinimumAndMaximum()
    {
        SqlOSCursorPagination.NormalizePageSize(null).Should().Be(25);
        SqlOSCursorPagination.NormalizePageSize(null, 10).Should().Be(10);
        SqlOSCursorPagination.NormalizePageSize(0).Should().Be(1);
        SqlOSCursorPagination.NormalizePageSize(1000).Should().Be(100);
        SqlOSCursorPagination.NormalizePageSize(40).Should().Be(40);
    }

    [TestMethod]
    public void RejectLegacyOffset_AllowsFirstWindowOnly()
    {
        SqlOSCursorPagination.RejectLegacyOffset(null);
        SqlOSCursorPagination.RejectLegacyOffset(1);
        var deep = () => SqlOSCursorPagination.RejectLegacyOffset(2);
        deep.Should().Throw<SqlOSCursorException>().WithMessage("*Offset pagination*");
    }

    [TestMethod]
    public void Keyset_DuplicatePrimarySortUsesIdTiebreaker()
    {
        var rows = new[]
        {
            new NamedRow("Ada", "a2"),
            new NamedRow("Ada", "a1"),
            new NamedRow("Bea", "b1")
        };
        var keyset = SqlOSKeyset<NamedRow>.Create()
            .Ascending(x => x.Name)
            .ThenAscending(x => x.Id);

        var ordered = keyset.ApplySort(rows.AsQueryable()).ToList();
        ordered.Select(x => x.Id).Should().Equal("a1", "a2", "b1");

        var afterAdaA1 = keyset.After(keyset.Encode(ordered[0])).Compile();
        ordered.Where(afterAdaA1).Select(x => x.Id).Should().Equal("a2", "b1");
    }

    [TestMethod]
    public void Keyset_DescendingTimestampUsesIdTiebreaker()
    {
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(1);
        var rows = new[]
        {
            new TimedRow(t2, "n1"),
            new TimedRow(t2, "n2"),
            new TimedRow(t1, "o1")
        };
        var keyset = SqlOSKeyset<TimedRow>.Create()
            .Descending(x => x.CreatedAt)
            .ThenDescending(x => x.Id);

        var ordered = keyset.ApplySort(rows.AsQueryable()).ToList();
        ordered.Select(x => x.Id).Should().Equal("n2", "n1", "o1");

        var afterFirst = keyset.After(keyset.Encode(ordered[0])).Compile();
        ordered.Where(afterFirst).Select(x => x.Id).Should().Equal("n1", "o1");
    }

    [TestMethod]
    public void Fingerprint_ChangesWhenFiltersChange()
    {
        SqlOSCursorCodec.Fingerprint("active", "chatgpt")
            .Should()
            .NotBe(SqlOSCursorCodec.Fingerprint("disabled", "chatgpt"));
    }

    private sealed class NamedRow
    {
        public NamedRow(string name, string id)
        {
            Name = name;
            Id = id;
        }

        public string Name { get; }
        public string Id { get; }
    }

    private sealed class TimedRow
    {
        public TimedRow(DateTime createdAt, string id)
        {
            CreatedAt = createdAt;
            Id = id;
        }

        public DateTime CreatedAt { get; }
        public string Id { get; }
    }
}
