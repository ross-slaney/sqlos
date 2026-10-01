using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class AttemptBudgetTests
{
    [TestMethod]
    public void A_fresh_budget_has_every_attempt_remaining()
    {
        var budget = new AttemptBudget(0, 5);

        budget.Spent.Should().Be(0);
        budget.Limit.Should().Be(5);
        budget.Remaining.Should().Be(5);
        budget.IsExhausted.Should().BeFalse();
    }

    [TestMethod]
    public void Spending_the_last_attempt_exhausts_the_budget_like_the_7x_email_code_rule()
    {
        var budget = new AttemptBudget(0, 5);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            budget = budget.Spend();
            budget.Spent.Should().Be(attempt);
            budget.IsExhausted.Should().BeFalse($"{attempt} of 5 attempts leaves some");
        }

        budget = budget.Spend();

        budget.Spent.Should().Be(5);
        budget.Remaining.Should().Be(0);
        budget.IsExhausted.Should().BeTrue("AttemptCount >= MaxAttempts invalidates the challenge");
    }

    [TestMethod]
    public void Spending_from_an_exhausted_budget_is_a_broken_invariant()
    {
        var exhausted = new AttemptBudget(5, 5);

        FluentActions.Invoking(() => exhausted.Spend())
            .Should().Throw<SqlOSDomainException>()
            .Which.Error.Should().Be(SqlOSDomainError.AttemptsExhausted);
        exhausted.Spent.Should().Be(5);
    }

    [TestMethod]
    public void A_single_attempt_budget_is_exhausted_by_one_failure()
    {
        var budget = new AttemptBudget(0, 1).Spend();

        budget.IsExhausted.Should().BeTrue();
        FluentActions.Invoking(() => budget.Spend()).Should().Throw<SqlOSDomainException>();
    }

    [TestMethod]
    public void A_stored_row_over_its_limit_reads_as_exhausted()
    {
        // The limit was lowered after attempts were spent.
        var budget = new AttemptBudget(7, 5);

        budget.IsExhausted.Should().BeTrue();
        budget.Remaining.Should().Be(0);
        FluentActions.Invoking(() => budget.Spend()).Should().Throw<SqlOSDomainException>();
    }

    [DataTestMethod]
    [DataRow(-1, 5)]
    [DataRow(0, 0)]
    [DataRow(0, -3)]
    [DataRow(-1, 0)]
    public void A_budget_needs_a_positive_limit_and_no_negative_attempts(int spent, int limit)
    {
        FluentActions.Invoking(() => new AttemptBudget(spent, limit))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void The_default_budget_is_exhausted()
    {
        default(AttemptBudget).IsExhausted.Should().BeTrue();
        default(AttemptBudget).Remaining.Should().Be(0);
        FluentActions.Invoking(() => default(AttemptBudget).Spend()).Should().Throw<SqlOSDomainException>();
    }

    [TestMethod]
    public void Budgets_are_equal_by_attempts_and_limit()
    {
        new AttemptBudget(2, 5).Should().Be(new AttemptBudget(1, 5).Spend());
        new AttemptBudget(2, 5).Should().NotBe(new AttemptBudget(2, 6));
        new AttemptBudget(2, 5).Should().NotBe(new AttemptBudget(3, 5));
    }
}
