using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

/// <summary>
/// The canonical email rules (#422) on the value object. <c>SqlOSEmailAddressTests</c> keeps the
/// 7.2.1 assertions on the string entry points that now delegate here.
/// </summary>
[TestClass]
public sealed class EmailAddressTests
{
    [DataTestMethod]
    [DataRow("Bob@Example.COM", "Bob@Example.COM", "BOB@EXAMPLE.COM", "example.com")]
    [DataRow("  bob@example.com\t", "bob@example.com", "BOB@EXAMPLE.COM", "example.com")]
    [DataRow("first.last+tag@sub.example.org", "first.last+tag@sub.example.org", "FIRST.LAST+TAG@SUB.EXAMPLE.ORG", "sub.example.org")]
    [DataRow("o'neil@example.com", "o'neil@example.com", "O'NEIL@EXAMPLE.COM", "example.com")]
    public void An_address_keeps_its_trimmed_form_and_folds_only_ascii_case_into_the_key(
        string input,
        string address,
        string canonical,
        string domain)
    {
        var email = EmailAddress.Parse(input);

        email.Address.Should().Be(address);
        email.Canonical.Should().Be(canonical);
        email.Domain.Should().Be(domain);
        email.ToString().Should().Be(address);
    }

    [TestMethod]
    public void Every_spelling_of_an_internationalized_domain_is_one_key()
    {
        var unicode = EmailAddress.Parse("ü@münchen.de");

        EmailAddress.Parse("ü@MÜNCHEN.DE").Should().Be(unicode);
        EmailAddress.Parse("ü@xn--mnchen-3ya.de").Should().Be(unicode);
        unicode.Canonical.Should().Be("ü@XN--MNCHEN-3YA.DE");
        unicode.Domain.Should().Be("xn--mnchen-3ya.de");
    }

    [TestMethod]
    public void Composed_and_decomposed_spellings_are_one_mailbox()
    {
        var decomposed = EmailAddress.Parse("u\u0308ser@example.com");

        decomposed.Should().Be(EmailAddress.Parse("üser@example.com"));
        decomposed.Address.Should().Be("üser@example.com", "the stored form is NFC");
    }

    [DataTestMethod]
    [DataRow("straße@example.com", "strasse@example.com", DisplayName = "ß in the local part")]
    [DataRow("bob@busineß.com", "bob@business.com", DisplayName = "ß in the domain")]
    [DataRow("bob@giþub.com", "bob@github.com", DisplayName = "þ")]
    [DataRow("bob@mæil.com", "bob@maeil.com", DisplayName = "æ")]
    [DataRow("bob@œil.com", "bob@oeil.com", DisplayName = "œ")]
    [DataRow("ü@example.com", "Ü@example.com", DisplayName = "non-ASCII case is not folded")]
    [DataRow("ı@example.com", "i@example.com", DisplayName = "dotless i")]
    [DataRow("İ@example.com", "i@example.com", DisplayName = "dotted capital I")]
    public void Collation_and_case_look_alikes_are_different_mailboxes(string lookAlike, string original)
    {
        var left = EmailAddress.Parse(lookAlike);
        var right = EmailAddress.Parse(original);

        left.Should().NotBe(right);
        (left == right).Should().BeFalse();
        left.MatchesStored(original).Should().BeFalse();
    }

    [TestMethod]
    public void Non_ascii_local_parts_are_kept_exactly_as_written()
    {
        EmailAddress.Parse("Jörg@example.com").Canonical.Should().Be("JöRG@EXAMPLE.COM");
        EmailAddress.Parse("😀@example.com").Canonical.Should().Be("😀@EXAMPLE.COM");
    }

