using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class LucidRiskGuardTests
{
    private static readonly DateTime ThuAm = new(2026, 10, 29, 14, 15, 0, DateTimeKind.Utc); // 10:15 ET
    private static readonly GuardContext Flat = new(25000m, 0, null, true, true);

    private static LucidRiskGuard Guard()
    {
        var clock = new SessionClock(SessionSettings.Default());
        return LucidRiskGuard.ForFlexEvaluation(new AccountRules(), new RiskSettings(), clock,
            NewsBlackout.Load(null, clock, 2, 3, new NullLogSink()), new NullLogSink());
    }

    private static EntryRequest Req(DateTime t, Side side = Side.Buy, int qty = 1, decimal stop = 8m) => new(t, side, qty, stop, 20m, false);
    private static DateTime Day(int d) => new(2026, 10, d, 14, 15, 0, DateTimeKind.Utc);

    [Fact] public void Allows_CleanEntryInWindow() => Assert.True(Guard().CanEnter(Req(ThuAm), Flat).Allowed);
    [Fact] public void Rejects_Friday() => Assert.False(Guard().CanEnter(Req(ThuAm.AddDays(1)), Flat).Allowed);
    [Fact] public void Rejects_OutsideWindow() => Assert.False(Guard().CanEnter(Req(ThuAm.AddHours(2)), Flat).Allowed);
    [Fact] public void Rejects_NewsBlackout() => Assert.False(Guard().CanEnter(Req(ThuAm.AddMinutes(-16)), Flat).Allowed);
    [Fact] public void Rejects_QuantityNotOne() => Assert.False(Guard().CanEnter(Req(ThuAm, qty: 2), Flat).Allowed);
    [Fact] public void Rejects_DataUnhealthy() => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { DataHealthy = false }).Allowed);
    [Fact] public void Rejects_Disconnected() => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { Connected = false }).Allowed);
    [Fact] public void Rejects_WideStop() => Assert.False(Guard().CanEnter(Req(ThuAm, stop: 15m), Flat).Allowed);
    [Fact] public void Rejects_LowHeadroom() => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { Equity = 24300m }).Allowed);

    [Fact]
    public void Rejects_Hedge()
    {
        var d = Guard().CanEnter(Req(ThuAm, Side.Sell), Flat with { OpenPositionQty = 1, OpenPositionSide = Side.Buy });
        Assert.False(d.Allowed);
        Assert.Contains("hedge", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_SecondPositionSameSide()
        => Assert.False(Guard().CanEnter(Req(ThuAm), Flat with { OpenPositionQty = 1, OpenPositionSide = Side.Buy }).Allowed);

    [Fact]
    public void Halts_WhenMicroscalpShareOver35Percent()
    {
        var g = Guard();
        g.OnTradeClosed(ThuAm.AddDays(-1), 100m, 3);
        Assert.False(g.CanEnter(Req(ThuAm), Flat with { Equity = 25100m }).Allowed);
    }

    [Fact]
    public void TargetReached_RequiresManualReenable()
    {
        var g = Guard();
        g.OnTradeClosed(Day(26), 445m, 60);
        g.OnTradeClosed(Day(27), 445m, 60);
        g.OnTradeClosed(Day(28), 400m, 60);
        Assert.True(g.ManualReenableRequired);
        Assert.False(g.CanEnter(Req(ThuAm), Flat with { Equity = 26290m }).Allowed);
    }

    [Fact]
    public void NewTradingDate_ResetsDailyState_KeepsEval()
    {
        var g = Guard();
        g.OnTradeClosed(Day(28), -360m, 60);
        Assert.True(g.DailyHaltActive);
        g.CanEnter(Req(ThuAm), Flat with { Equity = 24640m });   // rolls to Thursday
        Assert.False(g.DailyHaltActive);
        Assert.Equal(-360m, g.Eval.TotalProfit);
    }

    [Fact]
    public void ShouldFlatten_At1555Et()
        => Assert.True(Guard().ShouldFlattenNow(new DateTime(2026, 10, 29, 19, 55, 0, DateTimeKind.Utc)));
}
