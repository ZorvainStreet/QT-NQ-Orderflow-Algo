using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class SessionClockTests
{
    private readonly SessionClock _clock = new(SessionSettings.Default());
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Theory] // Thursdays: 2026-10-29 EDT, 2026-11-05 EST, 2026-03-05 EST, 2026-03-12 EDT
    [InlineData(2026, 10, 29, 13, 35)]
    [InlineData(2026, 11, 5, 14, 35)]
    [InlineData(2026, 3, 5, 14, 35)]
    [InlineData(2026, 3, 12, 13, 35)]
    public void NyAmOpens_At0935Et_AcrossDst(int y, int mo, int d, int h, int mi)
    {
        Assert.Equal("NY_AM_KILLZONE", _clock.ActiveWindow(Utc(y, mo, d, h, mi))?.Name);
        Assert.Null(_clock.ActiveWindow(Utc(y, mo, d, h, mi).AddMinutes(-1)));
    }

    [Fact]
    public void Ist_Is0530AheadOfUtc_NoDst()
    {
        Assert.Equal(new TimeSpan(19, 5, 0), _clock.ToIst(Utc(2026, 10, 29, 13, 35)).TimeOfDay);
        Assert.Equal(new TimeSpan(20, 5, 0), _clock.ToIst(Utc(2026, 11, 5, 14, 35)).TimeOfDay);
    }

    [Fact]
    public void NoEntries_FriSatSun_AllowedMonToThu()
    {
        Assert.True(_clock.IsEntryWeekday(Utc(2026, 10, 29, 14, 0)));   // Thu
        Assert.False(_clock.IsEntryWeekday(Utc(2026, 10, 30, 14, 0)));  // Fri
        Assert.False(_clock.IsEntryWeekday(Utc(2026, 11, 1, 14, 0)));   // Sun
        Assert.True(_clock.IsEntryWeekday(Utc(2026, 11, 2, 15, 0)));    // Mon
    }

    [Fact]
    public void LondonDisabledByDefault_LunchAndAfter1530HaveNoWindow()
    {
        Assert.Null(_clock.ActiveWindow(Utc(2026, 10, 29, 7, 30)));   // 03:30 ET
        Assert.Null(_clock.ActiveWindow(Utc(2026, 10, 29, 16, 30)));  // 12:30 ET
        Assert.Null(_clock.ActiveWindow(Utc(2026, 10, 29, 19, 30)));  // 15:30 ET (end exclusive)
    }

    [Fact]
    public void Flatten_From1555Et_UntilRoll()
    {
        Assert.False(_clock.IsPastFlatten(Utc(2026, 10, 29, 19, 54)));
        Assert.True(_clock.IsPastFlatten(Utc(2026, 10, 29, 19, 55)));
        Assert.True(_clock.IsPastFlatten(Utc(2026, 10, 29, 21, 59)));   // 17:59 ET
        Assert.False(_clock.IsPastFlatten(Utc(2026, 10, 29, 22, 0)));   // 18:00 ET new session
    }

    [Fact]
    public void TradingDate_RollsAt1800Et()
    {
        Assert.Equal(new DateOnly(2026, 10, 29), _clock.TradingDate(Utc(2026, 10, 29, 21, 0)));   // 17:00 ET
        Assert.Equal(new DateOnly(2026, 10, 30), _clock.TradingDate(Utc(2026, 10, 29, 22, 30)));  // 18:30 ET
    }

    [Fact]
    public void NextWindowOpen_FromThursdayEvening_IsMondayAm_InEst()
    {
        var next = _clock.NextWindowOpen(Utc(2026, 10, 29, 20, 0));
        Assert.Equal("NY_AM_KILLZONE", next?.Window.Name);
        Assert.Equal(Utc(2026, 11, 2, 14, 35), next?.OpensUtc);
    }
}
