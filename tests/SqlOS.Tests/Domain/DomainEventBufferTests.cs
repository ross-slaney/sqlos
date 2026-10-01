using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class DomainEventBufferTests
{
    private sealed record Happened(string Name) : ISqlOSDomainEvent;

    [TestMethod]
    public void Raised_events_are_pending_in_raise_order_until_drained()
    {
        var buffer = new DomainEventBuffer();
        buffer.Raise(new Happened("first"));
        buffer.Raise(new Happened("second"));

        buffer.Pending.Should().Equal(new Happened("first"), new Happened("second"));
        buffer.Pending.Should().HaveCount(2, "reading the pending events does not drain them");
    }

    [TestMethod]
    public void Drain_returns_the_events_with_increasing_sequences_and_empties_the_buffer()
    {
        var buffer = new DomainEventBuffer();
        buffer.Raise(new Happened("first"));
        buffer.Raise(new Happened("second"));

        var drained = buffer.Drain();

        drained.Select(raised => raised.Event).Should().Equal(new Happened("first"), new Happened("second"));
        drained[1].Sequence.Should().BeGreaterThan(drained[0].Sequence);
        buffer.Pending.Should().BeEmpty();
        buffer.Drain().Should().BeEmpty();
    }

    [TestMethod]
    public void Sequences_order_events_across_buffers()
    {
        var user = new DomainEventBuffer();
        var session = new DomainEventBuffer();
        user.Raise(new Happened("user changed"));
        session.Raise(new Happened("session revoked"));
        user.Raise(new Happened("user changed again"));

        var merged = user.Drain().Concat(session.Drain()).OrderBy(raised => raised.Sequence).Select(raised => raised.Event);

        merged.Should().Equal(new Happened("user changed"), new Happened("session revoked"), new Happened("user changed again"));
    }

    [TestMethod]
    public void Requeue_restores_drained_events_ahead_of_newer_ones()
    {
        var buffer = new DomainEventBuffer();
        buffer.Raise(new Happened("first"));
        buffer.Raise(new Happened("second"));
        var drained = buffer.Drain();
        buffer.Raise(new Happened("third"));

        buffer.Requeue(drained);

        buffer.Pending.Should().Equal(new Happened("first"), new Happened("second"), new Happened("third"));
        buffer.Drain().Select(raised => raised.Sequence).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public void Null_events_are_rejected()
    {
        var buffer = new DomainEventBuffer();

        FluentActions.Invoking(() => buffer.Raise(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => buffer.Requeue(null!)).Should().Throw<ArgumentNullException>();
    }
}
