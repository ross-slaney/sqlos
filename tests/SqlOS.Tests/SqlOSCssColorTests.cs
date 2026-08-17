using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCssColorTests
{
    [TestMethod]
    [DataRow("#abc", "#aabbcc")]
    [DataRow("#4F46E5", "#4f46e5")]
    [DataRow("#11223344", "#11223344")]
    [DataRow(" rgb(1, 2, 3) ", "rgb(1,2,3)")]
    [DataRow("rgba(255,255,255,0.5)", "rgba(255,255,255,0.5)")]
    [DataRow("hsla(120, 50%, 40%, 1.0)", "hsla(120,50%,40%,1)")]
    [DataRow("HSL(360,100%,50%)", "hsl(360,100%,50%)")]
    [DataRow("Transparent", "transparent")]
    public void TryNormalize_AcceptsSupportedForms(string value, string expected)
    {
        SqlOSCssColor.TryNormalize(value, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
        normalized!.Length.Should().BeLessThanOrEqualTo(SqlOSCssColor.MaxLength);
    }

    [TestMethod]
    [DataRow("</style><script>alert(1)</script>")]
    [DataRow("red;}</style><script>alert(1)</script>")]
    [DataRow("url(https://evil.example)")]
    [DataRow("expression(alert(1))")]
    [DataRow("javascript:alert(1)")]
    [DataRow("var(--primary)")]
    [DataRow("color-mix(in srgb, red, blue)")]
    [DataRow("#gggggg")]
    [DataRow("rgb(256,0,0)")]
    [DataRow("rgb(1,2,3,0.5)")]
    [DataRow("rgba(1,2,3)")]
    [DataRow("hsl(10,50,40)")]
    [DataRow("red")]
    [DataRow("#2563eb\n</style>")]
    [DataRow("&lt;script&gt;")]
    [DataRow("%3Cstyle%3E")]
    [DataRow("__SQLOS_CSP_NONCE__")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow("")]
    [DataRow("   ")]
    public void TryNormalize_RejectsAdversarialAndUnsupportedValues(string value)
    {
        SqlOSCssColor.TryNormalize(value, out var normalized).Should().BeFalse();
        normalized.Should().BeNull();
    }

    [TestMethod]
    public void TryNormalize_RejectsControlCharactersAndMaxLengthOverflow()
    {
        SqlOSCssColor.TryNormalize("#2563eb\0", out _).Should().BeFalse();
        SqlOSCssColor.TryNormalize(new string('a', SqlOSCssColor.MaxLength + 1), out _).Should().BeFalse();
        SqlOSCssColor.TryNormalize("rgba(255, 255, 255, 1)", out var compact).Should().BeTrue();
        compact.Should().Be("rgba(255,255,255,1)");
    }

    [TestMethod]
    [DataRow("PrimaryColor", "</style><script>")]
    [DataRow("AccentColor", "url(https://evil.example)")]
    [DataRow("BackgroundColor", "red;}")]
    public void Require_RejectsEachColorField(string name, string value)
    {
        var act = () => SqlOSCssColor.Require(value, name);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{name}*supported CSS color*");
    }

    [TestMethod]
    public void Render_UsesFallbackForLegacyInvalidValues()
    {
        SqlOSCssColor.Render("</style><script>alert(1)</script>", "#4f46e5").Should().Be("#4f46e5");
        SqlOSCssColor.Render("#0D9488", "#4f46e5").Should().Be("#0d9488");
    }
}
