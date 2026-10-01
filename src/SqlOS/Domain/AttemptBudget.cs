namespace SqlOS.Domain;

/// <summary>
/// A limited number of attempts (<c>AttemptCount</c> against a limit such as <c>MaxAttempts</c>).
/// The budget is exhausted once the attempts spent reach the limit, the 7.x rule for email codes
/// (<c>AttemptCount &gt;= MaxAttempts</c> invalidates the challenge).
/// </summary>
/// <remarks>
/// Spending an attempt from an exhausted budget is a broken invariant. An email-code challenge
/// spends one attempt per verification, in the database with one conditional update before the
/// code is compared, and is invalidated once the budget is exhausted, so parallel guesses each
/// spend an attempt and never more than the limit between them (#424). Stored rows may hold more
/// attempts than the limit (a lowered limit), which simply reads as exhausted. The default value
/// has a limit of zero and is exhausted.
/// </remarks>
internal readonly record struct AttemptBudget
{
    public AttemptBudget(int spent, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Spent = spent;
        Limit = limit;
    }

    public int Spent { get; }

    public int Limit { get; }

    public int Remaining => Math.Max(0, Limit - Spent);

    public bool IsExhausted => Spent >= Limit;

    public AttemptBudget Spend()
        => IsExhausted
            ? throw SqlOSDomainException.Of(SqlOSDomainError.AttemptsExhausted)
            : new AttemptBudget(Spent + 1, Limit);
}
