using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSEmailAddressTests
{
    [DataTestMethod]
    [DataRow("Bob@Example.COM", "BOB@EXAMPLE.COM")]
    [DataRow("  bob@example.com  ", "BOB@EXAMPLE.COM")]
    [DataRow("first.last+tag@sub.example.org", "FIRST.LAST+TAG@SUB.EXAMPLE.ORG")]
    public void AsciiAddresses_FoldAsciiCaseOnly_AndKeepTheHistoricalKey(string input, string expected)
    {
        SqlOSEmailAddress.TryNormalize(input, out var key).Should().BeTrue();
        key.Should().Be(expected);
        key.Should().Be(input.Trim().ToUpperInvariant(), "ASCII addresses keep the key SqlOS has always stored");
        SqlOSAdminService.NormalizeEmail(input).Should().Be(expected);
    }

    [TestMethod]
    public void InternationalizedDomains_UseTheIdnaAsciiForm_ForEverySpelling()
    {
        SqlOSEmailAddress.TryNormalize("ü@münchen.de", out var unicode).Should().BeTrue();
        SqlOSEmailAddress.TryNormalize("ü@MÜNCHEN.DE", out var upperUnicode).Should().BeTrue();
        SqlOSEmailAddress.TryNormalize("ü@xn--mnchen-3ya.de", out var punycode).Should().BeTrue();

        unicode.Should().Be("ü@XN--MNCHEN-3YA.DE");
        upperUnicode.Should().Be(unicode);
        punycode.Should().Be(unicode);
    }

    [TestMethod]
    public void UnicodeNormalization_MakesComposedAndDecomposedSpellingsOneKey()
    {
        SqlOSEmailAddress.TryCanonicalize("üser@example.com", out var address, out var decomposed).Should().BeTrue();
        SqlOSEmailAddress.TryNormalize("üser@example.com", out var composed).Should().BeTrue();

        decomposed.Should().Be(composed);
        address.Should().Be("üser@example.com");
    }

    [DataTestMethod]
    [DataRow("bob@busineß.com", "bob@business.com")]
    [DataRow("bob@giþub.com", "bob@github.com")]
    [DataRow("bob@mæil.com", "bob@maeil.com")]
    [DataRow("bob@œil.com", "bob@oeil.com")]
    [DataRow("straße@example.com", "strasse@example.com")]
    [DataRow("ü@example.com", "Ü@example.com")]
    public void CollationLookAlikes_AreDifferentKeys(string lookAlike, string original)
    {
        SqlOSEmailAddress.TryNormalize(lookAlike, out var lookAlikeKey).Should().BeTrue();
        SqlOSEmailAddress.TryNormalize(original, out var originalKey).Should().BeTrue();

        lookAlikeKey.Should().NotBe(originalKey);
        SqlOSEmailAddress.IsSameMailbox(lookAlike, original).Should().BeFalse();
    }

    [TestMethod]
    public void NonAsciiLocalParts_AreKeptExactlyAsWritten()
    {
        SqlOSEmailAddress.TryNormalize("Jörg@example.com", out var key).Should().BeTrue();

        key.Should().Be("JöRG@EXAMPLE.COM");
    }

    [DataTestMethod]
    [DataRow("boſ@example.com", DisplayName = "long s in the local part")]
    [DataRow("Ken@example.com", DisplayName = "Kelvin sign in the local part")]
    [DataRow("bob@buſiness.com", DisplayName = "long s in the domain")]
    [DataRow("bob@Kelvin.com", DisplayName = "Kelvin sign in the domain")]
    [DataRow("ｂｏｂ@example.com", DisplayName = "fullwidth local part")]
    [DataRow("bob＠example.com", DisplayName = "fullwidth at sign")]
    [DataRow("bob@ｅxample.com", DisplayName = "fullwidth domain letter")]
    [DataRow("ﬁsh@example.com", DisplayName = "ligature")]
    public void AsciiLookAlikeCharacters_AreRejectedInsteadOfFolded(string input)
    {
        SqlOSEmailAddress.TryNormalize(input, out _).Should().BeFalse();
        SqlOSAdminService.NormalizeEmail(input).Should().NotBe(
            SqlOSAdminService.NormalizeEmail("bos@example.com"),
            "an invalid address never shares a key with a valid one");
    }

    [DataTestMethod]
    [DataRow("bob​@example.com", DisplayName = "zero-width space")]
    [DataRow("bob@exa­mple.com", DisplayName = "soft hyphen")]
    [DataRow("bob@exa​mple.com", DisplayName = "zero-width space in domain")]
    [DataRow("bob‮@example.com", DisplayName = "bidi override")]
    [DataRow("bob\u0000@example.com", DisplayName = "NUL")]
    [DataRow("bo b@example.com", DisplayName = "inner space")]
    [DataRow("bob@exam\tple.com", DisplayName = "tab")]
    [DataRow("bob@example.com", DisplayName = "private use")]
    public void ControlFormatAndSeparatorCharacters_AreRejected(string input)
    {
        SqlOSEmailAddress.TryNormalize(input, out _).Should().BeFalse();
    }

    [TestMethod]
    public void UnpairedSurrogates_AreRejected()
    {
        // Built at runtime: attribute metadata cannot carry a lone surrogate.
        SqlOSEmailAddress.TryNormalize("bob" + (char)0xD800 + "@example.com", out _).Should().BeFalse();
        SqlOSEmailAddress.TryNormalize("bob@example.com" + (char)0xDC00, out _).Should().BeFalse();
        SqlOSEmailAddress.TryNormalizeDomain("exa" + (char)0xD800 + "mple.com", out _).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("bob")]
    [DataRow("@example.com")]
    [DataRow("bob@")]
    [DataRow("bob@@example.com")]
    [DataRow("\"a@b\"@example.com")]
    [DataRow("bob@exa_mple.über")]
    public void AddressesThatDoNotParse_AreRejected(string input)
    {
        SqlOSEmailAddress.TryNormalize(input, out _).Should().BeFalse();
    }

    [TestMethod]
    public void OverlongAddresses_AreRejected()
    {
        var local = new string('a', 64);
        var domain = string.Join('.', Enumerable.Repeat(new string('b', 60), 5)) + ".com";
        SqlOSEmailAddress.TryNormalize($"{local}@{domain}", out _).Should().BeFalse();
        SqlOSEmailAddress.TryNormalize(null, out _).Should().BeFalse();
    }

    [TestMethod]
    public void StoredRows_MatchOnlyWhenTheirAddressIsTheSameMailbox()
    {
        SqlOSEmailAddress.TryNormalize("ü@münchen.de", out var key).Should().BeTrue();
        var legacyKeyedRow = new SqlOSUserEmail { Email = "ü@münchen.de", NormalizedEmail = "Ü@MÜNCHEN.DE" };
        var victimRow = new SqlOSUserEmail { Email = "bob@business.com", NormalizedEmail = "BOB@BUSINESS.COM" };
        SqlOSEmailAddress.TryNormalize("bob@busineß.com", out var lookAlikeKey).Should().BeTrue();

        SqlOSEmailAddress.MatchesStoredEmail(legacyKeyedRow, key).Should().BeTrue();
        SqlOSEmailAddress.MatchesStoredEmail(victimRow, lookAlikeKey).Should().BeFalse();
        SqlOSEmailAddress.MatchesStoredEmail(new SqlOSUserEmail { Email = "bob", NormalizedEmail = "BOB" }, "BOB").Should().BeFalse();
    }

    [TestMethod]
    public void LookupKeys_IncludeTheHistoricalKeysForNonAsciiInputOnly()
    {
        SqlOSEmailAddress.TryNormalize("bob@example.com", out var asciiKey).Should().BeTrue();
        SqlOSEmailAddress.LookupKeys(asciiKey, "bob@example.com").Should().Equal(asciiKey);

        SqlOSEmailAddress.TryNormalize("ü@münchen.de", out var idnKey).Should().BeTrue();
        SqlOSEmailAddress.LookupKeys(idnKey, "ü@münchen.de").Should().Equal(
            idnKey,
            "Ü@MÜNCHEN.DE",
            "Ü@MÜNCHEN.DE");
    }

    [TestMethod]
    public void Domains_CompareAsCanonicalAsciiStrings()
    {
        SqlOSEmailAddress.TryNormalizeDomain("MÜNCHEN.de", out var idn).Should().BeTrue();
        idn.Should().Be("xn--mnchen-3ya.de");
        SqlOSEmailAddress.TryNormalizeDomain("Example.COM", out var ascii).Should().BeTrue();
        ascii.Should().Be("example.com");
        SqlOSEmailAddress.TryNormalizeDomain("exa​mple.com", out _).Should().BeFalse();
        SqlOSEmailAddress.TryNormalizeDomain(null, out _).Should().BeFalse();
        SqlOSEmailAddress.GetDomain("BOB@XN--MNCHEN-3YA.DE").Should().Be("xn--mnchen-3ya.de");

        SqlOSOrganizationEmailDomains.SameDomain("münchen.de", "xn--mnchen-3ya.de").Should().BeTrue();
        SqlOSOrganizationEmailDomains.SameDomain("business.com", "busineß.com").Should().BeFalse();
        SqlOSOrganizationEmailDomains.SameDomain(null, "business.com").Should().BeFalse();
    }

    [TestMethod]
    public void FallbackKeys_ForInvalidInput_NeverCollideWithValidKeys()
    {
        SqlOSAdminService.NormalizeEmail("  not-an-email ").Should().Be("NOT-AN-EMAIL");
        SqlOSAdminService.NormalizeEmail("bob@@example.com").Should().Be("BOB@@EXAMPLE.COM");
        SqlOSAdminService.NormalizeEmail("boſ@example.com").Should().Be("BOſ@EXAMPLE.COM");
        SqlOSEmailAddress.IsSameMailbox("bob", "bob").Should().BeFalse();
    }
}
