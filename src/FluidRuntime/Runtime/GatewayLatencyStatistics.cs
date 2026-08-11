namespace FluidRuntime.Runtime;

public sealed record TailLatencyDistribution(
    int Count,
    double Minimum,
    double P50,
    double P95,
    double P99,
    double Maximum,
    double Mean);

public sealed record PairedTailLatencySummary(
    TailLatencyDistribution Baseline,
    TailLatencyDistribution Optimized,
    TailLatencyDistribution Delta,
    int OptimizedLowerCount,
    int BaselineLowerCount,
    int TieCount);

internal static class GatewayLatencyStatistics
{
    public static PairedTailLatencySummary SummarizePairs(
        IEnumerable<long> baselineValues,
        IEnumerable<long> optimizedValues)
    {
        var baseline = baselineValues.Select(value => (double)value).ToArray();
        var optimized = optimizedValues.Select(value => (double)value).ToArray();
        if (baseline.Length == 0 || baseline.Length != optimized.Length)
        {
            throw new ArgumentException(
                "Paired latency samples must be non-empty and have equal counts.");
        }

        var deltas = baseline.Select((value, index) => optimized[index] - value)
            .ToArray();
        return new PairedTailLatencySummary(
            Distribution(baseline),
            Distribution(optimized),
            Distribution(deltas),
            OptimizedLowerCount: deltas.Count(value => value < 0),
            BaselineLowerCount: deltas.Count(value => value > 0),
            TieCount: deltas.Count(value => value == 0));
    }

    public static TailLatencyDistribution Distribution(
        IEnumerable<long> values) =>
        Distribution(values.Select(value => (double)value));

    private static TailLatencyDistribution Distribution(
        IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return new TailLatencyDistribution(0, 0, 0, 0, 0, 0, 0);
        }

        return new TailLatencyDistribution(
            ordered.Length,
            Round(ordered[0]),
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            Round(ordered[^1]),
            Round(ordered.Average()));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var rank = percentile * (ordered.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return Round(
            ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower));
    }

    private static double Round(double value) => Math.Round(value, 3);
}
