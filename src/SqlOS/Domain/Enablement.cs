namespace SqlOS.Domain;

/// <summary>
/// Whether an item is in service: <c>IsActive</c> (or <c>IsEnabled</c>) and, where the entity
/// records one, when and why it was disabled (<c>DisabledAt</c>, <c>DisabledReason</c>).
/// </summary>
/// <remarks>
/// <para>
/// An item is enabled only when it is active and has no disable record, the 7.x client rule
/// (<c>!IsActive || DisabledAt != null</c> means inactive). Inactive without a disable record is
/// how a client disabled in its code-owned definition looks; whether that state may be enabled at
/// runtime is the owning aggregate's rule.
/// </para>
/// <para>
/// Disabling is idempotent: an item that already has a disable record keeps its first time and
/// reason. Entities with only an <c>IsActive</c> column build the part from that flag and store
/// only <see cref="IsActive"/>. The default value is inactive, so an uninitialized enablement is
/// disabled.
/// </para>
/// </remarks>
internal readonly record struct Enablement(bool IsActive, DateTime? DisabledAt = null, string? DisabledReason = null)
{
    public static Enablement Enabled { get; } = new(IsActive: true);

    public bool IsEnabled => IsActive && DisabledAt is null;

    public Enablement Disable(string reason, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return !IsActive && DisabledAt is not null
            ? this
            : new Enablement(false, now, reason);
    }

    public Enablement Enable() => Enabled;

    public void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.Disabled);
        }
    }
}
