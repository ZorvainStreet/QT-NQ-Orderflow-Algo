using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class NewsBlackoutTests
{
    private readonly SessionClock _clock = new(SessionSettings.Default());
    private static DateTime Utc(int d, int h, int m) => new(2026, 10, d, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void Recurring1000Et_BlocksMinus2ToPlus3()
    {
        var nb = NewsBlackout.Load(null, _clock, 2, 3, new NullLogSink());
        Assert.False(nb.IsBlocked(Utc(29, 13, 57), out _));     // 09:57 ET
        Assert.True(nb.IsBlocked(Utc(29, 13, 58), out var r));  // 09:58 ET
        Assert.Contains("10:00", r);
        Assert.True(nb.IsBlocked(Utc(29, 14, 2), out _));
        Assert.False(nb.IsBlocked(Utc(29, 14, 3), out _));      // T+3 exclusive
    }

    [Fact]
    public void CsvEvent_IsParsedInEt()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "# FOMC\n2026-10-28 14:00,FOMC Statement\n");
        var nb = NewsBlackout.Load(path, _clock, 2, 3, new NullLogSink());
        Assert.True(nb.IsBlocked(Utc(28, 18, 0), out var r));   // 14:00 EDT
        Assert.Contains("FOMC", r);
    }

    [Fact]
    public void MalformedRow_IsSkipped_NotThrown()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "garbage\n2026-10-28 14:00,FOMC\n");
        Assert.True(NewsBlackout.Load(path, _clock, 2, 3, new NullLogSink()).IsBlocked(Utc(28, 18, 0), out _));
    }
}
