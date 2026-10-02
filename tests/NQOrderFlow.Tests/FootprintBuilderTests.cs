using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class FootprintBuilderTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static Trade At(double seconds, decimal price = 100m) => new(T0.AddSeconds(seconds), price, 1, true, false);

    [Fact]
    public void BucketStart_FloorsToPeriod()
        => Assert.Equal(T0.AddMinutes(5), FootprintBuilder.BucketStart(T0.AddMinutes(7).AddSeconds(3), TimeSpan.FromMinutes(5)));

    [Fact]
    public void NewMinute_ClosesPreviousBar()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        Assert.Null(b.OnTrade(At(0)));
        Assert.Null(b.OnTrade(At(59.9)));
        var closed = b.OnTrade(At(60));
        Assert.Equal(T0, closed!.StartUtc);
        Assert.Equal(2m, closed.TotalVolume);
        Assert.Equal(T0.AddMinutes(1), b.Current!.StartUtc);
    }

    [Fact]
    public void QuietMinutes_CreateNoFakeBars()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        b.OnTrade(At(0));
        b.OnTrade(At(300));
        Assert.Single(b.Closed);
    }

    [Fact]
    public void CloseIfElapsed_ClosesOnTimeWithoutTrades()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        b.OnTrade(At(10));
        Assert.Null(b.CloseIfElapsed(T0.AddSeconds(59)));
        Assert.Equal(T0, b.CloseIfElapsed(T0.AddSeconds(60))!.StartUtc);
        Assert.Null(b.Current);
    }

    [Fact]
    public void History_IsCapped_OldestDropped()
    {
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 2);
        for (int m = 0; m <= 3; m++) b.OnTrade(At(m * 60));
        Assert.Equal(new[] { T0.AddMinutes(1), T0.AddMinutes(2) }, b.Closed.Select(x => x.StartUtc));
    }

    [Fact]
    public void LateTrade_AfterCloseAndTimeAdvance_RejectedAndCounted()
    {
        // Test (a): trade at 10s, close at 60s, then trade at 59.9s
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        b.OnTrade(At(10));
        b.CloseIfElapsed(T0.AddSeconds(60));
        var late = b.OnTrade(At(59.9));  // Trade from same original period
        Assert.Null(late);
        Assert.Null(b.Current);
        Assert.Equal(1, b.LateTrades);
        Assert.Single(b.Closed);
    }

    [Fact]
    public void LateTrade_FromOldBucket_RejectedAndCounted()
    {
        // Test (b): Current at minute 2, then trade from minute 1
        var b = new FootprintBuilder(TimeSpan.FromMinutes(1), 0.25m, 10);
        b.OnTrade(At(120));  // Minute 2
        var late = b.OnTrade(At(60));  // Minute 1 (older)
        Assert.Null(late);
        Assert.NotNull(b.Current);
        Assert.Equal(T0.AddMinutes(2), b.Current!.StartUtc);
        Assert.Equal(1, b.LateTrades);
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidTickSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FootprintBuilder(TimeSpan.FromMinutes(1), 0m, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FootprintBuilder(TimeSpan.FromMinutes(1), -0.25m, 10));
    }
}
