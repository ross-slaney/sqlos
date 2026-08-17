using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthPageColorSecurityTests
{
    [TestMethod]
    [DataRow("PrimaryColor", "</style><script>alert(1)</script>")]
    [DataRow("AccentColor", "url(https://evil.example/#ffff)")]
    [DataRow("BackgroundColor", "red;}</style><script src=//evil></script>")]
    public async Task HostedLogin_LegacyInvalidStoredColors_RenderDefaultsWithoutNonceingInjectedTags(
        string field,
        string payload)
    {
        await using var harness = await ControlPlaneParityHarness.CreateAsync();
        await harness.Settings.EnsureDefaultAuthPageSettingsAsync();
        var settings = await harness.Context.Set<SqlOSAuthPageSettings>().SingleAsync(x => x.Id == "default");
        if (field == "PrimaryColor")
        {
            settings.PrimaryColor = payload;
        }
        else if (field == "AccentColor")
        {
            settings.AccentColor = payload;
        }
        else
        {
            settings.BackgroundColor = payload;
        }

        await harness.Context.SaveChangesAsync();

        using var response = await harness.Client.GetAsync("/sqlos/auth/login");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        html.Should().NotContain(payload);
        html.Should().NotContain("alert(1)");
        html.Should().NotContain("evil.example");
        html.Should().Contain(field switch
        {
            "PrimaryColor" => "--primary: #4f46e5",
            "AccentColor" => "--accent: #111827",
            _ => "--page-bg: #f8fafc"
        });
        html.Should().MatchRegex("<style nonce=\"[A-Za-z0-9_-]+\">");
        html.Should().MatchRegex("<script nonce=\"[A-Za-z0-9_-]+\">");
        html.Should().NotContain("<script>alert");
        System.Text.RegularExpressions.Regex.Matches(html, "<script").Count.Should().Be(1);
        System.Text.RegularExpressions.Regex.Matches(html, "<style").Count.Should().Be(1);
    }
}
