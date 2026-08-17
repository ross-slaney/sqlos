using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Configuration;
using SqlOS.Security;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSBrowserSecurityHeadersTests
{
    [TestMethod]
    public void PrepareHtml_AssignsNonceOnlyToRendererOwnedPlaceholders()
    {
        var headers = new SqlOSBrowserSecurityHeaders(Options.Create(new SqlOSOptions()));
        var context = new DefaultHttpContext();
        var html =
            $$"""
            <style {{SqlOSCspNonce.Attribute}}>:root { --primary: #4f46e5; }</style>
            </style><script>alert(1)</script>
            <script>window.injected = true</script>
            <style>body { color: red; }</style>
            """;

        var prepared = headers.PrepareHtml(context, html);
        var policy = context.Response.Headers.ContentSecurityPolicy.ToString();
        var nonce = System.Text.RegularExpressions.Regex.Match(prepared, """<style nonce="([A-Za-z0-9_-]+)">""")
            .Groups[1].Value;

        nonce.Should().NotBeNullOrWhiteSpace();
        prepared.Should().Contain($"<style nonce=\"{nonce}\">");
        prepared.Should().Contain("<script>alert(1)</script>");
        prepared.Should().Contain("<script>window.injected = true</script>");
        prepared.Should().Contain("<style>body { color: red; }</style>");
        prepared.Should().NotContain("<script nonce=");
        prepared.Should().NotContain(SqlOSCspNonce.Token);
        prepared.Should().NotContain(SqlOSCspNonce.Attribute);
        policy.Should().Contain($"'nonce-{nonce}'");
        policy.Should().Contain("frame-ancestors 'none'");
    }
}
