using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class TimeWindowTests
{
    private static readonly DateTime From = Now;
    private static readonly DateTime To = Now.AddDays(30);

    [TestMethod]
    public void A_grant_window_includes_both_bounds_like_the_fga_access_check()
    {
        var window = TimeWindow.Between(From, To);

        window.Contains(From.Tick(-1)).Should().BeFalse();
        window.Contains(From).Should().BeTrue("EffectiveFrom <= now");
        window.Contains(From.AddDays(10)).Should().BeTrue();
        window.Contains(To).Should().BeTrue("EffectiveTo >= now");
        window.Contains(To.Tick(1)).Should().BeFalse();
        window.IsEndExclusive.Should().BeFalse();
    }

    [TestMethod]
    public void Open_grant_bounds_never_limit_the_window()
    {
        TimeWindow.Between(null, To).Contains(DateTime.MinValue).Should().BeTrue();
        TimeWindow.Between(null, To).Contains(To.Tick(1)).Should().BeFalse();
        TimeWindow.Between(From, null).Contains(DateTime.MaxValue).Should().BeTrue();
        TimeWindow.Between(From, null).Contains(From.Tick(-1)).Should().BeFalse();
        TimeWindow.Between(null, null).Contains(Now).Should().BeTrue();
    }

    [TestMethod]
    public void A_service_account_window_excludes_its_expiry_like_client_authentication()
    {
        var window = TimeWindow.Until(To);

        window.Contains(To.Tick(-1)).Should().BeTrue("ExpiresAt > now");
        window.Contains(To).Should().BeFalse();
        window.Contains(DateTime.MinValue).Should().BeTrue();
        window.IsEndExclusive.Should().BeTrue();
        window.EffectiveFrom.Should().BeNull();
        TimeWindow.Until(null).Contains(DateTime.MaxValue).Should().BeTrue("a service account without ExpiresAt never expires");
    }

    [TestMethod]
    public void An_inverted_window_is_accepted_and_never_in_effect()
    {
        var inverted = TimeWindow.Between(To, From);

        inverted.Contains(From).Should().BeFalse();
        inverted.Contains(To).Should().BeFalse();
        inverted.Contains(From.AddDays(10)).Should().BeFalse();
    }

    [TestMethod]
    public void The_default_window_is_always_in_effect()
    {
        default(TimeWindow).Should().Be(TimeWindow.Always);
        TimeWindow.Always.Contains(DateTime.MinValue).Should().BeTrue();
        TimeWindow.Always.Contains(DateTime.MaxValue).Should().BeTrue();
    }

    [TestMethod]
    public void Windows_are_equal_by_bounds_and_end_rule()
    {
        TimeWindow.Between(From, To).Should().Be(TimeWindow.Between(From, To));
        TimeWindow.Between(null, To).Should().NotBe(TimeWindow.Until(To), "the end instant differs");
        TimeWindow.Between(From, To).Should().NotBe(TimeWindow.Between(From, To.Tick(1)));
    }
}
