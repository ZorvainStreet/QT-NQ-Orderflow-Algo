using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class TickNormalizerTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static TickNormalizer New(DataSettings? s = null) => new(s ?? new DataSettings(), 0.25m);
    private static RawTrade Raw(int ms, decimal price, decimal size = 1, Aggressor side = Aggressor.Buy)
        => new(T0.AddMilliseconds(ms), price, size, side);

    [Fact]
    public void ExchangeFlag_IsTruth()
    {
        var n = New();
        Assert.False(n.OnTrade(Raw(0, 20000m, side: Aggressor.Sell))!.Value.IsBuy);
        Assert.Equal(0, n.Fallbacks);
    }

    [Fact]
    public void Price_IsSnappedToTick()
        => Assert.Equal(20000.25m, New().OnTrade(Raw(0, 20000.2600001m))!.Value.Price);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSize_IsDropped(int size)
    {
        var n = New();
        Assert.Null(n.OnTrade(Raw(0, 20000m, size)));
        Assert.Equal(1, n.Dropped);
    }

    [Fact]
    public void OutOfOrder_IsDropped_SameTimestamp_IsKept()
    {
        var n = New();
        n.OnTrade(Raw(100, 20000m));
        Assert.NotNull(n.OnTrade(Raw(100, 20000m)));   // same ms, legitimate second print
        Assert.Null(n.OnTrade(Raw(99, 20000m)));
    }

    [Fact]
    public void IdenticalPrints_KeptByDefault_DroppedWhenEnabled()
    {
        var keep = New();
        keep.OnTrade(Raw(0, 20000m));
        Assert.NotNull(keep.OnTrade(Raw(0, 20000m)));
        var drop = New(new DataSettings(DropIdenticalPrints: true));
        drop.OnTrade(Raw(0, 20000m));
        Assert.Null(drop.OnTrade(Raw(0, 20000m)));
    }

    [Fact]
    public void BadTick_WithoutQuoteChange_IsDropped()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        Assert.Null(n.OnTrade(Raw(1, 20011m)));        // 44 ticks > 40
        Assert.NotNull(n.OnTrade(Raw(2, 20000.25m)));
    }

    [Fact]
    public void BadTick_FilterAccepts_AfterConfirmedJump()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        Assert.Null(n.OnTrade(Raw(1, 20011m)));
        Assert.Null(n.OnTrade(Raw(2, 20011m)));
        Assert.NotNull(n.OnTrade(Raw(3, 20011m)));     // 3rd consecutive confirms the jump
    }

    [Fact]
    public void Jump_AfterQuoteChange_IsAccepted()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        n.OnQuote(new RawQuote(T0.AddMilliseconds(1), 20010.75m, 20011m));
        Assert.NotNull(n.OnTrade(Raw(2, 20011m)));
    }

    [Fact]
    public void Fallback_UsesQuoteThenTickRuleThenPreviousSide()
    {
        var n = New();
        n.OnQuote(new RawQuote(T0, 19999.75m, 20000m));
        Assert.True(n.OnTrade(Raw(1, 20000m, side: Aggressor.Unknown))!.Value.IsBuy);        // at ask
        Assert.False(n.OnTrade(Raw(2, 19999.75m, side: Aggressor.Unknown))!.Value.IsBuy);    // at bid
        n.OnQuote(new RawQuote(T0.AddMilliseconds(3), 19999.5m, 20000.5m));
        Assert.True(n.OnTrade(Raw(4, 20000m, side: Aggressor.Unknown))!.Value.IsBuy);        // inside spread, uptick
        Assert.True(n.OnTrade(Raw(5, 20000m, side: Aggressor.Unknown))!.Value.IsBuy);        // unchanged → previous
        Assert.Equal(4, n.Fallbacks);
        Assert.Equal(100.0, n.FallbackPercent);
    }

    [Fact]
    public void CrossedQuote_IsIgnored()
    {
        var n = New();
        n.OnQuote(new RawQuote(T0, 20001m, 20000m));
        n.OnTrade(Raw(1, 20000m));
        Assert.Null(n.OnTrade(Raw(2, 20011m)));         // crossed quote did not count as a quote change
    }

    [Fact]
    public void NonPositiveTickSize_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TickNormalizer(new DataSettings(), 0m));

    [Fact]
    public void ScatteredBadJumps_AllDropped()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        Assert.Null(n.OnTrade(Raw(1, 20011m)));   // 44 ticks out, candidate=20011, _jumpDrops=1
        Assert.Null(n.OnTrade(Raw(2, 20100m)));   // 400 ticks out, different from 20011, restart candidate=20100, _jumpDrops=1
        Assert.Null(n.OnTrade(Raw(3, 19900m)));   // 400 ticks out, different from 20100, restart candidate=19900, _jumpDrops=1
        Assert.Equal(3, n.Dropped);
    }

    [Fact]
    public void JumpThenNormalThenJumpJump_LastStillDropped()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        Assert.Null(n.OnTrade(Raw(1, 20011m)));        // jump, candidate=20011, _jumpDrops=1
        Assert.NotNull(n.OnTrade(Raw(2, 20000m)));     // normal, clears candidate and _jumpDrops
        Assert.Null(n.OnTrade(Raw(3, 20011m)));        // jump again, candidate=20011, _jumpDrops=1
        Assert.Null(n.OnTrade(Raw(4, 20011m)));        // agrees with candidate, _jumpDrops=2, still < 3
        Assert.Equal(3, n.Dropped);
    }

    [Fact]
    public void QuoteChangeWithoutCorroboration_JumpStillDropped()
    {
        var n = New();
        n.OnTrade(Raw(0, 20000m));
        n.OnQuote(new RawQuote(T0.AddMilliseconds(1), 19999.75m, 20000m));
        // Quote is at 19999.75/20000, but print at 20011 is far from both.
        Assert.Null(n.OnTrade(Raw(2, 20011m)));        // 44 ticks from last, not corroborated by quote
        Assert.Equal(1, n.Dropped);
    }
}
