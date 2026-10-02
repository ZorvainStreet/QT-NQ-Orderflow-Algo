using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;

namespace QT_MNQ_Orderflow_Algo.OrderFlow;

public enum FlowContext { None, InitiativeBuy, InitiativeSell, ResponsiveBuy, ResponsiveSell }

public sealed record FeatureInputs(
    DateTime Utc,
    IReadOnlyList<FootprintBar> Bars1m,
    IReadOnlyList<Bucket> Buckets,
    int TapeMidCount,
    decimal SessionCvd,
    decimal? LastPrice,
    ValueArea? Developing,
    DataSettings Settings,
    decimal TickSize);

/// <summary>Immutable order-flow state at one instant. Journaled with every proposal from Phase 4 on.</summary>
public sealed record OrderFlowSnapshot(
    DateTime Utc,
    decimal LastBarDelta,
    decimal NormalizedBarDelta,
    decimal SessionCvd,
    decimal CvdSlope5m,
    decimal CvdSlope15m,
    int StackedBuyLevels,
    int StackedSellLevels,
    bool UnfinishedHigh,
    bool UnfinishedLow,
    int DeltaFlip,
    double VelocityZ,
    bool VelocitySpike,
    FlowContext Context);

public sealed record AbsorptionResult(bool Detected, decimal Score, decimal AggressiveVolume, decimal DominancePercent)
{
    public static AbsorptionResult None { get; } = new(false, 0m, 0m, 0m);
}

public static class OrderFlowFeatures
{
    private const int StackLookbackBars = 2;
    private const int ContextBars = 5;
    private const int SlopeShortBars = 5;
    private const int SlopeLongBars = 15;

    public static OrderFlowSnapshot Compute(FeatureInputs x)
    {
        var s = x.Settings;
        var bars = x.Bars1m;
        var last = bars.Count > 0 ? bars[^1] : null;
        var medianBarVolume = Stats.Median(Tail(bars, s.MedianBarLookback).Select(b => b.TotalVolume).ToList());
        var lastDelta = last?.Delta ?? 0m;
        var normalized = medianBarVolume > 0 ? Math.Clamp(lastDelta / medianBarVolume, -1m, 1m) : 0m;
        var recent = Tail(bars, StackLookbackBars);
        var (velocityZ, spike) = Velocity(x);
        return new OrderFlowSnapshot(
            x.Utc, lastDelta, normalized, x.SessionCvd,
            MeanDelta(bars, SlopeShortBars), MeanDelta(bars, SlopeLongBars),
            recent.Count == 0 ? 0 : recent.Max(b => FootprintBar.LongestStack(b.BuyImbalances(s.ImbalanceRatio, s.ImbalanceMinVolume), x.TickSize)),
            recent.Count == 0 ? 0 : recent.Max(b => FootprintBar.LongestStack(b.SellImbalances(s.ImbalanceRatio, s.ImbalanceMinVolume), x.TickSize)),
            last?.UnfinishedHigh ?? false, last?.UnfinishedLow ?? false,
            DeltaFlip(x.Buckets, s), velocityZ, spike, Context(x));
    }

    public static AbsorptionResult Absorption(IReadOnlyList<Trade> midWindow, decimal medianBucketVolume, decimal level,
        bool bullish, DataSettings s, decimal tickSize)
    {
        if (midWindow.Count == 0 || medianBucketVolume <= 0) return AbsorptionResult.None;
        var buy = midWindow.Where(t => t.IsBuy).Sum(t => t.Size);
        var sell = midWindow.Where(t => !t.IsBuy).Sum(t => t.Size);
        if (buy + sell <= 0m) return AbsorptionResult.None;
        var aggressive = bullish ? sell : buy;
        var dominance = aggressive / (buy + sell) * 100m;
        var tolerance = s.AbsorptionMaxProgressTicks * tickSize;
        var low = midWindow.Min(t => t.Price);
        var high = midWindow.Max(t => t.Price);
        var touchedAndHeld = bullish
            ? low <= level + tolerance && low >= level - tolerance
            : high >= level - tolerance && high <= level + tolerance;
        var required = s.AbsorptionVolMultiple * medianBucketVolume;
        var detected = touchedAndHeld && aggressive >= required && dominance >= s.AbsorptionDominancePercent;
        return new AbsorptionResult(detected, Math.Min(1m, aggressive / required), aggressive, dominance);
    }

    private static int DeltaFlip(IReadOnlyList<Bucket> buckets, DataSettings s)
    {
        if (buckets.Count < 2) return 0;
        var current = buckets[^1];
        var previous = buckets.Take(buckets.Count - 1).LastOrDefault(b => b.Delta != 0);
        if (previous is null || Math.Sign(current.Delta) != -Math.Sign(previous.Delta)) return 0;
        var median = Stats.Median(buckets.Select(b => b.Volume).ToList());
        return median > 0 && Math.Abs(current.Delta) >= s.DeltaFlipMinMultiple * median ? Math.Sign(current.Delta) : 0;
    }

    private static (double Z, bool Spike) Velocity(FeatureInputs x)
    {
        var (mean, sd) = Stats.MeanStd(x.Buckets.Select(b => (double)b.Trades).ToList());
        var z = sd > 0 ? (x.TapeMidCount - mean) / sd : 0;
        return (z, z > x.Settings.VelocityZLimit);
    }

    private static FlowContext Context(FeatureInputs x)
    {
        var window = Tail(x.Bars1m, ContextBars);
        if (window.Count < ContextBars || x.LastPrice is not { } price) return FlowContext.None;
        var change = price - window[0].Open;
        var delta = window.Sum(b => b.Delta);
        if (change == 0 || delta == 0) return FlowContext.None;
        if (Math.Sign(change) != Math.Sign(delta)) return delta > 0 ? FlowContext.ResponsiveBuy : FlowContext.ResponsiveSell;
        if (x.Developing is not { } va) return FlowContext.None;
        if (change > 0 && price > va.Vah) return FlowContext.InitiativeBuy;
        if (change < 0 && price < va.Val) return FlowContext.InitiativeSell;
        return FlowContext.None;
    }

    private static decimal MeanDelta(IReadOnlyList<FootprintBar> bars, int n)
    {
        var tail = Tail(bars, n);
        return tail.Count == 0 ? 0m : tail.Sum(b => b.Delta) / tail.Count;
    }

    private static IReadOnlyList<T> Tail<T>(IReadOnlyList<T> items, int n) =>
        items.Skip(Math.Max(0, items.Count - n)).ToList();
}
