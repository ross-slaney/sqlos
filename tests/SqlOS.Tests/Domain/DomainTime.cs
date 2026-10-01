namespace SqlOS.Tests.Domain;

/// <summary>Fixed instants for domain tests. Domain code takes <c>now</c>; tests never read a clock.</summary>
internal static class DomainTime
{
    public static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public static DateTime Tick(this DateTime value, int ticks) => value.AddTicks(ticks);
}