    [DataTestMethod]
    [DataRow("bob\0@example.com", DisplayName = "NUL")]
    [DataRow("bob@exam\tple.com", DisplayName = "tab")]
    [DataRow("bob@example.com\r\nBcc: mallory@evil.example", DisplayName = "header injection")]
    [DataRow("bob\n@example.com", DisplayName = "line feed")]
    [DataRow("bob\u007F@example.com", DisplayName = "DEL")]
    [DataRow("bob\u0085@example.com", DisplayName = "next line (C1 control)")]
    [DataRow("bo b@example.com", DisplayName = "inner space")]
    [DataRow("bob\u00A0@example.com", DisplayName = "no-break space")]
    [DataRow("bob\u2028@example.com", DisplayName = "line separator")]
    public void Control_and_separator_characters_are_rejected(string input)
    {
        EmailAddress.TryParse(input, out _).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("bob\u202E@example.com", DisplayName = "right-to-left override")]
    [DataRow("bob@exa\u202Dmple.com", DisplayName = "left-to-right override in the domain")]
    [DataRow("bob\u200B@example.com", DisplayName = "zero-width space")]
    [DataRow("bo\u200Db@example.com", DisplayName = "zero-width joiner")]
    [DataRow("bob@exa\u00ADmple.com", DisplayName = "soft hyphen")]
    [DataRow("\uFEFFbob@example.com", DisplayName = "byte order mark")]
    [DataRow("bob\u2060@example.com", DisplayName = "word joiner")]
    [DataRow("bob\uE000@example.com", DisplayName = "private use")]
    [DataRow("bob\u0378@example.com", DisplayName = "unassigned")]
    public void Invisible_markup_and_unassigned_characters_are_rejected(string input)
    {
        EmailAddress.TryParse(input, out _).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("<b>bob</b>@example.com", "<B>BOB</B>@EXAMPLE.COM")]
    [DataRow("\"bob\"@example.com", "\"BOB\"@EXAMPLE.COM")]
    [DataRow("bob@<script>.com", "BOB@<SCRIPT>.COM")]
    public void Visible_ascii_markup_is_accepted_as_in_7_2_1(string input, string canonical)
    {
        // 7.2.1 accepts every visible ASCII character; renderers HTML-encode addresses. Rejecting
        // markup here would change behavior and needs a ledger entry.
        EmailAddress.Parse(input).Canonical.Should().Be(canonical);
    }

    [DataTestMethod]
    [DataRow("boſ@example.com", DisplayName = "long s")]
    [DataRow("\u212Aen@example.com", DisplayName = "Kelvin sign")]
    [DataRow("ｂｏｂ@example.com", DisplayName = "fullwidth local part")]
    [DataRow("bob＠example.com", DisplayName = "fullwidth at sign")]
    [DataRow("bob@ｅxample.com", DisplayName = "fullwidth domain letter")]
    [DataRow("ﬁsh@example.com", DisplayName = "ligature")]
    [DataRow("bob@example\u3002com", DisplayName = "ideographic full stop")]
    public void Characters_that_only_disguise_ascii_are_rejected(string input)
    {
        EmailAddress.TryParse(input, out _).Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("bob")]
    [DataRow("@example.com")]
    [DataRow("bob@")]
    [DataRow("bob@@example.com")]
    [DataRow("bob@example.com@example.com")]
    [DataRow("\"a@b\"@example.com")]
    [DataRow("bob@exa_mple.über")]
    public void Input_that_is_not_an_address_is_rejected(string? input)
    {
        EmailAddress.TryParse(input, out var email).Should().BeFalse();
        email.Should().BeNull();
    }

    [TestMethod]
    public void Unpaired_surrogates_are_rejected()
    {
        EmailAddress.TryParse("bob" + (char)0xD800 + "@example.com", out _).Should().BeFalse();
        EmailAddress.TryParse("bob@example.com" + (char)0xDC00, out _).Should().BeFalse();
        EmailAddress.TryCanonicalizeDomain("exa" + (char)0xD800 + "mple.com", out _).Should().BeFalse();
    }

    [TestMethod]
    public void The_address_and_its_key_are_limited_to_320_characters()
    {
        var domain = "example.com";
        var atLimit = new string('a', EmailAddress.MaxLength - domain.Length - 1) + "@" + domain;

        EmailAddress.Parse(atLimit).Canonical.Length.Should().Be(320);
        EmailAddress.TryParse("a" + atLimit, out _).Should().BeFalse();
        EmailAddress.TryParse("  " + atLimit + "  ", out _).Should().BeTrue("surrounding whitespace is trimmed before the length check");
    }

    [TestMethod]
    public void Parse_throws_the_7x_message_with_a_stable_code()
    {
        FluentActions.Invoking(() => EmailAddress.Parse("not-an-email"))
            .Should().Throw<SqlOSDomainException>()
            .Where(exception => exception.Error == SqlOSDomainError.InvalidEmailAddress)
            .WithMessage("Enter a valid email address.");
    }

