using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class VerificationTests
{
    [TestMethod]
    public void Verifying_an_unverified_item_records_now()
    {
        var verified = new Verification(false, null).Verify(Now);

        verified.IsVerified.Should().BeTrue();
        verified.VerifiedAt.Should().Be(Now);
    }

    [TestMethod]
    public void Verifying_again_keeps_the_time_ownership_was_first_proven()
    {
        var first = new Verification(false, null).Verify(Now);

        var again = first.Verify(Now.AddDays(3));

        again.Should().Be(first);
        again.VerifiedAt.Should().Be(Now);
    }

    [TestMethod]
    public void A_verified_row_without_a_time_gets_one_like_the_7x_rule()
    {
        // 7.x: existing.IsVerified = true; existing.VerifiedAt ??= now;
        var legacy = new Verification(true, null);

        legacy.Verify(Now).Should().Be(new Verification(true, Now));
    }

    [TestMethod]
    public void Verifying_an_unverified_row_replaces_a_stale_time()
    {
        var stale = new Verification(false, Now.AddYears(-1));

        stale.Verify(Now).Should().Be(new Verification(true, Now));
    }

    [TestMethod]
    public void The_default_verification_is_unverified()
    {
        default(Verification).IsVerified.Should().BeFalse();
        default(Verification).VerifiedAt.Should().BeNull();
    }

    [TestMethod]
    public void Verifications_are_equal_by_flag_and_time()
    {
        new Verification(true, Now).Should().Be(new Verification(true, Now));
        new Verification(true, Now).Should().NotBe(new Verification(false, Now));
        new Verification(true, Now).Should().NotBe(new Verification(true, null));
    }
}
