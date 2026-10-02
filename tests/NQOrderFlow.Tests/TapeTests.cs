using QT_MNQ_Orderflow_Algo.Data;
using Xunit;

public sealed class TapeTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static Trade At(double s, decimal size, bool buy) => new(T0.AddSeconds(s), 100m, size, buy, false);

    [Fact]
    public void Tape_SumsAndExpires()
    {
        var w = new TapeWindow(TimeSpan.FromSeconds(20));
        w.Add(At(0, 5, true));
        w.Add(At(10, 3, false));
        Assert.Equal((5m, 3m, 2m, 2), (w.BuyVolume, w.SellVolume, w.Delta, w.Count));
        w.Trim(T0.AddSeconds(20));                       // first trade is exactly 20 s old → out
        Assert.Equal((0m, 3m, 1), (w.BuyVolume, w.SellVolume, w.Count));
    }

    [Fact]
    public void Buckets_CloseAndZeroFillGaps()
    {
        var b = new BucketSeries(TimeSpan.FromSeconds(20), 10);
        b.Add(At(1, 4, true));
        b.Add(At(5, 1, false));
        b.Add(At(65, 2, true));                          // skips the 20 s and 40 s buckets
        Assert.Equal(
            new[] { (T0, 5m, 3m, 2), (T0.AddSeconds(20), 0m, 0m, 0), (T0.AddSeconds(40), 0m, 0m, 0) },
            b.Closed.Select(x => (x.StartUtc, x.Volume, x.Delta, x.Trades)));
    }

    [Fact]
    public void Buckets_AreCapped_AndMedianUsesClosedOnly()
    {
        var b = new BucketSeries(TimeSpan.FromSeconds(20), 2);
        b.Add(At(0, 2, true));
        b.Add(At(20, 4, true));
        b.Add(At(40, 6, true));
        b.Add(At(60, 100, true));                        // current, not closed
        Assert.Equal(new[] { 4m, 6m }, b.Closed.Select(x => x.Volume));
        Assert.Equal(5m, b.MedianVolume);
    }

    [Fact]
    public void Buckets_AdvanceTo_ClosesOnTime()
    {
        var b = new BucketSeries(TimeSpan.FromSeconds(20), 10);
        b.Add(At(1, 3, true));
        b.AdvanceTo(T0.AddSeconds(25));
        Assert.Single(b.Closed);
    }
}
