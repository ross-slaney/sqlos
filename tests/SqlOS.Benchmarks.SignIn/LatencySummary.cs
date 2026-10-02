namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// Order statistics of a latency sample, in milliseconds. Percentiles interpolate linearly between
/// the closest ranks (Hyndman and Fan's type 7, as numpy and Excel's PERCENTILE.INC compute them).
/// A far outlier lies above the third quartile by more than three interquartile ranges (Tukey's
/// outer fence); they are counted, not removed, so the mean and the tail include them.
/// </summary>
internal sealed record LatencySummary(
    int Count,
    double Mean,
    double StandardDeviation,
    double Min,
    double P50,
    double P90,
    double P95,
    double P99,
    double Max,
    double FarOutlierFence,
    int FarOutliers)
{
    public static LatencySummary Of(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0)
        {
            throw new ArgumentException("A latency summary needs at least one sample.", nameof(samples));
        }

        var sorted = samples.Order().ToArray();
        var mean = sorted.Average();
        var variance = sorted.Length > 1
            ? sorted.Sum(sample => (sample - mean) * (sample - mean)) / (sorted.Length - 1)
            : 0;
        var q1 = Percentile(sorted, 0.25);
        var q3 = Percentile(sorted, 0.75);
        var fence = q3 + 3 * (q3 - q1);
        return new LatencySummary(
            sorted.Length,
            mean,
            Math.Sqrt(variance),
            sorted[0],
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            fence,
            sorted.Count(sample => sample > fence));
    }

    /// <summary>The <paramref name="quantile"/> of an ascending sample (type 7 interpolation).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double quantile)
    {
        var rank = quantile * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}
