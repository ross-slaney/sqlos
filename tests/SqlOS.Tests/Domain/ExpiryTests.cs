using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class ExpiryTests
{
    [TestMethod]
    public void An_expiry_is_active_until_the_tick_before_the_expiry_instant()
    {
        var expiry = new Expiry(Now);

        expiry.IsExpired(Now.Tick(-1)).Should().BeFalse();
        expiry.IsExpired(Now.AddDays(-1)).Should().BeFalse();
        FluentActions.Invoking(() => expiry.EnsureActive(Now.Tick(-1))).Should().NotThrow();
    }

    [TestMethod]
    public void An_expiry_is_expired_at_the_exact_instant_and_after()
    {
        var expiry = new Expiry(Now);

        expiry.IsExpired(Now).Should().BeTrue("ExpiresAt <= now means expired, the 7.x rule");
        expiry.IsExpired(Now.Tick(1)).Should().BeTrue();
        expiry.IsExpired(DateTime.MaxValue).Should().BeTrue();
    }

    [TestMethod]
    public void EnsureActive_throws_the_expired_code_at_the_exact_instant()
    {
        var expiry = new Expiry(Now);

        FluentActions.Invoking(() => expiry.EnsureActive(Now))
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.Expired);
        FluentActions.Invoking(() => expiry.EnsureActive(Now.AddYears(1)))
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.Expired);
    }

    [TestMethod]
    public void After_adds_a_positive_lifetime_to_now()
    {
        Expiry.After(Now, TimeSpan.FromMinutes(10)).ExpiresAt.Should().Be(Now.AddMinutes(10));
        Expiry.After(Now, TimeSpan.FromTicks(1)).Should().Be(new Expiry(Now.Tick(1)));
        Expiry.After(Now, TimeSpan.FromMinutes(10)).ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [DataTestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(-6_000_000_000L)]
    public void After_rejects_a_lifetime_that_is_not_positive(long ticks)
    {
        FluentActions.Invoking(() => Expiry.After(Now, TimeSpan.FromTicks(ticks)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void The_comparison_uses_the_instant_and_ignores_the_kind()
    {
        // Values read through EF before the UTC convention had Kind = Unspecified.
        var stored = new Expiry(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified));

        stored.IsExpired(Now).Should().BeTrue();
        stored.IsExpired(Now.Tick(-1)).Should().BeFalse();
    }

    [TestMethod]
    public void The_default_expiry_is_always_expired()
    {
        default(Expiry).IsExpired(Now).Should().BeTrue();
        default(Expiry).IsExpired(DateTime.MinValue).Should().BeTrue();
    }

    [TestMethod]
    public void Expiries_are_equal_by_instant()
    {
        new Expiry(Now).Should().Be(new Expiry(Now));
        new Expiry(Now).Should().NotBe(new Expiry(Now.Tick(1)));
        (new Expiry(Now) == Expiry.After(Now.AddMinutes(-5), TimeSpan.FromMinutes(5))).Should().BeTrue();
    }
}
