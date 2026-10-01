using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

/// <summary>
/// The redirect URI rules on the value object. <c>SqlOSRedirectUriPolicyTests</c> keeps the 7.2.1
/// assertions on the policy that now delegates here.
/// </summary>
[TestClass]
public sealed class RedirectUriTests
{
    [DataTestMethod]
    [DataRow("https://client.example.test/callback")]
    [DataRow("HTTPS://Client.Example.Test/Callback?x=1")]
    [DataRow("http://127.0.0.1:49152/cb")]
    [DataRow("com.example.app:/oauth2redirect")]
    [DataRow("https://client.example.test/cb#fragment")]
    public void An_absolute_uri_is_kept_exactly_as_written(string value)
    {
        var redirectUri = RedirectUri.Create(value);

        redirectUri.Value.Should().Be(value);
        redirectUri.ToString().Should().Be(value);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("callback")]
    [DataRow("https://")]
    public void A_value_that_is_not_an_absolute_uri_is_rejected(string? value)
    {
        RedirectUri.TryCreate(value, out var redirectUri).Should().BeFalse();
        redirectUri.Should().BeNull();
        FluentActions.Invoking(() => RedirectUri.Create(value))
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.InvalidRedirectUri);
    }

    [DataTestMethod]
    [DataRow("https://client.example.test/cb#fragment", true)]
    [DataRow("https://client.example.test/cb#", true)]
    [DataRow("https://client.example.test/cb", false)]
    public void HasFragment_reports_any_fragment_marker_like_7x_client_seeding(string value, bool expected)
    {
        RedirectUri.Create(value).HasFragment.Should().Be(expected);
    }

    [DataTestMethod]
    [DataRow("https://client.example.test/cb", true, false, true)]
    [DataRow("https://client.example.test/cb", false, true, false)]
    [DataRow("http://127.0.0.1:8080/cb", false, true, true)]
    [DataRow("http://[::1]/cb", false, true, true)]
    [DataRow("http://localhost:5000/cb", false, true, true)]
    [DataRow("http://127.0.0.1/cb", true, false, false)]
    [DataRow("http://client.example.test/cb", true, true, false)]
    [DataRow("com.example.app:/cb", true, true, false)]
    [DataRow("HTTPS://client.example.test/cb", true, false, true)]
    public void Registration_allows_https_and_http_loopback_as_the_host_allows(
        string value,
        bool allowHttps,
        bool allowLoopback,
        bool expected)
    {
        RedirectUri.Create(value).IsAllowedForRegistration(allowHttps, allowLoopback).Should().Be(expected);
    }

    [TestMethod]
    public void A_requested_uri_matches_a_registration_exactly()
    {
        var registered = new[] { "https://client.example.test/callback" };

        RedirectUri.Create("https://client.example.test/callback").MatchesRegistered(registered, allowLoopbackRedirectUris: false).Should().BeTrue();
        RedirectUri.Create("https://client.example.test/Callback").MatchesRegistered(registered, allowLoopbackRedirectUris: true).Should().BeFalse();
        RedirectUri.Create("https://client.example.test:443/callback").MatchesRegistered(registered, allowLoopbackRedirectUris: true).Should().BeFalse();
        RedirectUri.Create("https://client.example.test/callback").MatchesRegistered([], allowLoopbackRedirectUris: true).Should().BeFalse();
    }

    [TestMethod]
    public void A_loopback_ip_literal_ignores_the_port_only_when_loopback_is_allowed()
    {
        var registered = new[] { "http://127.0.0.1/callback" };
        var requested = RedirectUri.Create("http://127.0.0.1:49152/callback");

        requested.MatchesRegistered(registered, allowLoopbackRedirectUris: true).Should().BeTrue();
        requested.MatchesRegistered(registered, allowLoopbackRedirectUris: false).Should().BeFalse();
        RedirectUri.Create("http://localhost:49152/callback").MatchesRegistered(["http://localhost/callback"], allowLoopbackRedirectUris: true)
            .Should().BeFalse("localhost can resolve elsewhere, so only IP literals are port-insensitive");
        RedirectUri.Create("http://127.0.0.1:49152/other").MatchesRegistered(registered, allowLoopbackRedirectUris: true).Should().BeFalse();
    }

    [TestMethod]
    public void Redirect_uris_are_equal_by_exact_value()
    {
        RedirectUri.Create("https://a.example/cb").Should().Be(RedirectUri.Create("https://a.example/cb"));
        RedirectUri.Create("https://a.example/cb").Should().NotBe(RedirectUri.Create("https://A.example/cb"));
        RedirectUri.Create("https://a.example/cb").Should().NotBe(RedirectUri.Create("https://a.example/cb/"));
    }
}
