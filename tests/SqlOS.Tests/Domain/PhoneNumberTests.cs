using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class PhoneNumberTests
{
    [DataTestMethod]
    [DataRow("+1 650 253 0000", "US", "+16502530000", "US")]
    [DataRow("(650) 253-0000", "US", "+16502530000", "US")]
    [DataRow("650.253.0000", "US", "+16502530000", "US")]
    [DataRow("  +16502530000  ", null, "+16502530000", "US")]
    [DataRow("+44 20 7031 3000", "US", "+442070313000", "GB")]
    [DataRow("020 7031 3000", "GB", "+442070313000", "GB")]
    [DataRow("+49 30 901820", null, "+4930901820", "DE")]
    [DataRow("tel:+1-650-253-0000", null, "+16502530000", "US")]
    public void A_valid_number_is_written_in_e164_with_its_region(string input, string? defaultRegion, string e164, string region)
    {
        var phone = PhoneNumber.Parse(input, defaultRegion);

        phone.E164.Should().Be(e164);
        phone.Region.Should().Be(region);
        phone.ToString().Should().Be(e164);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void A_blank_number_is_required(string? input)
    {
        PhoneNumber.TryParse(input, "US", out var phone, out var error).Should().BeFalse();
        phone.Should().BeNull();
        error.Should().Be("Phone number is required.");
        FluentActions.Invoking(() => PhoneNumber.Parse(input, "US"))
            .Should().Throw<SqlOSDomainException>()
            .Where(exception => exception.Error == SqlOSDomainError.InvalidPhoneNumber)
            .WithMessage("Phone number is required.");
    }

    [DataTestMethod]
    [DataRow("abc", "US", DisplayName = "not a number")]
    [DataRow("+1 555 0100", "US", DisplayName = "too short")]
    [DataRow("+999 123 4567", "US", DisplayName = "unknown country code")]
    [DataRow("6502530000", null, DisplayName = "national number without a default region")]
    [DataRow("+", "US", DisplayName = "only a plus")]
    [DataRow("+1 650 253 00000000", "US", DisplayName = "too long")]
    public void A_number_libphonenumber_rejects_is_invalid(string input, string? defaultRegion)
    {
        PhoneNumber.TryParse(input, defaultRegion, out var phone, out var error).Should().BeFalse();
        phone.Should().BeNull();
        error.Should().Be("Phone number is invalid.");
        PhoneNumber.TryParse(input, defaultRegion, out _).Should().BeFalse();
    }

    [TestMethod]
    public void The_default_region_is_used_exactly_as_the_caller_passes_it()
    {
        // The phone OTP flow trims and upper-cases its configured region before calling; the OTP
        // test-delivery admin path passes it raw. libphonenumber does not recognize a padded region.
        PhoneNumber.TryParse("650-253-0000", "US", out _).Should().BeTrue();
        PhoneNumber.TryParse("650-253-0000", " US ", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Numbers_are_equal_by_e164()
    {
        PhoneNumber.Parse("+1 (650) 253-0000", null).Should().Be(PhoneNumber.Parse("650-253-0000", "US"));
        PhoneNumber.Parse("+1 (650) 253-0000", null).GetHashCode().Should().Be(PhoneNumber.Parse("6502530000", "US").GetHashCode());
        PhoneNumber.Parse("+1 650 253 0000", null).Should().NotBe(PhoneNumber.Parse("+1 650 253 0001", null));
    }
}
