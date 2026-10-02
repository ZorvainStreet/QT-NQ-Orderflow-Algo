using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class SessionProfileTests
{
    private static readonly SessionClock Clock = new(SessionSettings.Default());
    private static SessionProfile New() => new(Clock, new DataSettings());
    private static Trade At(DateTime utc, decimal price, decimal size = 1, bool buy = true) => new(utc, price, size, buy, false);
    private static DateTime Edt(int d, int h, int m) => new DateTime(2026, 10, d, h, m, 0, DateTimeKind.Utc).AddHours(4);   // ET → UTC in EDT
    private static DateTime Est(int d, int h, int m) => new DateTime(2026, 11, d, h, m, 0, DateTimeKind.Utc).AddHours(5);   // ET → UTC in EST

    [Fact]
    public void ValueArea_ExpandsFromPocTowardLargerNeighbor()
    {
        var vol = new Dictionary<decimal, decimal> { [99m] = 10, [100m] = 50, [101m] = 30, [102m] = 10 };
        var va = ValueArea.Compute(vol, 70m)!;        // total 100, target 70: 50 → +30 (101) = 80
        Assert.Equal((100m, 101m, 100m), (va.Poc, va.Vah, va.Val));
        Assert.Null(ValueArea.Compute(new Dictionary<decimal, decimal>(), 70m));
    }

    [Fact]
    public void Vwap_AndStdDev_AreVolumeWeighted_RthOnly()
    {
        var p = New();
        p.OnTrade(At(Edt(29, 9, 0), 50m, 100));          // pre-RTH: overnight only
        p.OnTrade(At(Edt(29, 9, 30), 100m, 1));
        p.OnTrade(At(Edt(29, 9, 31), 104m, 3));
        var s = p.Snapshot();
        Assert.Equal(103m, s.Vwap);                       // (100 + 312) / 4
        Assert.Equal(103m + 2 * s.VwapStdDev!.Value, s.Band(2));
        Assert.True(Math.Abs(s.VwapStdDev.Value - 1.7320508m) < 0.0001m);   // sqrt(3)
        Assert.Equal((50m, 50m), (s.OnHigh, s.OnLow));
    }

    [Fact]
    public void OpeningRanges_FirstFiveAndFifteenMinutes_InEdtAndEst()
    {
        foreach (var et in new Func<int, int, DateTime>[] { (h, m) => Edt(29, h, m), (h, m) => Est(5, h, m) })
        {
            var p = New();
            p.OnTrade(At(et(9, 30), 100m));
            p.OnTrade(At(et(9, 34), 102m));
            p.OnTrade(At(et(9, 36), 105m));               // after OR5, inside OR15
            p.OnTrade(At(et(9, 45), 90m));                // after OR15
            var s = p.Snapshot();
            Assert.Equal((102m, 100m), (s.Or5High, s.Or5Low));
            Assert.Equal((105m, 100m), (s.Or15High, s.Or15Low));
            Assert.Equal((105m, 90m), (s.RthHigh, s.RthLow));
        }
    }

    [Fact]
    public void Roll_At1800Et_KeepsPriorRthSummary_AndResetsSession()
    {
        var p = New();
        p.OnTrade(At(Edt(28, 10, 0), 100m, 5));
        p.OnTrade(At(Edt(28, 15, 59), 110m, 1));
        p.OnTrade(At(Edt(28, 18, 0), 120m, 1));           // new session (trading date 29th), overnight
        var s = p.Snapshot();
        Assert.Equal(new DateOnly(2026, 10, 29), s.TradingDate);
        Assert.Null(s.Vwap);
        Assert.Equal((120m, 120m), (s.OnHigh, s.OnLow));
        Assert.Equal(new DateOnly(2026, 10, 28), s.Prior!.TradingDate);
        Assert.Equal((110m, 100m, 110m, 100m), (s.Prior.High, s.Prior.Low, s.Prior.Close, s.Prior.Value.Poc));
    }

    [Fact]
    public void PostRthTrades_AreIgnoredForRthAndOvernight()
    {
        var p = New();
        p.OnTrade(At(Edt(29, 16, 30), 100m));
        var s = p.Snapshot();
        Assert.Null(s.RthHigh);
        Assert.Null(s.OnHigh);
    }
}
