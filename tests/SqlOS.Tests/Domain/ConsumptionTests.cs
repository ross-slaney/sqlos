using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class ConsumptionTests
{
    [TestMethod]
    public void An_unconsumed_item_is_consumed_at_now()
    {
        var unconsumed = new Consumption(null);

        var consumed = unconsumed.Consume(Now);

        unconsumed.IsConsumed.Should().BeFalse();
        consumed.IsConsumed.Should().BeTrue();
        consumed.ConsumedAt.Should().Be(Now);
    }

    [TestMethod]
    public void Consuming_twice_is_a_broken_invariant()
    {
        var consumed = new Consumption(null).Consume(Now);

        FluentActions.Invoking(() => consumed.Consume(Now.AddSeconds(1)))
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.AlreadyConsumed);
        consumed.ConsumedAt.Should().Be(Now, "a failed transition never changes the part");
    }

    [TestMethod]
    public void A_stored_consumption_cannot_be_consumed_again_even_at_the_same_instant()
    {
        var stored = new Consumption(Now);

        FluentActions.Invoking(() => stored.Consume(Now))
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.AlreadyConsumed);
    }

    [TestMethod]
    public void The_default_consumption_is_unconsumed()
    {
        default(Consumption).IsConsumed.Should().BeFalse();
        default(Consumption).Consume(Now).ConsumedAt.Should().Be(Now);
    }

    [TestMethod]
    public void Consumptions_are_equal_by_time()
    {
        new Consumption(Now).Should().Be(new Consumption(null).Consume(Now));
        new Consumption(Now).Should().NotBe(new Consumption(null));
        new Consumption(Now).Should().NotBe(new Consumption(Now.Tick(1)));
    }
}
