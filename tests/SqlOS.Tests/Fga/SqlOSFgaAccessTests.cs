using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The filter <c>BuildFilterAsync</c> returns: a marker naming who the rows are for, read by the context's
/// query execution, and replaced by the predicate over the caller's access roots only when a planned
/// statement needs it.
/// </summary>
[TestClass]
public class SqlOSFgaAccessTests
{
    private static readonly SqlOSFgaAccessToken Token = new(["subj_a", "grp_b"], "[\"subj_a\",\"grp_b\"]", "perm_read", "READ", TypeSeq: null, new SqlOSFgaOptions { MaxResourceHierarchyDepth = 3 });

    private sealed class Row : IHasResourceId
    {
        public string ResourceId { get; set; } = string.Empty;

        public byte[]? FgaScope { get; private set; }

        public int Rank { get; set; }
    }

    [TestMethod]
    public void TheFilter_IsTheMarkerAndNothingElse()
    {
        var filter = SqlOSFgaAccess.Filter<Row>(Token);

        SqlOSFgaAccess.TokenOf(filter).Should().BeSameAs(Token, "the filter composed unchanged is recognized as the caller's");
        SqlOSFgaAccess.Tokens(filter).Should().Equal(Token);

        Expression<Func<Row, bool>> composed = r => r.Rank > 1 && SqlOSFgaAccess.Visible(r, Token);
        SqlOSFgaAccess.TokenOf(composed).Should().BeNull("a filter combined with another condition is not the marker alone");
        SqlOSFgaAccess.Tokens(composed).Should().Equal([Token], "but the query still carries the caller");

        var inMemory = () => filter.Compile()(new Row());
        inMemory.Should().Throw<InvalidOperationException>().WithMessage("*UseSqlOSFga*", "the database answers the marker; nothing else does");
    }

    [TestMethod]
    public void Resolve_ReplacesEveryMarkerWithThePredicateOverTheRoots_ReadOncePerCaller()
    {
        IQueryable<Row> rows = new List<Row>().AsQueryable();
        var query = rows.Where(SqlOSFgaAccess.Filter<Row>(Token)).Where(x => SqlOSFgaAccess.Visible(x, Token) && x.Rank > 1).Expression;

        var asked = 0;
        var resolved = SqlOSFgaAccess.Resolve(
            query,
            token =>
            {
                asked++;
                token.Should().BeSameAs(Token);
                return [new SqlOSFgaAccessRoot { ResourceSeq = 7, Depth = 1 }, new SqlOSFgaAccessRoot { ResourceSeq = 9, Depth = 2 }];
            });

        asked.Should().Be(1, "the roots are read once for the caller, however many times the filter appears");
        var text = resolved.ToString();
        text.Should().NotContain("Visible");
        text.Should().Contain("Property(entity, \"FgaScope\")", "the predicate reads the row's scope through the filter's own parameter");
        text.Should().Contain("Property(x, \"FgaScope\")", "and through the parameter of a lambda the marker was composed into");
        text.Should().Contain("x.Rank > 1", "the rest of the composed condition stays");

        var none = SqlOSFgaAccess.Resolve(query, _ => []).ToString();
        none.Should().NotContain("Visible").And.Contain("False", "a caller with no root sees nothing");
    }
}
