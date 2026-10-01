using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class ResourceIndicatorTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  \t ")]
    public void A_blank_request_value_names_no_resource(string? value)
    {
        ResourceIndicator.FromRequest(value).Should().BeNull();
    }

    [TestMethod]
    public void A_request_value_is_trimmed()
    {
        ResourceIndicator.FromRequest("  https://app.example/mcp  ")!.Value.Should().Be("https://app.example/mcp");
        ResourceIndicator.FromRequest("https://app.example/mcp")!.ToString().Should().Be("https://app.example/mcp");
    }

    [TestMethod]
    public void A_resource_matches_an_audience_by_ordinal_equality()
    {
        var resource = ResourceIndicator.FromRequest(" https://app.example/api ")!;

        resource.Matches("https://app.example/api").Should().BeTrue();
        resource.Matches("https://app.example/api/").Should().BeFalse();
        resource.Matches("https://APP.example/api").Should().BeFalse();
        resource.Matches(" https://app.example/api").Should().BeFalse("stored audiences are compared as stored");
        resource.Matches(null).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("todos-api")]
    [DataRow("https://app.example/api#fragment")]
    [DataRow("urn:example:api")]
    public void A_value_that_is_not_an_rfc_8707_absolute_uri_is_kept_as_in_7_2_1(string value)
    {
        // RFC 8707 §2 requires an absolute URI without a fragment; 7.2.1 binds any trimmed value
        // and the behavior lock records that. Enforcing the form is a ledgered change (#429).
        ResourceIndicator.FromRequest(value)!.Value.Should().Be(value);
    }

    [TestMethod]
    public void Resource_indicators_are_equal_by_value()
    {
        ResourceIndicator.FromRequest("https://a/api").Should().Be(ResourceIndicator.FromRequest(" https://a/api "));
        ResourceIndicator.FromRequest("https://a/api").Should().NotBe(ResourceIndicator.FromRequest("https://a/mcp"));
    }
}
