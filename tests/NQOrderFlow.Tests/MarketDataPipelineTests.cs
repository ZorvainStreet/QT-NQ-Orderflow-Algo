using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class MarketDataPipelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);   // 10:00 EDT
    private static MarketDataPipeline New() => new(new DataSettings(), new SessionClock(SessionSettings.Default()), 0.25m);
    private static RawTrade Raw(double s, decimal price, decimal size = 1, Aggressor side = Aggressor.Buy)
        => new(T0.AddSeconds(s), price, size, side);

    [Fact]
    public void Trades_FlowIntoBarsCvdAndProfile()
    {
        var p = New();
        p.OnTrade(Raw(0, 100m, 3));
        p.OnTrade(Raw(10, 100.25m, 1, Aggressor.Sell));
        var closed = p.OnTrade(Raw(60, 100m, 2));
        Assert.Equal(2m, closed!.Delta);
        Assert.Equal(4m, p.Snapshot(T0.AddSeconds(60)).SessionCvd);
        Assert.NotNull(p.Levels().Vwap);
        Assert.Single(p.Bars1m());
    }

    [Fact]
    public void Cvd_ResetsAtTradingDayRoll()
    {
        var p = New();
        p.OnTrade(new RawTrade(new DateTime(2026, 10, 29, 21, 59, 0, DateTimeKind.Utc), 100m, 5, Aggressor.Buy));  // 17:59 ET
        p.OnTrade(new RawTrade(new DateTime(2026, 10, 29, 22, 0, 0, DateTimeKind.Utc), 100m, 2, Aggressor.Sell));  // 18:00 ET
        Assert.Equal(-2m, p.Snapshot(new DateTime(2026, 10, 29, 22, 0, 1, DateTimeKind.Utc)).SessionCvd);
    }

    [Fact]
    public void Health_NeedsWarmupAndFreshTicks()
    {
        var p = New();
        p.OnTrade(Raw(0, 100m));
        Assert.False(p.Health(T0.AddSeconds(1)).IsHealthy);         // not warm
        p.MarkWarmupComplete();
        Assert.True(p.Health(T0.AddSeconds(3)).IsHealthy);
        Assert.False(p.Health(T0.AddSeconds(3.5)).IsHealthy);       // stale > 3 s
        Assert.False(New().Health(T0).IsHealthy);                   // never ticked
    }

    [Fact]
    public void Health_FlagsHighFallbackRate()
    {
        var p = New();
        p.OnTrade(Raw(0, 100m, side: Aggressor.Unknown));
        Assert.True(p.Health(T0).FallbackWarning);
    }

    [Fact]
    public void OnTimer_ClosesQuietMinute()
    {
        var p = New();
        p.OnTrade(Raw(5, 100m));
        Assert.NotNull(p.OnTimer(T0.AddSeconds(61)));
    }

    [Fact]
    public void ReplayedOlderTick_IsNotDoubleCounted()
    {
        var p = New();
        p.OnTrade(Raw(10, 100m, 4));
        p.OnTrade(Raw(9, 100m, 4));                                 // older replay → dropped
        Assert.Equal(4m, p.Snapshot(T0.AddSeconds(10)).SessionCvd);
    }
}
