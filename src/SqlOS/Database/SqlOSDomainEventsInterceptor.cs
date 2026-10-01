using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
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
/// <b>After the save</b>, the events go to every <see cref="ISqlOSPostCommitHandler{TEvent}"/>: at
/// once when the save committed its own transaction, or when the caller's transaction commits. A
/// rolled-back or failed transaction drops them. Handlers run in a scope of their own, with the
/// saving scope's request context, so they never re-enter the context that is completing its save
/// (or whose committed transaction EF still reports as current while it commits).
/// </para>
/// <para>
/// One shared instance serves every context: a save in progress is tracked by its context, and
/// events awaiting a commit by their transaction. <c>AddSqlOS</c> attaches it to the host's context
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

    private static readonly ConcurrentDictionary<Type, PostCommitDispatcher> Dispatchers = new();

    private readonly ConditionalWeakTable<DbContext, PendingSave> _saving = new();
    private readonly ConditionalWeakTable<DbTransaction, List<PendingDispatch>> _awaitingCommit = new();

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
        CompleteAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await CompleteAsync(eventData.Context, cancellationToken);
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
        => DispatchCommittedAsync(transaction, eventData.Context, CancellationToken.None).GetAwaiter().GetResult();

    public Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
        => DispatchCommittedAsync(transaction, eventData.Context, cancellationToken);

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => _awaitingCommit.Remove(transaction);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _awaitingCommit.Remove(transaction);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        => _awaitingCommit.Remove(transaction);

    public Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _awaitingCommit.Remove(transaction);
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

                    save.Events.Add(item.Raised);
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

    private async Task CompleteAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !_saving.TryGetValue(context, out var save))
        {
            return;
        }

        _saving.Remove(context);
        var dispatch = new PendingDispatch(save.Events, save.Request);
        if (CurrentTransaction(context) is { } transaction)
        {
            _awaitingCommit.GetValue(transaction, static _ => []).Add(dispatch);
            return;
        }

        await DispatchAsync(context, [dispatch], cancellationToken);
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

    private async Task DispatchCommittedAsync(DbTransaction transaction, DbContext? context, CancellationToken cancellationToken)
    {
        if (!_awaitingCommit.TryGetValue(transaction, out var dispatches))
        {
            return;
        }

        _awaitingCommit.Remove(transaction);
        if (context is not null)
        {
            await DispatchAsync(context, dispatches, cancellationToken);
        }
    }

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

    private static async Task DispatchAsync(DbContext context, IReadOnlyList<PendingDispatch> dispatches, CancellationToken cancellationToken)
    {
        if (ApplicationServices(context) is not { } services
            || services.GetService<IServiceScopeFactory>() is not { } scopes
            || !HasHandlers(services, dispatches))
        {
            return;
        }

        foreach (var dispatch in dispatches)
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<SqlOSRequestContextAccessor>() is { } requestContext)
            {
                requestContext.Current = dispatch.Request;
            }

            foreach (var raised in dispatch.Events)
            {
                var dispatcher = Dispatchers.GetOrAdd(raised.Event.GetType(), PostCommitDispatcher.Create);
                await dispatcher.DispatchAsync(scope.ServiceProvider, raised.Event, cancellationToken);
            }
        }
    }

    private static bool HasHandlers(IServiceProvider services, IReadOnlyList<PendingDispatch> dispatches)
    {
        var registrations = services.GetService<IServiceProviderIsService>();
        return dispatches.Any(dispatch => dispatch.Events.Any(raised =>
            registrations?.IsService(typeof(ISqlOSPostCommitHandler<>).MakeGenericType(raised.Event.GetType())) ?? true));
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

    private static DbTransaction? CurrentTransaction(DbContext context)
        => context.Database.CurrentTransaction is { } transaction && context.Database.IsRelational()
            ? transaction.GetDbTransaction()
            : null;

    private readonly record struct DrainedEvent(DomainEventBuffer Source, RaisedDomainEvent Raised);

    private sealed record PendingDispatch(IReadOnlyList<RaisedDomainEvent> Events, SqlOSRequestContext Request);

    private sealed class PendingSave(List<DrainedEvent> drained, SqlOSRequestContext request)
    {
        /// <summary>The events drained before projecting, which a failed save returns.</summary>
        public List<DrainedEvent> Drained { get; } = drained;

        public SqlOSRequestContext Request { get; } = request;

        /// <summary>Every event of the save, in raise order, for the post-commit handlers.</summary>
        public List<RaisedDomainEvent> Events { get; } = [];

        public List<SqlOSAuditEvent> Rows { get; } = [];
    }

    private abstract class PostCommitDispatcher
    {
        public static PostCommitDispatcher Create(Type eventType)
            => (PostCommitDispatcher)Activator.CreateInstance(typeof(PostCommitDispatcher<>).MakeGenericType(eventType))!;

        public abstract Task DispatchAsync(IServiceProvider services, ISqlOSDomainEvent domainEvent, CancellationToken cancellationToken);
    }

    private sealed class PostCommitDispatcher<TEvent> : PostCommitDispatcher
        where TEvent : ISqlOSDomainEvent
    {
        public override async Task DispatchAsync(IServiceProvider services, ISqlOSDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            foreach (var handler in services.GetServices<ISqlOSPostCommitHandler<TEvent>>())
            {
                await handler.HandleAsync((TEvent)domainEvent, cancellationToken);
            }
        }
    }
}
