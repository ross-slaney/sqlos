using System.Collections.Frozen;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;

namespace SqlOS.AuditLogs;

/// <summary>
/// Maps domain events to the <see cref="SqlOSAuditEvent"/> rows SqlOS writes for them, in the same
/// save, and therefore the same transaction, as the change the event describes.
/// </summary>
/// <remarks>
/// <para>
/// Every event type is registered once: <see cref="SqlOSAuditProjectionBuilder.Audit{TEvent}"/>
/// names the builder of its row, and <see cref="SqlOSAuditProjectionBuilder.Unaudited{TEvent}"/>
/// records that the action wrote no audit row in 7.2.1. Projecting an unregistered event fails the
/// save, and <c>AuditProjectionTests</c> fails when an event type in SqlOS is not registered, so an
/// audit row cannot silently disappear.
/// </para>
/// <para>
/// A builder reproduces the 7.2.1 row for its action exactly: event type, source, actor, targets,
/// context and metadata shape, usually through <see cref="SqlOSAuditRows"/>. Layers 2 to 4 of the
/// 8.0.0 refactor register only actions 7.2.1 audits, moving one audit call site at a time; #415
/// adds the missing ones in layer 5. No event is registered yet.
/// </para>
/// </remarks>
internal sealed class SqlOSAuditProjection
{
    private readonly FrozenDictionary<Type, Func<ISqlOSDomainEvent, SqlOSAuditProjectionContext, SqlOSAuditEvent?>> _projections;

    internal SqlOSAuditProjection(IReadOnlyDictionary<Type, Func<ISqlOSDomainEvent, SqlOSAuditProjectionContext, SqlOSAuditEvent?>> projections)
        => _projections = projections.ToFrozenDictionary();

    /// <summary>SqlOS's projection, registered once in dependency injection.</summary>
    public static SqlOSAuditProjection Default { get; } = new SqlOSAuditProjectionBuilder().Build();

    /// <summary>The registered event types, audited or not.</summary>
    public IEnumerable<Type> EventTypes => _projections.Keys;

    public bool Handles(Type eventType) => _projections.ContainsKey(eventType);

    /// <summary>
    /// The audit row for <paramref name="domainEvent"/>, or <see langword="null"/> for an event
    /// registered as unaudited.
    /// </summary>
    public SqlOSAuditEvent? Project(ISqlOSDomainEvent domainEvent, SqlOSAuditProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        return _projections.TryGetValue(domainEvent.GetType(), out var project)
            ? project(domainEvent, context)
            : throw new InvalidOperationException(
                $"No audit projection is registered for domain event '{domainEvent.GetType().FullName}'. Register it with SqlOSAuditProjectionBuilder.Audit or Unaudited.");
    }
}

/// <summary>Registers the audit row, or the absence of one, for each domain event type.</summary>
internal sealed class SqlOSAuditProjectionBuilder
{
    private readonly Dictionary<Type, Func<ISqlOSDomainEvent, SqlOSAuditProjectionContext, SqlOSAuditEvent?>> _projections = [];

    /// <summary>Events of <typeparamref name="TEvent"/> write the row <paramref name="build"/> returns.</summary>
    public SqlOSAuditProjectionBuilder Audit<TEvent>(Func<TEvent, SqlOSAuditProjectionContext, SqlOSAuditEvent> build)
        where TEvent : ISqlOSDomainEvent
    {
        ArgumentNullException.ThrowIfNull(build);
        return Register(typeof(TEvent), (domainEvent, context) => build((TEvent)domainEvent, context)
            ?? throw new InvalidOperationException($"The audit projection for '{typeof(TEvent).FullName}' returned no row."));
    }

    /// <summary>Events of <typeparamref name="TEvent"/> write no audit row, as their action did not in 7.2.1.</summary>
    public SqlOSAuditProjectionBuilder Unaudited<TEvent>()
        where TEvent : ISqlOSDomainEvent
        => Register(typeof(TEvent), static (_, _) => null);

    public SqlOSAuditProjection Build() => new(_projections);

    private SqlOSAuditProjectionBuilder Register(Type eventType, Func<ISqlOSDomainEvent, SqlOSAuditProjectionContext, SqlOSAuditEvent?> project)
    {
        if (!_projections.TryAdd(eventType, project))
        {
            throw new ArgumentException($"Domain event '{eventType.FullName}' already has an audit projection.", nameof(eventType));
        }

        return this;
    }
}

/// <summary>
/// What a projection may use besides the event: the save's clock reading for this row and the
/// request the unit of work serves.
/// </summary>
/// <param name="Now">
/// When the row is ingested. Rows of one save get strictly increasing instants, a microsecond apart
/// in the order their events were raised, so the audit log keeps that order on both providers.
/// </param>
/// <param name="Request">The request context of the saving scope.</param>
internal sealed record SqlOSAuditProjectionContext(DateTime Now, SqlOSRequestContext Request);
