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

    /// <summary>Range and ordering checks for the Phase 2 data settings. Returns the first problem, or null.</summary>
    public static string? Validate(DataSettings s)
    {
        if (s.BadTickMaxTicks < 1) return "BadTickMaxTicks must be >= 1";
        if (s.BadTickConfirmCount < 2) return "BadTickConfirmCount must be >= 2 (1 disables the jump filter)";
        if (s.MaxFutureSkewSeconds < 1) return "MaxFutureSkewSeconds must be >= 1";
        if (s.BarCloseGraceMs is < 0 or > 10000) return "BarCloseGraceMs must be in 0..10000";
        if (!double.IsFinite(s.FallbackWarnPercent) || !double.IsFinite(s.VelocityZLimit)) return "FallbackWarnPercent and VelocityZLimit must be finite numbers";
        if (s.MaxTickAgeSeconds < 1 || s.BarHistoryCapacity < 20) return "MaxTickAgeSeconds >= 1 and BarHistoryCapacity >= 20 required";
        if (s.ImbalanceRatio < 1m || s.ImbalanceMinVolume < 0m || s.StackedLevels < 1) return "Imbalance settings out of range";
        if (!(0 < s.TapeShortSeconds && s.TapeShortSeconds < s.TapeMidSeconds && s.TapeMidSeconds < s.TapeLongSeconds))
            return "Tape windows must satisfy 0 < short < mid < long";
        if (s.BaselineMinutes < 1 || s.MedianBarLookback < 1) return "Baseline and lookback must be >= 1";
        if (s.AbsorptionVolMultiple <= 0m || s.AbsorptionMaxProgressTicks < 0 || s.DeltaFlipMinMultiple < 0m) return "Absorption/flip settings out of range";
        if (s.AbsorptionDominancePercent is < 0m or > 100m || s.ValueAreaPercent is <= 0m or > 100m) return "Percent settings out of range";
        if (s.FallbackWarnPercent is < 0 or > 100 || s.VelocityZLimit <= 0) return "Fallback/velocity settings out of range";
        if (!(0 < s.OpeningRangeShortMinutes && s.OpeningRangeShortMinutes < s.OpeningRangeLongMinutes)) return "Opening ranges must satisfy 0 < short < long";
        if (s.RthOpenEt >= s.RthCloseEt) return "RthOpenEt must be before RthCloseEt";
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
