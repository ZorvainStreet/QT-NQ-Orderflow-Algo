using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

/// <summary>All trading time logic in US Eastern. Inputs are UTC; the PC time zone is irrelevant.</summary>
public sealed class SessionClock
{
    private const int MaxLookaheadDays = 8;
    private readonly TimeZoneInfo _et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private readonly TimeZoneInfo _ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    public SessionClock(SessionSettings settings) => Settings = settings;

    public SessionSettings Settings { get; }
    public DateTime ToEt(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), _et);
    public DateTime ToIst(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), _ist);
    public DateTime EtToUtc(DateTime etLocal) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(etLocal, DateTimeKind.Unspecified), _et);

    public DateOnly TradingDate(DateTime utc)
    {
        var et = ToEt(utc);
        var d = DateOnly.FromDateTime(et);
        return et.TimeOfDay >= Settings.TradingDayRollEt ? d.AddDays(1) : d;
    }

    /// <summary>Previous weekday trading date. Exchange holidays are not modeled (see docs/ASSUMPTIONS.md).</summary>
    public DateOnly PreviousTradingDate(DateOnly tradingDate)
    {
        var d = tradingDate.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    /// <summary>UTC start of a trading date's session: the previous calendar day at the roll time, ET.</summary>
    public DateTime SessionStartUtc(DateOnly tradingDate) =>
        EtToUtc(tradingDate.AddDays(-1).ToDateTime(TimeOnly.FromTimeSpan(Settings.TradingDayRollEt)));

    public bool IsEntryWeekday(DateTime utc) => IsEntryDay(ToEt(utc).DayOfWeek);

    public SessionWindow? ActiveWindow(DateTime utc)
    {
        var tod = ToEt(utc).TimeOfDay;
        return Settings.Windows.FirstOrDefault(w => w.Enabled && tod >= w.StartEt && tod < w.EndEt);
    }

    public bool IsPastFlatten(DateTime utc)
    {
        var tod = ToEt(utc).TimeOfDay;
        return tod >= Settings.FlattenTimeEt && tod < Settings.TradingDayRollEt;
    }

    public (SessionWindow Window, DateTime OpensUtc)? NextWindowOpen(DateTime utc)
    {
        var etDate = ToEt(utc).Date;
        for (int day = 0; day < MaxLookaheadDays; day++)
        {
            var date = etDate.AddDays(day);
            if (!IsEntryDay(date.DayOfWeek)) continue;
            foreach (var w in Settings.Windows.Where(w => w.Enabled).OrderBy(w => w.StartEt))
            {
                var openUtc = EtToUtc(date + w.StartEt);
                if (openUtc > AsUtc(utc)) return (w, openUtc);
            }
        }
        return null;
    }

    public string Stamp(DateTime utc) =>
        $"UTC {AsUtc(utc):yyyy-MM-dd HH:mm:ss} | ET {ToEt(utc):HH:mm:ss} | IST {ToIst(utc):HH:mm:ss}";

    private static bool IsEntryDay(DayOfWeek d) =>
        d is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday;

    private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