    [TestMethod]
    public void Equality_is_ordinal_on_the_canonical_key_only()
    {
        var bob = EmailAddress.Parse("Bob@Example.com");
        var same = EmailAddress.Parse("bob@EXAMPLE.COM");

        bob.Should().Be(same);
        (bob == same).Should().BeTrue();
        (bob != same).Should().BeFalse();
        bob.GetHashCode().Should().Be(same.GetHashCode());
        bob.Address.Should().NotBe(same.Address, "the display form is not part of equality");
        new HashSet<EmailAddress> { bob, same }.Should().ContainSingle();
        bob.Equals((object?)null).Should().BeFalse();
        (bob == null).Should().BeFalse();
        ((EmailAddress?)null == null).Should().BeTrue();
    }

    [TestMethod]
    public void A_stored_address_matches_only_the_same_mailbox()
    {
        var bob = EmailAddress.Parse("bob@business.com");

        bob.MatchesStored("BOB@BUSINESS.COM").Should().BeTrue();
        bob.MatchesStored("  bob@business.com ").Should().BeTrue();
        bob.MatchesStored("bob@busineß.com").Should().BeFalse();
        bob.MatchesStored("boſ@business.com").Should().BeFalse("a stored address that is not valid never matches");
        bob.MatchesStored(null).Should().BeFalse();
        EmailAddress.Parse("ü@münchen.de").MatchesStored("ü@MÜNCHEN.DE").Should().BeTrue("a row keyed by 7.2.0 is re-canonicalized");
    }

    [TestMethod]
    public void Lookup_keys_add_the_7_2_0_keys_only_for_non_ascii_input()
    {
        var ascii = EmailAddress.Parse("bob@example.com");
        EmailAddress.LookupKeys(ascii.Canonical, "bob@example.com").Should().Equal(ascii.Canonical);

        var idn = EmailAddress.Parse("ü@münchen.de");
        EmailAddress.LookupKeys(idn.Canonical, "ü@münchen.de").Should().Equal(idn.Canonical, "Ü@MÜNCHEN.DE");
        EmailAddress.LookupKeys(idn.Canonical, null).Should().Equal(idn.Canonical);
        EmailAddress.LegacyKey(" Bob@Example.com ").Should().Be("BOB@EXAMPLE.COM");
    }

    [TestMethod]
    public void The_fallback_key_for_invalid_input_never_equals_a_valid_key()
    {
        EmailAddress.FallbackKey("  not-an-email ").Should().Be("NOT-AN-EMAIL");
        EmailAddress.FallbackKey("boſ@example.com").Should().Be("BOſ@EXAMPLE.COM");
        EmailAddress.FallbackKey("boſ@example.com").Should().NotBe(EmailAddress.Parse("bos@example.com").Canonical);
        EmailAddress.FallbackKey(null).Should().BeEmpty();
    }

    [TestMethod]
    public void Domains_canonicalize_to_lower_case_idna_ascii()
    {
        EmailAddress.TryCanonicalizeDomain("MÜNCHEN.de", out var idn).Should().BeTrue();
        idn.Should().Be("xn--mnchen-3ya.de");
        EmailAddress.TryCanonicalizeDomain("Example.COM", out var ascii).Should().BeTrue();
        ascii.Should().Be("example.com");
        EmailAddress.TryCanonicalizeDomain("exa\u200Bmple.com", out _).Should().BeFalse();
        EmailAddress.TryCanonicalizeDomain(string.Empty, out _).Should().BeFalse();
        EmailAddress.TryCanonicalizeDomain(null, out _).Should().BeFalse();
        EmailAddress.DomainOf("BOB@XN--MNCHEN-3YA.DE").Should().Be("xn--mnchen-3ya.de");
    }

    [TestMethod]
    public void The_7x_string_entry_points_agree_with_the_value_object()
    {
        foreach (var input in new[] { "Bob@Example.COM", "ü@münchen.de", "boſ@example.com", "bob", "<b>@example.com", " x@y.z " })
        {
            var parsed = EmailAddress.TryParse(input, out var email);

            SqlOSEmailAddress.TryCanonicalize(input, out var address, out var key).Should().Be(parsed, input);
            if (parsed)
            {
                address.Should().Be(email!.Address);
                key.Should().Be(email.Canonical);
                SqlOSAdminService.NormalizeEmail(input).Should().Be(email.Canonical);
            }
        }
    }
}
