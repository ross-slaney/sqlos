using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.Database;

/// <summary>
/// Turns the domain events of a save into audit rows written by that same save, and hands them to
/// post-commit handlers once the change has committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>While saving</b>, it drains the events of every tracked <see cref="ISqlOSAggregate"/> and the
/// failure records of the unit of work (<see cref="SqlOSUnitOfWorkEvents"/>), projects each through
/// <see cref="SqlOSAuditProjection"/> and adds the rows to the save, so they commit or roll back
/// with the change. Projecting may raise further events; it drains again until none are left.
/// Rows are ordered by when their events were raised.
/// </para>
/// <para>
/// <b>A save that fails or is canceled</b> leaves the unit of work exactly as it was: its rows are
/// detached and its events go back to the aggregates that raised them, so a retried save projects
/// them once and an abandoned unit of work writes nothing.
/// </para>
/// <para>
/// <b>After the save</b>, the events go to every <see cref="ISqlOSPostCommitHandler{TEvent}"/>
/// (<see cref="SqlOSPostCommitDispatch"/>): at once when the save committed its own transaction,
/// when the EF transaction the save ran in commits, or when the ambient
/// <see cref="System.Transactions.TransactionScope"/> it enlisted in commits. A transaction that
/// rolls back, or ends without committing, takes them with it. A commit that fails keeps them, so
/// a retried commit that succeeds still runs them. Two things stay invisible: a transaction
/// committed straight through ADO.NET rather than through EF, and a rollback to a savepoint, which
/// does not withdraw the events of the saves it undoes.
/// </para>
/// <para>
/// One shared instance serves every context: a save in progress is tracked by its context, and
/// events awaiting a commit by EF's transaction object, which is new for every transaction a
/// context begins or uses. Never by the ADO.NET <see cref="DbTransaction"/>: Npgsql reuses one for
/// every transaction on a pooled connection, so a transaction disposed without committing would
/// hand its events to the next one. <c>AddSqlOS</c> attaches it to the host's context
/// through <c>ConfigureDbContext</c> and <see cref="SqlOSDbContext{TContext}"/> attaches it in its
/// constructor; both check first, so a context never runs it twice. The clock, the projection, the
/// request context and the handlers come from the services of the scope that created the context.
/// A context built without dependency injection uses <see cref="TimeProvider.System"/>,
/// <see cref="SqlOSAuditProjection.Default"/> and <see cref="SqlOSRequestContext.System"/>, and has
/// no handlers.
/// </para>
/// </remarks>
internal sealed class SqlOSDomainEventsInterceptor : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    /// <summary>
    /// How many times one save drains again for events raised while projecting. Projections that
    /// keep raising events are a bug; the save fails instead of looping.
    /// </summary>
    internal const int MaxDrainRounds = 8;

    private readonly ConditionalWeakTable<DbContext, PendingSave> _saving = new();
    private readonly ConditionalWeakTable<IDbContextTransaction, List<SqlOSPostCommitDispatch>> _awaitingCommit = new();

    private SqlOSDomainEventsInterceptor()
    {
    }

    public static SqlOSDomainEventsInterceptor Instance { get; } = new();

    /// <summary>Adds the interceptor to <paramref name="options"/> unless it is already there.</summary>
    public static DbContextOptionsBuilder AttachTo(DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsAttached(options.Options) ? options : options.AddInterceptors(Instance);
    }

    /// <summary>The options with the interceptor added, unless it is already there.</summary>
    public static DbContextOptions<TContext> AttachTo<TContext>(DbContextOptions<TContext> options)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsAttached(options)
            ? options
            : new DbContextOptionsBuilder<TContext>(options).AddInterceptors(Instance).Options;
    }

    internal static bool IsAttached(IDbContextOptions options)
        => options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(Instance) == true;

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context, result);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context, result);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(eventData.Context)?.RunAsync().GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (Complete(eventData.Context) is { } dispatch)
        {
            await dispatch.RunAsync();
        }

        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Abandon(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Abandon(eventData.Context);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => Abandon(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Abandon(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        foreach (var dispatch in TakeAwaitingCommit(eventData))
        {
            dispatch.RunAsync().GetAwaiter().GetResult();
        }
    }

    public async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        foreach (var dispatch in TakeAwaitingCommit(eventData))
        {
            await dispatch.RunAsync();
        }
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => _ = TakeAwaitingCommit(eventData);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _ = TakeAwaitingCommit(eventData);
        return Task.CompletedTask;
    }

    private void Stage(DbContext? context, InterceptionResult<int> result)
    {
        if (context is null || result.HasResult)
        {
            // Another interceptor answered the save itself; nothing is written.
            return;
        }

        // A save that ended without completing or failing (it threw before EF began writing) is
        // abandoned, so its events are projected afresh with this one.
        Abandon(context);

        var drained = Drain(context);
        if (drained.Count == 0)
        {
            return;
        }

        var services = ApplicationServices(context);
        var projection = services?.GetService<SqlOSAuditProjection>() ?? SqlOSAuditProjection.Default;
        var request = RequestContext(services);
        var now = (services?.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var save = new PendingSave(drained, request);
        try
        {
            for (var round = 1; drained.Count > 0; round++)
            {
                if (round > MaxDrainRounds)
                {
                    throw new InvalidOperationException(
                        $"Projecting domain events kept raising new events after {MaxDrainRounds} rounds; the save was not attempted.");
                }

                foreach (var item in drained)
                {
                    var row = projection.Project(
                        item.Raised.Event,
                        new SqlOSAuditProjectionContext(now.AddTicks(save.Rows.Count * TimeSpan.TicksPerMicrosecond), request));
                    if (row is not null)
                    {
                        context.Add(row);
                        save.Rows.Add(row);
                    }

                    save.Events.Add(item.Raised.Event);
                }

                drained = Drain(context);
            }
        }
        catch
        {
            Restore(context, save);
            throw;
        }

        _saving.AddOrUpdate(context, save);
    }

    /// <summary>
    /// Ends the save of <paramref name="context"/>: returns its post-commit dispatch when it can run
    /// now, or <see langword="null"/> when it has none or waits for a transaction to commit.
    /// </summary>
    private SqlOSPostCommitDispatch? Complete(DbContext? context)
    {
        if (context is null || !_saving.TryGetValue(context, out var save))
        {
            return null;
        }

        _saving.Remove(context);
        var dispatch = SqlOSPostCommitDispatch.For(ApplicationServices(context), save.Events, save.Request);
        if (dispatch is null || !context.Database.IsRelational())
        {
            return dispatch;
        }

        if (context.Database.CurrentTransaction is { } transaction)
        {
            _awaitingCommit.GetValue(transaction, static _ => []).Add(dispatch);
            return null;
        }

        // Inside an ambient transaction EF begins none of its own: the save commits with it.
        if ((context.Database.GetEnlistedTransaction() ?? Transaction.Current) is { } ambient)
        {
            RunWhenCommitted(ambient, dispatch);
            return null;
        }

        return dispatch;
    }

    private void Abandon(DbContext? context)
    {
        if (context is null || !_saving.TryGetValue(context, out var save))
        {
            return;
        }

        _saving.Remove(context);
        Restore(context, save);
    }

    /// <summary>Removes and returns the dispatches waiting for the transaction that is ending.</summary>
    private IReadOnlyList<SqlOSPostCommitDispatch> TakeAwaitingCommit(TransactionEndEventData eventData)
    {
        // EF still reports the ending transaction as current while it notifies interceptors.
        if (eventData.Context?.Database.CurrentTransaction is not { } transaction
            || transaction.TransactionId != eventData.TransactionId
            || !_awaitingCommit.TryGetValue(transaction, out var dispatches))
        {
            return [];
        }

        _awaitingCommit.Remove(transaction);
        return dispatches;
    }

    private static void RunWhenCommitted(Transaction ambient, SqlOSPostCommitDispatch dispatch)
        => ambient.TransactionCompleted += (_, completed) =>
        {
            if (completed.Transaction?.TransactionInformation.Status != TransactionStatus.Committed)
            {
                return;
            }

            // The handlers run outside the finished transaction, as after any other commit.
            using var outside = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            dispatch.RunAsync().GetAwaiter().GetResult();
        };

    private static List<DrainedEvent> Drain(DbContext context)
    {
        var drained = new List<DrainedEvent>();
        var tracker = context.ChangeTracker;
        var detectChanges = tracker.AutoDetectChangesEnabled;

        // Events are not EF state: finding the aggregates needs no DetectChanges, which the save
        // runs right after this interceptor anyway.
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries())
            {
                if (entry.Entity is ISqlOSAggregate aggregate)
                {
                    Take(aggregate.Events);
                }
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = detectChanges;
        }

        if (SqlOSUnitOfWorkEvents.TryGet(context, out var unitOfWork))
        {
            Take(unitOfWork);
        }

        drained.Sort(static (left, right) => left.Raised.Sequence.CompareTo(right.Raised.Sequence));
        return drained;

        void Take(DomainEventBuffer buffer)
        {
            foreach (var raised in buffer.Drain())
            {
                drained.Add(new DrainedEvent(buffer, raised));
            }
        }
    }

    /// <summary>
    /// Detaches the rows a save added and returns the events it drained before projecting to the
    /// buffers that raised them. Events raised while projecting are not returned: projecting the
    /// returned events raises them again.
    /// </summary>
    private static void Restore(DbContext context, PendingSave save)
    {
        foreach (var row in save.Rows)
        {
            var entry = context.Entry(row);
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }

        foreach (var source in save.Drained.GroupBy(static item => item.Source))
        {
            source.Key.Requeue(source.Select(static item => item.Raised));
        }
    }

    private static IServiceProvider? ApplicationServices(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

    private static SqlOSRequestContext RequestContext(IServiceProvider? services)
    {
        try
        {
            return services?.GetService<SqlOSRequestContextAccessor>()?.Current ?? SqlOSRequestContext.System;
        }
        catch (InvalidOperationException)
        {
            // A pooled context is created from the root provider, which cannot resolve scoped services.
            return SqlOSRequestContext.System;
        }
    }

    private readonly record struct DrainedEvent(DomainEventBuffer Source, RaisedDomainEvent Raised);

    private sealed class PendingSave(List<DrainedEvent> drained, SqlOSRequestContext request)
    {
        /// <summary>The events drained before projecting, which a failed save returns.</summary>
        public List<DrainedEvent> Drained { get; } = drained;

        public SqlOSRequestContext Request { get; } = request;

        /// <summary>Every event of the save, in raise order, for the post-commit handlers.</summary>
        public List<ISqlOSDomainEvent> Events { get; } = [];

        public List<SqlOSAuditEvent> Rows { get; } = [];
    }
}
