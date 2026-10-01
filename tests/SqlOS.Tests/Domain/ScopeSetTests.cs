using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class ScopeSetTests
{
    [DataTestMethod]
    [DataRow(null, new string[0])]
    [DataRow("", new string[0])]
    [DataRow("   ", new string[0])]
    [DataRow("openid", new[] { "openid" })]
    [DataRow("openid profile email", new[] { "openid", "profile", "email" })]
    [DataRow("  profile   openid profile ", new[] { "profile", "openid" })]
    [DataRow("Read read READ", new[] { "Read", "read", "READ" })]
    [DataRow("openid\tprofile", new[] { "openid\tprofile" })]
    [DataRow(" \topenid\t ", new[] { "openid" })]
    public void Parsing_splits_on_spaces_trims_and_keeps_the_first_occurrence_in_order(string? scope, string[] expected)
    {
        var set = ScopeSet.Parse(scope);

        set.Should().Equal(expected);
        set.Count.Should().Be(expected.Length);
        SqlOSScopePolicy.Split(scope).Should().Equal(expected, "SqlOSScopePolicy delegates to the set");
    }

    [TestMethod]
    public void The_wire_form_joins_the_set_in_order_with_single_spaces()
    {
        ScopeSet.Parse("  profile   openid profile ").ToString().Should().Be("profile openid");
        ScopeSet.Empty.ToString().Should().BeEmpty();
        default(ScopeSet).ToString().Should().BeEmpty();
    }

    [TestMethod]
    public void Of_normalizes_a_sequence_like_its_space_delimited_string()
    {
        ScopeSet.Of(["openid", " profile ", "openid", null, "", "a b"]).Should().Equal("openid", "profile", "a", "b");
        ScopeSet.Of([]).Should().BeEmpty();
    }

    [TestMethod]
    public void The_grant_keeps_the_requested_order_and_drops_scopes_outside_the_allow_list()
    {
        var requested = ScopeSet.Parse("email openid todos.read profile");

        requested.IntersectWith(["openid", "profile", "email"]).Should().Equal("email", "openid", "profile");
        requested.IntersectWith([]).Should().BeEmpty("an empty allow-list grants nothing");
        requested.IntersectWith(["OPENID"]).Should().BeEmpty("scopes compare ordinally");
        SqlOSScopePolicy.Grant("email openid todos.read profile", "[\"openid\",\"profile\",\"email\"]").Should().Equal("email", "openid", "profile");
    }

    [TestMethod]
    public void A_union_appends_new_scopes_after_the_existing_ones()
    {
        var existing = ScopeSet.Parse("openid profile");

        existing.UnionWith(ScopeSet.Parse("email openid offline_access")).Should().Equal("openid", "profile", "email", "offline_access");
        existing.UnionWith(ScopeSet.Empty).Should().Equal("openid", "profile");
        ScopeSet.Empty.UnionWith(existing).Should().Equal("openid", "profile");
    }

    [TestMethod]
    public void Membership_and_subsets_compare_ordinally()
    {
        var granted = ScopeSet.Parse("openid profile email");

        granted.Contains("openid").Should().BeTrue();
        granted.Contains("OpenID").Should().BeFalse();
        ScopeSet.Parse("profile openid").IsSubsetOf(granted).Should().BeTrue();
        ScopeSet.Parse("openid offline_access").IsSubsetOf(granted).Should().BeFalse();
        ScopeSet.Empty.IsSubsetOf(granted).Should().BeTrue();
    }

    [TestMethod]
    public void Equality_is_set_equality()
    {
        // FluentAssertions treats a ScopeSet as a collection, so equality is asserted explicitly.
        var left = ScopeSet.Parse("openid profile email");
        var reordered = ScopeSet.Parse("email openid profile");

        left.Equals(reordered).Should().BeTrue();
        (left == reordered).Should().BeTrue();
        left.GetHashCode().Should().Be(reordered.GetHashCode());
        left.ToString().Should().NotBe(reordered.ToString(), "order is kept for the wire form");
        (left != ScopeSet.Parse("openid profile")).Should().BeTrue();
        left.Equals(ScopeSet.Parse("openid profile email offline_access")).Should().BeFalse();
        left.Equals(ScopeSet.Parse("openid profile e-mail")).Should().BeFalse();
        ScopeSet.Empty.Equals(default).Should().BeTrue();
        ScopeSet.Empty.Equals(ScopeSet.Parse("  ")).Should().BeTrue();
        left.Equals((object)reordered).Should().BeTrue();
        left.Equals((object)"openid profile email").Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("openid", true)]
    [DataRow("api:read/write", true)]
    [DataRow("https://api.example/todos.read", true)]
    [DataRow("!#$%&'()*+,-./0-9:;<=>?@[]^_`{|}~", true)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("a b", false)]
    [DataRow("a\"b", false)]
    [DataRow("a\\b", false)]
    [DataRow("a\tb", false)]
    [DataRow("a\u007Fb", false)]
    [DataRow("é", false)]
    public void Scope_tokens_follow_rfc_6749_syntax(string? token, bool expected)
    {
        ScopeSet.IsValidToken(token).Should().Be(expected);
    }
}
