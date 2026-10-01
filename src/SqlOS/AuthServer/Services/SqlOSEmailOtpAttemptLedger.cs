using System.Transactions;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Spends email-code attempts in the database (#424). An attempt is spent with one conditional
/// update before its code is compared, and a wrong code that spent the last attempt invalidates
/// the challenge with another, so concurrent verifications can neither compare more codes than
/// the budget allows nor lose each other's attempts.
/// </summary>
/// <remarks>
/// <para>
/// The updates are the challenge's own (<see cref="SqlOSEmailOtpChallenge.AtomicAttempts"/>) and
/// bypass the change tracker, so no later save of the tracked challenge writes a stale count over
/// them. They commit on their own, which is the point: a verification that runs inside a database
/// transaction would have its attempt rolled back with that transaction, so it is refused, except
/// for sign-up verification, which still runs inside the sign-up transaction as in 7.2.1
/// (<see cref="EmailOtpAttemptScope.SignupTransaction"/>).
/// </para>
/// <para>
/// A store without set-based updates (the EF Core in-memory provider used by unit tests) spends
/// the attempt on the tracked challenge instead, and the caller's save persists it.
/// </para>
/// </remarks>
internal sealed class SqlOSEmailOtpAttemptLedger
{
    internal const string TransactionRefusedMessage =
        "Email OTP verification cannot run inside a database transaction: a rollback would erase the attempt it counts.";

    private readonly ISqlOSAuthServerDbContext _context;

    public SqlOSEmailOtpAttemptLedger(ISqlOSAuthServerDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Spends one attempt of <paramref name="challenge"/>'s budget, if it is still open at
    /// <paramref name="now"/> with an attempt left, and returns the reservation the code may then be
    /// registered against; null when no attempt could be spent.
    /// </summary>
    public async Task<EmailOtpAttemptReservation?> TryReserveAsync(
        SqlOSEmailOtpChallenge challenge,
        DateTime now,
        EmailOtpAttemptScope scope,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            if (!challenge.IsOpen(now))
            {
                return null;
            }

            challenge.SpendAttemptInMemory();
            return new EmailOtpAttemptReservation(challenge.Id);
        }

        if (scope == EmailOtpAttemptScope.Independent)
        {
            EnsureNoCallerTransaction();
        }

        var spent = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(SqlOSEmailOtpChallenge.AtomicAttempts.CanSpend(challenge.Id, now))
            .ExecuteUpdateAsync(SqlOSEmailOtpChallenge.AtomicAttempts.Spend, cancellationToken);
        return spent == 1 ? new EmailOtpAttemptReservation(challenge.Id) : null;
    }

    /// <summary>
    /// After a wrong code, invalidates the challenge once its attempts are all spent. Returns true
    /// when this call invalidated it, so exactly one wrong code reports the exhausted budget.
    /// </summary>
    public async Task<bool> TryExhaustAsync(
        SqlOSEmailOtpChallenge challenge,
        EmailOtpAttemptReservation reservation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (!_context.Database.IsRelational())
        {
            return challenge.ExhaustInMemory(now);
        }

        var invalidated = await _context.Set<SqlOSEmailOtpChallenge>()
            .Where(SqlOSEmailOtpChallenge.AtomicAttempts.IsSpentOut(reservation.ChallengeId))
            .ExecuteUpdateAsync(SqlOSEmailOtpChallenge.AtomicAttempts.Invalidate(now), cancellationToken);
        return invalidated == 1;
    }

    private void EnsureNoCallerTransaction()
    {
        if (_context.Database.CurrentTransaction is not null
            || _context.Database.GetEnlistedTransaction() is not null
            || Transaction.Current is not null)
        {
            throw new InvalidOperationException(TransactionRefusedMessage);
        }
    }
}

/// <summary>Whether an email-code attempt commits on its own or with the caller's transaction.</summary>
internal enum EmailOtpAttemptScope
{
    /// <summary>
    /// The attempt commits on its own, before the code is compared, so no caller can roll it back
    /// (#424). Verifying inside a database transaction is refused.
    /// </summary>
    Independent = 1,

    /// <summary>
    /// Sign-up verification, which still runs inside the sign-up transaction: its attempt commits
    /// or rolls back with the sign-up, as in 7.2.1 (#449). The 7.2.2 fix verifies sign-up codes
    /// before that transaction opens; carrying it forward removes this scope.
    /// </summary>
    SignupTransaction = 2
}
