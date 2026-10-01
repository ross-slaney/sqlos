using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlOS.Domain;

namespace SqlOS.Database;

/// <summary>
/// The domain events of one save that have <see cref="ISqlOSPostCommitHandler{TEvent}"/>s, with
/// what running those handlers needs once the save has committed.
/// </summary>
/// <remarks>
/// <para>
/// Only events with a registered handler are kept, so a save whose events have none dispatches
/// nothing and leaves nothing waiting for its transaction.
/// </para>
/// <para>
/// <see cref="RunAsync"/> runs each event's handlers, in the order the events were raised, in a
/// dependency-injection scope of its own that carries the saving scope's request context. The
/// change is committed by then, so a handler that throws is logged and the rest still run: failing
/// the caller would report a committed change as failed and invite a retry of committed work,
/// and inside an EF or ambient commit the exception would escape mid-commit. Work that must fail
/// the request belongs before the save. Handlers are not canceled by the caller's token for the
/// same reason.
/// </para>
/// </remarks>
internal sealed class SqlOSPostCommitDispatch
{
    private static readonly ConcurrentDictionary<Type, EventDispatcher> Dispatchers = new();

    private readonly IServiceScopeFactory _scopes;
    private readonly IReadOnlyList<ISqlOSDomainEvent> _events;
    private readonly SqlOSRequestContext _request;

    private SqlOSPostCommitDispatch(IServiceScopeFactory scopes, IReadOnlyList<ISqlOSDomainEvent> events, SqlOSRequestContext request)
    {
        _scopes = scopes;
        _events = events;
        _request = request;
    }

    /// <summary>
    /// The events that have a handler, in raise order, or <see langword="null"/> when none has or
    /// there are no <paramref name="services"/> (a context built without dependency injection).
    /// </summary>
    public static SqlOSPostCommitDispatch? For(
        IServiceProvider? services,
        IEnumerable<ISqlOSDomainEvent> events,
        SqlOSRequestContext request)
    {
        if (services?.GetService<IServiceScopeFactory>() is not { } scopes)
        {
            return null;
        }

        // A container that cannot answer is asked for the handlers at dispatch time instead.
        var registrations = services.GetService<IServiceProviderIsService>();
        var handled = events
            .Where(domainEvent => registrations?.IsService(DispatcherFor(domainEvent).HandlerType) ?? true)
            .ToList();
        return handled.Count == 0 ? null : new SqlOSPostCommitDispatch(scopes, handled, request);
    }

    public async Task RunAsync()
    {
        foreach (var domainEvent in _events)
        {
            await using var scope = _scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<SqlOSRequestContextAccessor>() is { } requestContext)
            {
                requestContext.Current = _request;
            }

            await DispatcherFor(domainEvent).DispatchAsync(scope.ServiceProvider, domainEvent);
        }
    }

    private static EventDispatcher DispatcherFor(ISqlOSDomainEvent domainEvent)
        => Dispatchers.GetOrAdd(
            domainEvent.GetType(),
            static eventType => (EventDispatcher)Activator.CreateInstance(typeof(EventDispatcher<>).MakeGenericType(eventType))!);

    private abstract class EventDispatcher
    {
        public abstract Type HandlerType { get; }

        public abstract Task DispatchAsync(IServiceProvider services, ISqlOSDomainEvent domainEvent);
    }

    private sealed class EventDispatcher<TEvent> : EventDispatcher
        where TEvent : ISqlOSDomainEvent
    {
        public override Type HandlerType => typeof(ISqlOSPostCommitHandler<TEvent>);

        public override async Task DispatchAsync(IServiceProvider services, ISqlOSDomainEvent domainEvent)
        {
            IEnumerable<ISqlOSPostCommitHandler<TEvent>> handlers;
            try
            {
                handlers = services.GetServices<ISqlOSPostCommitHandler<TEvent>>().ToList();
            }
            catch (Exception exception)
            {
                LogFailure(services, exception, typeof(ISqlOSPostCommitHandler<TEvent>));
                return;
            }

            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync((TEvent)domainEvent, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    LogFailure(services, exception, handler.GetType());
                }
            }
        }

        private static void LogFailure(IServiceProvider services, Exception exception, Type handler)
            => services.GetService<ILogger<SqlOSPostCommitDispatch>>()?.LogError(
                exception,
                "Post-commit handler {Handler} failed for domain event {EventType}. The change it reacts to is committed.",
                handler.FullName,
                typeof(TEvent).FullName);
    }
}
