namespace SqlOS.Domain;

/// <summary>
/// When something is in effect: an FGA grant between <c>EffectiveFrom</c> and <c>EffectiveTo</c>,
/// or a service account until its <c>ExpiresAt</c>. A missing bound is open.
/// </summary>
/// <remarks>
/// <para>
/// The two 7.x rules differ at the end instant, and the window keeps both exactly:
/// </para>
/// <list type="bullet">
/// <item><see cref="Between"/> (FGA grants) includes both bounds, as the access check does
/// (<c>EffectiveFrom &lt;= now</c> and <c>EffectiveTo &gt;= now</c>).</item>
/// <item><see cref="Until"/> (service accounts) excludes its end, as client authentication does
/// (<c>ExpiresAt &gt; now</c>).</item>
/// </list>
/// <para>
/// A window whose start is after its end is accepted, as 7.x accepts such a grant, and is never in
/// effect. The default value is <see cref="Always"/>.
/// </para>
/// </remarks>
internal readonly record struct TimeWindow
{
    private TimeWindow(DateTime? effectiveFrom, DateTime? effectiveTo, bool isEndExclusive)
    {
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        IsEndExclusive = isEndExclusive;
    }

    /// <summary>The first instant in effect, or <see langword="null"/> for no start.</summary>
    public DateTime? EffectiveFrom { get; }

    /// <summary>The end of the window, or <see langword="null"/> for no end.</summary>
    public DateTime? EffectiveTo { get; }

    /// <summary>True when <see cref="EffectiveTo"/> itself is no longer in effect.</summary>
    public bool IsEndExclusive { get; }

    public static TimeWindow Always => default;

    /// <summary>In effect from <paramref name="effectiveFrom"/> through <paramref name="effectiveTo"/>, both included.</summary>
    public static TimeWindow Between(DateTime? effectiveFrom, DateTime? effectiveTo)
        => new(effectiveFrom, effectiveTo, isEndExclusive: false);

    /// <summary>In effect until, and not at, <paramref name="expiresAt"/>.</summary>
    public static TimeWindow Until(DateTime? expiresAt)
        => new(null, expiresAt, isEndExclusive: true);

    public bool Contains(DateTime now)
        => (EffectiveFrom is not { } from || from <= now)
            && (EffectiveTo is not { } to || (IsEndExclusive ? now < to : now <= to));
}
