using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class EnablementTests
{
    [TestMethod]
    public void An_active_item_without_a_disable_record_is_enabled()
    {
        Enablement.Enabled.IsEnabled.Should().BeTrue();
        new Enablement(true).IsEnabled.Should().BeTrue();
        FluentActions.Invoking(() => Enablement.Enabled.EnsureEnabled()).Should().NotThrow();
    }

    [TestMethod]
    public void Disabling_records_the_time_and_reason()
    {
        var disabled = Enablement.Enabled.Disable("disabled_by_operator", Now);

        disabled.Should().Be(new Enablement(false, Now, "disabled_by_operator"));
        disabled.IsEnabled.Should().BeFalse();
        FluentActions.Invoking(() => disabled.EnsureEnabled())
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.Disabled);
    }

    [TestMethod]
    public void Disabling_again_keeps_the_first_disable_record()
    {
        var first = Enablement.Enabled.Disable("disabled_by_operator", Now);

        first.Disable("client_emergency_disabled", Now.AddHours(1)).Should().Be(first);
    }

    [TestMethod]
    public void Enabling_clears_the_disable_record()
    {
        var disabled = Enablement.Enabled.Disable("disabled_by_operator", Now);

        disabled.Enable().Should().Be(Enablement.Enabled);
        Enablement.Enabled.Enable().Should().Be(Enablement.Enabled);
    }

    [TestMethod]
    public void An_inactive_item_without_a_disable_record_is_disabled_by_its_definition()
    {
        // How a client disabled in its code-owned seed looks: whether it may be enabled at runtime
        // is the client's rule, not the part's.
        var seedDisabled = new Enablement(false);

        seedDisabled.IsEnabled.Should().BeFalse();
        seedDisabled.DisabledAt.Should().BeNull();
        seedDisabled.Disable("disabled_by_operator", Now).Should().Be(new Enablement(false, Now, "disabled_by_operator"));
    }

    [TestMethod]
    public void A_flag_only_entity_keeps_its_flag_through_the_transitions()
    {
        // Users, organizations and memberships store only IsActive.
        var inactive = new Enablement(IsActive: false);

        inactive.Disable("deactivated", Now).IsActive.Should().BeFalse();
        inactive.Enable().IsActive.Should().BeTrue();
        new Enablement(IsActive: true).Disable("deactivated", Now).IsActive.Should().BeFalse();
    }

    [TestMethod]
    public void An_active_row_with_a_disable_record_fails_closed()
    {
        new Enablement(true, Now, "inconsistent").IsEnabled.Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void Disabling_needs_a_reason(string? reason)
    {
        FluentActions.Invoking(() => Enablement.Enabled.Disable(reason!, Now)).Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void The_default_enablement_is_disabled()
    {
        default(Enablement).IsEnabled.Should().BeFalse();
        FluentActions.Invoking(() => default(Enablement).EnsureEnabled()).Should().Throw<SqlOSDomainException>();
    }

    [TestMethod]
    public void Enablements_are_equal_by_flag_time_and_reason()
    {
        new Enablement(false, Now, "a").Should().Be(new Enablement(false, Now, "a"));
        new Enablement(false, Now, "a").Should().NotBe(new Enablement(false, Now, "b"));
        new Enablement(false, Now, "a").Should().NotBe(new Enablement(false, null, "a"));
        Enablement.Enabled.Should().Be(new Enablement(true));
    }
}
