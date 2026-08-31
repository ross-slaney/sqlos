using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSRedirectUriPolicyTests
{
    [DataTestMethod]
    [DataRow("https://client.example.test/callback", "https://client.example.test/callback")]
    [DataRow("http://127.0.0.1:49152/callback", "http://127.0.0.1:49152/callback")]
    [DataRow("http://localhost:5000/callback", "http://localhost:5000/callback")]
    public void IsRegisteredMatch_AcceptsExactMatches(string registered, string requested)
    {
        SqlOSRedirectUriPolicy.IsRegisteredMatch([registered], requested, allowLoopbackRedirectUris: true)
            .Should().BeTrue();
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1/callback/abc123", "http://127.0.0.1:49152/callback/abc123")]
    [DataRow("http://127.0.0.1/callback/abc123", "http://127.0.0.1:65535/callback/abc123")]
    [DataRow("http://127.0.0.1:8080/callback", "http://127.0.0.1:49152/callback")]
    [DataRow("http://[::1]/callback", "http://[::1]:49152/callback")]
    [DataRow("http://[0:0:0:0:0:0:0:1]/callback", "http://[::1]:49152/callback")]
    [DataRow("http://127.0.0.1/callback?state=x", "http://127.0.0.1:49152/callback?state=x")]
    public void IsRegisteredMatch_IgnoresPortsForLoopbackIpLiterals(string registered, string requested)
    {
        SqlOSRedirectUriPolicy.IsRegisteredMatch([registered], requested, allowLoopbackRedirectUris: true)
            .Should().BeTrue();
    }

    [DataTestMethod]
    [DataRow("http://127.0.0.1/callback/abc123", "http://127.0.0.1:49152/callback/other")]
    [DataRow("http://127.0.0.1/callback", "http://127.0.0.1:49152/callback?extra=1")]
    [DataRow("http://127.0.0.1/Callback", "http://127.0.0.1:49152/callback")]
    [DataRow("http://127.0.0.1/callback", "http://[::1]:49152/callback")]
    [DataRow("http://[::1]/callback", "http://127.0.0.1:49152/callback")]
    [DataRow("http://localhost/callback", "http://localhost:49152/callback")]
    [DataRow("http://localhost:5000/callback", "http://localhost:49152/callback")]
    [DataRow("https://client.example.test/callback", "https://client.example.test:8443/callback")]
    [DataRow("http://client.example.test/callback", "http://client.example.test:8080/callback")]
    [DataRow("http://127.0.0.1/callback", "https://127.0.0.1:49152/callback")]
    public void IsRegisteredMatch_RejectsNonMatchingRedirects(string registered, string requested)
    {
        SqlOSRedirectUriPolicy.IsRegisteredMatch([registered], requested, allowLoopbackRedirectUris: true)
            .Should().BeFalse();
    }

    [TestMethod]
    public void IsRegisteredMatch_RequiresExactMatchWhenLoopbackDisabled()
    {
        SqlOSRedirectUriPolicy.IsRegisteredMatch(
                ["http://127.0.0.1/callback"],
                "http://127.0.0.1:49152/callback",
                allowLoopbackRedirectUris: false)
            .Should().BeFalse();

        SqlOSRedirectUriPolicy.IsRegisteredMatch(
                ["http://127.0.0.1:49152/callback"],
                "http://127.0.0.1:49152/callback",
                allowLoopbackRedirectUris: false)
            .Should().BeTrue();
    }
}
