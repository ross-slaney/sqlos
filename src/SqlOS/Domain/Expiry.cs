namespace SqlOS.Domain;

/// <summary>
/// The end of an item's lifetime (<c>ExpiresAt</c>). The item is expired from the expiry instant
/// on: at <c>now == ExpiresAt</c> it is already expired.
/// </summary>
/// <remarks>
/// This is the rule most 7.x checks apply (<c>ExpiresAt &lt;= now</c> means expired). A few 7.x
/// queries still accept a token at the exact instant (<c>ExpiresAt &gt;= now</c>, for example
/// <c>SqlOSCryptoService.FindTemporaryTokenAsync</c>); the one-tick difference is not observable,
/// and moving those checks onto this part removes it. The default value expires at
/// <see cref="DateTime.MinValue"/>, so an uninitialized expiry is always expired.
/// </remarks>
internal readonly record struct Expiry(DateTime ExpiresAt)
{
    /// <summary>An expiry <paramref name="lifetime"/> after <paramref name="now"/>.</summary>
    public static Expiry After(DateTime now, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        return new Expiry(now.Add(lifetime));
    }

    public bool IsExpired(DateTime now) => now >= ExpiresAt;

    public void EnsureActive(DateTime now)
    {
        if (IsExpired(now))
        {
            throw SqlOSDomainException.Of(SqlOSDomainError.Expired);
        }
    }
}
