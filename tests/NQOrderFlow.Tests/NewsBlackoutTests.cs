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

    [Fact]
    public void FridayAfter1000Et_NextEventUtc_SkipsWeekendToMondayMorning()
    {
        // Friday 2026-10-30 11:00 ET (15:00 UTC) - after 10:00 ET recurring
        // Next event should be Monday 2026-11-02 08:30 ET (13:30 UTC in EST)
        var nb = NewsBlackout.Load(null, _clock, 2, 3, new NullLogSink());
        var fridayUtc = Utc(30, 15, 0);  // Friday 11:00 ET
        var nextUtc = nb.NextEventUtc(fridayUtc);

        // Monday 08:30 ET in EST (after DST ends on Nov 1, 2026 at 02:00 UTC)
        // 08:30 EST = 13:30 UTC
        var expectedUtc = new DateTime(2026, 11, 2, 13, 30, 0, DateTimeKind.Utc);
        Assert.Equal(expectedUtc, nextUtc);
    }

    [Fact]
    public void Saturday0830Et_NotBlocked_NoRecurringOnWeekend()
    {
        var nb = NewsBlackout.Load(null, _clock, 2, 3, new NullLogSink());
        // Saturday 2026-10-31 08:30 ET = 12:30 UTC (EDT)
        Assert.False(nb.IsBlocked(Utc(31, 12, 30), out _));
    }

    [Fact]
    public void CsvWithDstGapTime_DoesNotThrow_ValidRowStillBlocks()
    {
        var path = Path.GetTempFileName();
        // 2026-03-08 02:30 is in the DST gap (01:59:59 EST → 03:00:00 EDT)
        File.WriteAllText(path, "2026-03-08 02:30,DST Gap\n2026-10-28 14:00,FOMC\n");

        // Should not throw; Load should skip the gap-time row and load the FOMC row
        var nb = NewsBlackout.Load(path, _clock, 2, 3, new NullLogSink());

        // Verify the valid FOMC row (14:00 ET) still blocks
        Assert.True(nb.IsBlocked(Utc(28, 18, 0), out var r));
        Assert.Contains("FOMC", r);
    }

    [Fact]
    public void MissingCsvFile_LogsError_NotInfo()
    {
        var logs = new TestLogSink();
        var nb = NewsBlackout.Load("/nonexistent/path.csv", _clock, 2, 3, logs);

        // Should log error, not info
        Assert.Contains(logs.Errors, e => e.Contains("NewsBlackout") || e.Contains("nonexistent"));
        Assert.DoesNotContain(logs.Infos, i => i.Contains("no CSV"));
    }
}

public sealed class TestLogSink : ILogSink
{
    public List<string> Infos { get; } = new();
    public List<string> Tradings { get; } = new();
    public List<string> Errors { get; } = new();

    public void Info(string message) => Infos.Add(message);
    public void Trading(string message) => Tradings.Add(message);
    public void Error(string message) => Errors.Add(message);
}
