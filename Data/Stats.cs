namespace QT_MNQ_Orderflow_Algo.Data;

public static class Stats
{
    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    public static (double Mean, double StdDev) MeanStd(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 0);
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (mean, Math.Sqrt(variance));
    }
}
