using System.Globalization;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public sealed class NewsBlackout
{
    private readonly SessionClock _clock;
    private readonly TimeSpan _before, _after;
    private readonly IReadOnlyList<(DateTime Utc, string Label)> _events;

    private NewsBlackout(SessionClock clock, int beforeMin, int afterMin, IReadOnlyList<(DateTime, string)> events)
    {
        _clock = clock;
        _before = TimeSpan.FromMinutes(beforeMin);
        _after = TimeSpan.FromMinutes(afterMin);
        _events = events;
    }

    public static NewsBlackout Load(string? csvPath, SessionClock clock, int beforeMin, int afterMin, ILogSink log)
    {
        var events = new List<(DateTime, string)>();
        if (csvPath is null)
        {
            log.Info("NewsBlackout: no CSV, using recurring news times only");
            return new NewsBlackout(clock, beforeMin, afterMin, events);
        }

        if (!File.Exists(csvPath))
        {
            log.Error($"NewsBlackout: CSV file not found '{csvPath}', using recurring news times only");
            return new NewsBlackout(clock, beforeMin, afterMin, events);
        }

        try
        {
            foreach (var raw in File.ReadAllLines(csvPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var et))
                {
                    log.Error($"NewsBlackout: skipped malformed row '{line}'");
                    continue;
                }
                try
                {
                    events.Add((clock.EtToUtc(et), parts.Length > 1 ? parts[1] : "News"));
                }
                catch (ArgumentException ex)
                {
                    log.Error($"NewsBlackout: skipped invalid DST time '{line}': {ex.Message}");
                }
            }
        }
        catch (IOException ex)
        {
            log.Error($"NewsBlackout: failed to read CSV '{csvPath}': {ex.Message}, using recurring news times only");
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Error($"NewsBlackout: no permission to read CSV '{csvPath}': {ex.Message}, using recurring news times only");
        }

        log.Info($"NewsBlackout: {events.Count} CSV events + recurring news times");
        return new NewsBlackout(clock, beforeMin, afterMin, events);
    }

    public bool IsBlocked(DateTime utc, out string reason)
    {
        foreach (var (evUtc, label) in Candidates(utc))
        {
            if (utc >= evUtc - _before && utc < evUtc + _after)
            {
                reason = $"News blackout: {label} at {_clock.ToEt(evUtc):HH:mm} ET";
                return true;
            }
        }
        reason = "";
        return false;
    }

    public DateTime? NextEventUtc(DateTime utc) =>
        Candidates(utc).Select(e => e.Utc).Where(t => t > utc).OrderBy(t => t).Cast<DateTime?>().FirstOrDefault();

    private IEnumerable<(DateTime Utc, string Label)> Candidates(DateTime utc)
    {
        foreach (var e in _events) yield return e;
        var etDate = _clock.ToEt(utc).Date;
        for (int d = 0; d <= 4; d++)
        {
            var date = etDate.AddDays(d);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            foreach (var t in _clock.Settings.RecurringNewsEt) yield return (_clock.EtToUtc(date + t), $"Recurring {t:hh\\:mm}");
        }
    }
}
