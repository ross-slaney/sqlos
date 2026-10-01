using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class DomainNameTests
{
    private static readonly DomainNameRules HostRules = new(["sqlos.example", " Corp.Example. ", "  "], AllowLocalhost: false);

    [DataTestMethod]
    [DataRow("example.com", "example.com")]
    [DataRow("Example.COM", "example.com")]
    [DataRow("  sub.example.co.uk  ", "sub.example.co.uk")]
    [DataRow("https://Example.com/path?q=1", "example.com")]
    [DataRow("http://example.com:8080", "example.com")]
    [DataRow("bob@Example.com", "example.com")]
    [DataRow("mailto:bob@example.com", "example.com")]
    [DataRow("example.com.", "example.com")]
    [DataRow("..example.com..", "example.com")]
    [DataRow("[example.com]", "example.com")]
    [DataRow("MÜNCHEN.de", "xn--mnchen-3ya.de")]
    [DataRow("xn--mnchen-3ya.de", "xn--mnchen-3ya.de")]
    [DataRow("1.example", "1.example")]
    [DataRow("a-b.example", "a-b.example")]
    [DataRow("1.2.3.4.5", "1.2.3.4.5")]
    [DataRow("例え.テスト", "xn--r8jz45g.xn--zckzah")]
    [DataRow("ﬁ.com", "fi.com")]
    public void A_claimable_domain_normalizes_to_its_lower_case_idna_ascii_name(string input, string expected)
    {
        var domain = DomainName.Parse(input, DomainNameRules.Default);

        domain.Value.Should().Be(expected);
        domain.ToString().Should().Be(expected);
    }

    [DataTestMethod]
    [DataRow(null, "Domain is required.")]
    [DataRow("", "Domain is required.")]
    [DataRow("   ", "Domain is required.")]
    [DataRow("*.example.com", "Wildcard domains cannot be verified.")]
    [DataRow("ex*ample.com", "Wildcard domains cannot be verified.")]
    [DataRow("bob@*.example.com", "Wildcard domains cannot be verified.")]
    [DataRow("a..b.com", "Domain is not a valid DNS name.")]
    [DataRow("@", "Domain is not a valid DNS name.")]
    [DataRow("127.0.0.1", "IP addresses cannot be verified as organization domains.")]
    [DataRow("http://10.0.0.1/", "IP addresses cannot be verified as organization domains.")]
    [DataRow("123", "IP addresses cannot be verified as organization domains.")]
    [DataRow("localhost", "Localhost domain verification is disabled.")]
    [DataRow("LOCALHOST", "Localhost domain verification is disabled.")]
    [DataRow("com", "Domain must include a public DNS suffix.")]
    [DataRow("-example.com", "Domain is not a valid DNS name.")]
    [DataRow("example-.com", "Domain is not a valid DNS name.")]
    [DataRow("xn--a.com", "Domain is not a valid DNS name.")]
    [DataRow("exa_mple.com", "Domain contains characters that are not valid in DNS labels.")]
    [DataRow("exa mple.com", "Domain contains characters that are not valid in DNS labels.")]
    [DataRow("_dmarc.example.com", "Domain contains characters that are not valid in DNS labels.")]
    public void An_unclaimable_domain_is_rejected_with_its_7x_message(string? input, string message)
    {
        DomainName.TryParse(input, DomainNameRules.Default, out var domain, out var error).Should().BeFalse();
        domain.Should().BeNull();
        error.Should().Be(message);
        FluentActions.Invoking(() => DomainName.Parse(input, DomainNameRules.Default))
            .Should().Throw<SqlOSDomainException>()
            .Where(exception => exception.Error == SqlOSDomainError.InvalidDomainName)
            .WithMessage(message);
    }

    [TestMethod]
    public void Idna_rejects_overlong_labels_and_names_before_the_label_rules()
    {
        // .NET's IdnMapping (ICU, UTS 46) rejects leading or trailing hyphens, labels over 63
        // characters and names over the DNS limit itself, so 7.2.1 answers with the IDNA message for
        // them; its own label messages remain for what IDNA accepts.
        DomainName.TryParse(new string('a', 63) + ".com", DomainNameRules.Default, out _, out _).Should().BeTrue();
        DomainName.TryParse(new string('a', 64) + ".com", DomainNameRules.Default, out _, out var labelError).Should().BeFalse();
        labelError.Should().Be("Domain is not a valid DNS name.");

        var longest = string.Join('.', Enumerable.Repeat(new string('b', 60), 4)) + ".com";
        DomainName.TryParse(longest, DomainNameRules.Default, out var accepted, out _).Should().BeTrue();
        accepted!.Value.Length.Should().Be(247);
        var tooLong = string.Join('.', Enumerable.Repeat(new string('b', 63), 4)) + ".com";
        DomainName.TryParse(tooLong, DomainNameRules.Default, out _, out var lengthError).Should().BeFalse();
        lengthError.Should().Be("Domain is not a valid DNS name.");
    }

    [TestMethod]
    public void Localhost_is_claimable_only_when_the_host_allows_it()
    {
        var rules = DomainNameRules.Default with { AllowLocalhost = true };

        DomainName.Parse("localhost", rules).Value.Should().Be("localhost");
        DomainName.Parse("http://LOCALHOST:5000", rules).Value.Should().Be("localhost");
    }

    [DataTestMethod]
    [DataRow("sqlos.example", "sqlos.example")]
    [DataRow("app.sqlos.example", "sqlos.example")]
    [DataRow("a.b.SQLOS.example", "sqlos.example")]
    [DataRow("corp.example", "corp.example")]
    [DataRow("x.corp.example", "corp.example")]
    public void The_hosts_own_namespace_is_reserved(string input, string root)
    {
        DomainName.TryParse(input, HostRules, out _, out var error).Should().BeFalse();
        error.Should().Be($"Domain is reserved by the SqlOS host: {root}.");
    }

    [DataTestMethod]
    [DataRow("notsqlos.example")]
    [DataRow("sqlos.example.com")]
    [DataRow("example.com")]
    public void A_reserved_root_matches_only_whole_labels(string input)
    {
        DomainName.TryParse(input, HostRules, out var domain, out _).Should().BeTrue();
        domain!.Value.Should().Be(input);
    }

    [TestMethod]
    public void A_misconfigured_reserved_root_throws_when_a_domain_reaches_the_check()
    {
        var rules = new DomainNameRules(["a..b"], AllowLocalhost: false);

        DomainName.TryParse("*.example.com", rules, out _, out var error).Should().BeFalse("earlier rules still answer first");
        error.Should().Be("Wildcard domains cannot be verified.");
        FluentActions.Invoking(() => DomainName.TryParse("example.com", rules, out _, out _))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Reserved domain root is invalid: a..b.");
    }

    [TestMethod]
    public void Domain_names_are_equal_by_value()
    {
        DomainName.Parse("Example.com", DomainNameRules.Default).Should().Be(DomainName.Parse("https://example.COM/x", DomainNameRules.Default));
        DomainName.Parse("example.com", DomainNameRules.Default).Should().NotBe(DomainName.Parse("example.org", DomainNameRules.Default));
    }

    [TestMethod]
    public void The_7x_claim_normalization_delegates_with_identical_results_and_messages()
    {
        var options = new SqlOSSsoPortalOptions();
        options.ReservedDomainRoots.Add("sqlos.example");

        SqlOSDomainOwnershipVerification.NormalizeDomain("https://Example.com/x", options).Should().Be("example.com");
        foreach (var input in new[] { "", "*.x.com", "127.0.0.1", "localhost", "com", "-a.com", "a_b.com", "app.sqlos.example", "a..b.com" })
        {
            DomainName.TryParse(input, SqlOSDomainOwnershipVerification.ToDomainNameRules(options), out _, out var error).Should().BeFalse(input);
            FluentActions.Invoking(() => SqlOSDomainOwnershipVerification.NormalizeDomain(input, options))
                .Should().Throw<InvalidOperationException>()
                .Where(exception => exception.GetType() == typeof(InvalidOperationException), "7.x threw a plain InvalidOperationException")
                .WithMessage(error!);
        }
    }
}
