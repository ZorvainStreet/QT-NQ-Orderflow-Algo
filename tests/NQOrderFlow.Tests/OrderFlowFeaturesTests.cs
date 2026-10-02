using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using QT_MNQ_Orderflow_Algo.OrderFlow;
using Xunit;

public sealed class OrderFlowFeaturesTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DataSettings S = new();

    private static FootprintBar Bar(int minute, decimal open, decimal close, decimal buy, decimal sell)
    {
        var bar = new FootprintBar(T0.AddMinutes(minute), 0.25m);
        bar.Add(new Trade(T0.AddMinutes(minute), open, buy, true, false));
        bar.Add(new Trade(T0.AddMinutes(minute).AddSeconds(30), close, sell, false, false));
        return bar;
    }

    private static FeatureInputs Inputs(IReadOnlyList<FootprintBar>? bars = null, IReadOnlyList<Bucket>? buckets = null,
        int tapeCount = 0, decimal? last = null, ValueArea? va = null) =>
        new(T0, bars ?? Array.Empty<FootprintBar>(), buckets ?? Array.Empty<Bucket>(), tapeCount, 0m, last, va, S, 0.25m);

    [Fact]
    public void Empty_IsNeutral()
    {
        var s = OrderFlowFeatures.Compute(Inputs());
        Assert.Equal((0m, 0m, 0, 0, 0.0), (s.LastBarDelta, s.NormalizedBarDelta, s.StackedBuyLevels, s.DeltaFlip, s.VelocityZ));
        Assert.Equal(FlowContext.None, s.Context);
    }

    [Fact]
    public void NormalizedDelta_IsClampedByMedianBarVolume()
    {
        var bars = new[] { Bar(0, 100m, 100m, 10, 10), Bar(1, 100m, 100m, 40, 0) };   // volumes 20, 40 → median 30
        var s = OrderFlowFeatures.Compute(Inputs(bars));
        Assert.Equal(40m, s.LastBarDelta);
        Assert.Equal(1m, s.NormalizedBarDelta);                                     // 40/30 clamped
    }

    [Fact]
    public void CvdSlope_IsMeanBarDelta()
    {
        var bars = Enumerable.Range(0, 5).Select(m => Bar(m, 100m, 100m, 10, 4)).ToArray();   // delta +6 each
        Assert.Equal(6m, OrderFlowFeatures.Compute(Inputs(bars)).CvdSlope5m);
    }

    [Fact]
    public void DeltaFlip_RequiresOppositeSignAndSize()
    {
        var buckets = new[] { new Bucket(T0, 10m, -6m, 5), new Bucket(T0.AddSeconds(20), 10m, 6m, 5) };   // median 10, min 5
        Assert.Equal(1, OrderFlowFeatures.Compute(Inputs(buckets: buckets)).DeltaFlip);
        var small = new[] { buckets[0], new Bucket(T0.AddSeconds(20), 10m, 4m, 5) };
        Assert.Equal(0, OrderFlowFeatures.Compute(Inputs(buckets: small)).DeltaFlip);
    }

    [Fact]
    public void VelocityZ_ComparesCurrentWindowToBucketBaseline()
    {
        var buckets = new[] { new Bucket(T0, 1m, 0m, 4), new Bucket(T0.AddSeconds(20), 1m, 0m, 6) };    // mean 5, sd 1
        var s = OrderFlowFeatures.Compute(Inputs(buckets: buckets, tapeCount: 9));
        Assert.Equal(4.0, s.VelocityZ, 6);
        Assert.True(s.VelocitySpike);
    }

    [Fact]
    public void Context_InitiativeBuy_AboveValueWithAgreeingDelta()
    {
        var bars = Enumerable.Range(0, 5).Select(m => Bar(m, 100m + m, 100m + m, 10, 2)).ToArray();
        var s = OrderFlowFeatures.Compute(Inputs(bars, last: 105m, va: new ValueArea(100m, 102m, 98m)));
        Assert.Equal(FlowContext.InitiativeBuy, s.Context);
    }

    [Fact]
    public void Context_Responsive_WhenDeltaOpposesMove()
    {
        var bars = Enumerable.Range(0, 5).Select(m => Bar(m, 100m + m, 100m + m, 2, 10)).ToArray();
        var s = OrderFlowFeatures.Compute(Inputs(bars, last: 105m, va: new ValueArea(100m, 102m, 98m)));
        Assert.Equal(FlowContext.ResponsiveSell, s.Context);
    }

    [Fact]
    public void Absorption_Bullish_HeavySellingHeldAtLevel()
    {
        var trades = new[]
        {
            new Trade(T0, 100m, 18m, false, false), new Trade(T0.AddSeconds(5), 99.75m, 5m, false, false),
            new Trade(T0.AddSeconds(9), 100m, 4m, true, false),
        };
        var r = OrderFlowFeatures.Absorption(trades, 10m, 100m, bullish: true, S, 0.25m);   // sell 23 ≥ 20; 85%; low 99.75 within 3 ticks
        Assert.True(r.Detected);
        Assert.Equal(1m, r.Score);
        Assert.False(OrderFlowFeatures.Absorption(trades, 10m, 102m, bullish: true, S, 0.25m).Detected);  // never traded near 102
    }

    [Fact]
    public void Absorption_FailsWhenPriceBrokeThrough()
    {
        var trades = new[] { new Trade(T0, 100m, 15m, false, false), new Trade(T0.AddSeconds(5), 99m, 10m, false, false) };
        Assert.False(OrderFlowFeatures.Absorption(trades, 10m, 100m, bullish: true, S, 0.25m).Detected);   // 4 ticks through
    }

    [Fact]
    public void Absorption_ZeroSizeTrades_ReturnsNone()
    {
        var trades = new[] { new Trade(T0, 100m, 0m, false, false), new Trade(T0.AddSeconds(1), 100m, 0m, true, false) };
        Assert.Equal(AbsorptionResult.None, OrderFlowFeatures.Absorption(trades, 10m, 100m, bullish: true, S, 0.25m));
    }
}
