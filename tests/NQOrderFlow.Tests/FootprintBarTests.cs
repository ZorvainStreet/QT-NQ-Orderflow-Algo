using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class FootprintBarTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static Trade B(decimal p, decimal size) => new(T0, p, size, true, false);
    private static Trade S(decimal p, decimal size) => new(T0, p, size, false, false);

    private static FootprintBar Bar(params Trade[] trades)
    {
        var bar = new FootprintBar(T0, 0.25m);
        foreach (var t in trades) bar.Add(t);
        return bar;
    }

    [Fact]
    public void Ohlc_Volume_Delta_AndRunningExtremes()
    {
        var bar = Bar(B(100m, 5), S(99.75m, 8), B(100.25m, 2));
        Assert.Equal((100m, 100.25m, 99.75m, 100.25m), (bar.Open, bar.High, bar.Low, bar.Close));
        Assert.Equal(15m, bar.TotalVolume);
        Assert.Equal(-1m, bar.Delta);
        Assert.Equal(5m, bar.MaxDelta);
        Assert.Equal(-3m, bar.MinDelta);
        Assert.Equal(3, bar.TradeCount);
    }

    [Fact]
    public void BidAsk_ArePerPrice()
    {
        var bar = Bar(B(100m, 5), S(100m, 3), B(100m, 1));
        Assert.Equal(6m, bar.AskAt(100m));
        Assert.Equal(3m, bar.BidAt(100m));
        Assert.Equal(0m, bar.AskAt(101m));
    }

    [Fact]
    public void Poc_IsLargestTotal_TieGoesToLowerPrice()
    {
        Assert.Equal(100m, Bar(B(100m, 5), S(100.25m, 3)).Poc);
        Assert.Equal(100m, Bar(B(100m, 4), S(100.25m, 4)).Poc);
        Assert.Null(new FootprintBar(T0, 0.25m).Poc);
    }

    [Fact]
    public void BuyImbalance_IsDiagonal_AskVsBidOneTickBelow()
    {
        // ask 36 @100.25 vs bid 12 @100.00 → 3.0x → imbalance; ask 30 @100.50 vs bid 11 @100.25 → 2.7x → none
        var bar = Bar(S(100m, 12), B(100.25m, 36), S(100.25m, 11), B(100.5m, 30));
        Assert.Equal(new[] { 100.25m }, bar.BuyImbalances(3.0m, 12m));
    }

    [Fact]
    public void BuyImbalance_ZeroOppositeSide_CountsIfAboveMinVolume()
    {
        Assert.Equal(new[] { 100m }, Bar(B(100m, 12)).BuyImbalances(3.0m, 12m));
        Assert.Empty(Bar(B(100m, 11)).BuyImbalances(3.0m, 12m));
    }

    [Fact]
    public void SellImbalance_IsDiagonal_BidVsAskOneTickAbove()
    {
        var bar = Bar(S(100m, 30), B(100.25m, 10));
        Assert.Equal(new[] { 100m }, bar.SellImbalances(3.0m, 12m));
    }

    [Fact]
    public void LongestStack_CountsConsecutiveTicksOnly()
    {
        Assert.Equal(3, FootprintBar.LongestStack(new[] { 100m, 100.25m, 100.5m, 101m }, 0.25m));
        Assert.Equal(0, FootprintBar.LongestStack(Array.Empty<decimal>(), 0.25m));
    }

    [Fact]
    public void UnfinishedAuction_AtHighAndLow()
    {
        var bar = Bar(B(100m, 1), S(100m, 1), B(99m, 1), B(100m, 1));
        Assert.True(bar.UnfinishedHigh);    // 100: both sides traded
        Assert.False(bar.UnfinishedLow);    // 99: only ask
    }
}
