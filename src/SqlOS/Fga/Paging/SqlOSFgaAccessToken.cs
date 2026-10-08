using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace SqlOS.Fga.Paging;

/// <summary>
/// Who a filter from <c>BuildFilterAsync</c> is for: the caller's resolved subjects and the permission. A
/// page that uses the filter runs SqlOS's walk with these, instead of the filter's predicate.
/// </summary>
internal sealed record SqlOSFgaAccessToken(IReadOnlyList<string> SubjectIds, string SubjectIdsJson, string PermissionId, string PermissionKey);

/// <summary>
/// The filters <c>BuildFilterAsync</c> returned, by the expression object itself, so a query that composes
/// the filter unchanged is recognized as an authorized query. The table is weak: a filter is forgotten when
/// the application lets go of it.
/// </summary>
internal static class SqlOSFgaFilterRegistry
{
    private static readonly ConditionalWeakTable<LambdaExpression, SqlOSFgaAccessToken> Tokens = new();

    public static void Register(LambdaExpression filter, SqlOSFgaAccessToken token) => Tokens.AddOrUpdate(filter, token);

    public static SqlOSFgaAccessToken? Find(LambdaExpression filter) => Tokens.TryGetValue(filter, out var token) ? token : null;
}

/// <summary>
/// What the pages SqlOS walks cost, for the tests and the benchmarks. <see cref="Collect"/> starts collecting
/// in the current async flow; <see cref="LastCounters"/> is then the counters of the last page walked in it,
/// or null when no page was walked since (a query that ran as a plain query leaves it null).
/// </summary>
internal static class SqlOSFgaPageDiagnostics
{
    private static readonly AsyncLocal<Box?> Current = new();

    public static SqlOSFgaPageCounters? LastCounters
    {
        get => Current.Value?.Counters;
        set
        {
            if (Current.Value is { } box)
            {
                box.Counters = value;
            }
        }
    }

    /// <summary>Starts (or restarts) collecting: the counters of pages walked from here on in this async flow are kept.</summary>
    public static void Collect() => Current.Value = new Box();

    /// <summary>A holder the walk writes into: a value set inside an awaited call does not flow back to the caller, a reference's contents do.</summary>
    private sealed class Box
    {
        public SqlOSFgaPageCounters? Counters { get; set; }
    }
}
