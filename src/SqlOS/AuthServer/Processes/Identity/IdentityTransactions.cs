using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuthServer.Processes.Identity;

/// <summary>
/// Runs an identity process's work in one database transaction where 7.2.1 did: the work commits
/// only when its outcome says so, and rolls back when it refuses or throws. The saves inside keep
/// their own boundaries; the transaction is what makes them one unit.
/// </summary>
/// <remarks>
/// Work inside a transaction its caller owns joins it, and commits or rolls back with it. A store
/// without transactions (the EF Core in-memory provider unit tests use) runs the work as it is.
/// Sign-ups have their own unit of work (<see cref="SignupUnitOfWork"/>), which also cleans up on such
/// a store.
/// </remarks>
internal static class IdentityTransactions
{
    public static async Task<TOutcome> RunAsync<TOutcome>(
        ISqlOSAuthServerDbContext context,
        Func<Task<TOutcome>> work,
        Func<TOutcome, bool> commits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(commits);
        IDbContextTransaction? transaction = null;
        if (SqlOSSignupOrchestration.SupportsDatabaseTransactions(context) && context.Database.CurrentTransaction == null)
        {
            transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var outcome = await work();
            if (transaction != null)
            {
                if (commits(outcome))
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
            }

            return outcome;
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
