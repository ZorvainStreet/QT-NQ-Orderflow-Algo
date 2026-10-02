using System.Globalization;

namespace QT_MNQ_Orderflow_Algo.Config;

/// <summary>Range and ordering checks for user inputs. Pure; returns the first problem found, or null.</summary>
public static class SettingsValidator
{
    private const string HhMm = @"hh\:mm";

    public static string? FirstError(AccountRules a, RiskSettings r, SessionSettings s)
    {
        if (r.TierBMinHeadroom >= r.TierAMinHeadroom) return "TierBMinHeadroom must be below TierAMinHeadroom";
        if (r.HaltHeadroomUsd >= r.TierBMinHeadroom) return "HaltHeadroomUsd must be below TierBMinHeadroom";

        var negative = FirstNegative(a, r);
        if (negative is not null) return $"{negative} must not be negative";

        if (r.MaxRiskPercentOfHeadroom <= 0m || r.MaxRiskPercentOfHeadroom > 100m)
            return "MaxRiskPercentOfHeadroom must be in (0,100]";
        var badPercent = FirstOutOfPercentRange(a, r);
        if (badPercent is not null) return $"{badPercent} must be in [0,100]";

        foreach (var w in s.Windows)
            if (w.Enabled && w.StartEt >= w.EndEt) return $"Window {w.Name}: start must be before end";
        if (s.FlattenTimeEt >= s.TradingDayRollEt) return "FlattenTimeEt must be before TradingDayRollEt";
        return null;
    }

    /// <summary>Parses "HH:mm,HH:mm". Blank input yields an empty list; any bad element fails the whole parse.</summary>
    public static bool TryParseTimeCsv(string? csv, out IReadOnlyList<TimeSpan> times)
    {
        var result = new List<TimeSpan>();
        times = result;
        if (string.IsNullOrWhiteSpace(csv)) return true;
        foreach (var part in csv.Split(','))
        {
            if (!TimeSpan.TryParseExact(part.Trim(), HhMm, CultureInfo.InvariantCulture, out var t)) return false;
            result.Add(t);
        }
        return true;
    }

    /// <summary>Recurring news times. Blank means the spec defaults (never "no blocks"); a bad element fails.</summary>
    public static bool ResolveRecurringNews(string? csv, out IReadOnlyList<TimeSpan> times)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            times = SessionSettings.Default().RecurringNewsEt;
            return true;
        }
        return TryParseTimeCsv(csv, out times);
    }

    private static string? FirstNegative(AccountRules a, RiskSettings r)
    {
        var money = new (string Name, decimal Value)[]
        {
            (nameof(a.InitialBalance), a.InitialBalance), (nameof(a.MllDistanceUsd), a.MllDistanceUsd),
            (nameof(a.MllLockFloorUsd), a.MllLockFloorUsd), (nameof(a.ProfitTargetUsd), a.ProfitTargetUsd),
            (nameof(a.TargetBufferUsd), a.TargetBufferUsd), (nameof(r.TierAMinHeadroom), r.TierAMinHeadroom),
            (nameof(r.TierBMinHeadroom), r.TierBMinHeadroom), (nameof(r.HaltHeadroomUsd), r.HaltHeadroomUsd),
            (nameof(r.MaxRiskTierA), r.MaxRiskTierA), (nameof(r.MaxRiskTierB), r.MaxRiskTierB),
            (nameof(r.MaxRiskTierC), r.MaxRiskTierC), (nameof(r.MaxStopPointsTierC), r.MaxStopPointsTierC),
            (nameof(r.CommissionPerSideUsd), r.CommissionPerSideUsd), (nameof(r.SlippageUsd), r.SlippageUsd),
            (nameof(r.MaxDailyLossUsd), r.MaxDailyLossUsd), (nameof(r.DailyProfitCapUsd), r.DailyProfitCapUsd),
            (nameof(r.GivebackArmUsd), r.GivebackArmUsd),
            (nameof(r.StreakPauseMinutes), r.StreakPauseMinutes), (nameof(r.LossCooldownMinutes), r.LossCooldownMinutes),
            (nameof(r.WinCooldownMinutes), r.WinCooldownMinutes), (nameof(r.MicroscalpHoldSeconds), r.MicroscalpHoldSeconds),
        };
        return money.FirstOrDefault(m => m.Value < 0m).Name;
    }

    private static string? FirstOutOfPercentRange(AccountRules a, RiskSettings r)
    {
        var percents = new (string Name, decimal Value)[]
        {
            (nameof(a.ConsistencyCapPercent), a.ConsistencyCapPercent),
            (nameof(a.ConsistencyEarlyStopPercent), a.ConsistencyEarlyStopPercent),
            (nameof(r.GivebackPercent), r.GivebackPercent),
            (nameof(r.MicroscalpWarnPercent), r.MicroscalpWarnPercent),
            (nameof(r.MicroscalpHaltPercent), r.MicroscalpHaltPercent),
        };
        return percents.FirstOrDefault(p => p.Value < 0m || p.Value > 100m).Name;
    }
}
