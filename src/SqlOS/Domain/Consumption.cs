namespace SqlOS.Domain;

/// <summary>
/// Single use (<c>ConsumedAt</c>): codes, temporary tokens, challenges and refresh tokens are used
/// once. Using one again is a broken invariant, never a silent success.
/// </summary>
/// <remarks>
/// Entities that compose this part keep <c>ConsumedAt</c> as a concurrency token, so two requests
/// that both see the item unconsumed cannot both commit the consumption.
/// </remarks>
internal readonly record struct Consumption(DateTime? ConsumedAt)
{
    public bool IsConsumed => ConsumedAt is not null;

    public Consumption Consume(DateTime now)
        => IsConsumed
            ? throw SqlOSDomainException.Of(SqlOSDomainError.AlreadyConsumed)
            : new Consumption(now);
}
