namespace SqlOS.Domain;

/// <summary>
/// The root of an aggregate: a cluster of entities that is consistent after every save, changed
/// only through the root's intention-revealing methods.
/// </summary>
/// <remarks>
/// Roots compose a buffer instead of inheriting a base class, so no public API is added. A public
/// entity implements this internal interface explicitly, which keeps the buffer out of the public
/// surface:
/// <code>
/// public sealed class SqlOSUser : ISqlOSAggregate
/// {
///     private readonly DomainEventBuffer _events = new();
///     DomainEventBuffer ISqlOSAggregate.Events => _events;
/// }
/// </code>
/// </remarks>
internal interface ISqlOSAggregate
{
    /// <summary>The events this aggregate raised that no save has committed yet.</summary>
    DomainEventBuffer Events { get; }
}
