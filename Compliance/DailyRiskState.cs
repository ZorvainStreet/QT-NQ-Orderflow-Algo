using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

/// <summary>Self-imposed daily limits. Lucid Flex has no DLL; these protect the $1,000 EOD cushion.</summary>
public sealed record DailyRiskState(
    DateOnly Date, decimal RealizedPnl, decimal PeakPnl, int Trades, int ConsecutiveLosses,
    DateTime? LastCloseUtc, bool LastWasWin, DateTime? StreakPauseUntilUtc, bool PostStreakTradeUsed, string? HaltReason)
{
    public static DailyRiskState New(DateOnly date) => new(date, 0m, 0m, 0, 0, null, false, null, false, null);

    public DailyRiskState WithTradeClosed(decimal pnl, DateTime closeUtc, RiskSettings s)
    {
        var losses = pnl < 0 ? ConsecutiveLosses + 1 : 0;
        var streakHit = StreakPauseUntilUtc is null && pnl < 0 && losses >= s.MaxConsecutiveLosses;
        var next = this with
        {
            RealizedPnl = RealizedPnl + pnl,
            PeakPnl = Math.Max(PeakPnl, RealizedPnl + pnl),
            Trades = Trades + 1,
            ConsecutiveLosses = losses,
            LastCloseUtc = closeUtc,
            LastWasWin = pnl > 0,
            StreakPauseUntilUtc = streakHit ? closeUtc.AddMinutes(s.StreakPauseMinutes) : StreakPauseUntilUtc,
            PostStreakTradeUsed = PostStreakTradeUsed || StreakPauseUntilUtc is not null,
        };
        return next with { HaltReason = HaltReason ?? next.EvaluateHalt(s) };
    }

    public bool CanEnter(DateTime nowUtc, bool isAPlus, RiskSettings s, out string reason)
    {
        reason = Veto(nowUtc, isAPlus, s) ?? "";
        return reason.Length == 0;
    }

    private string? Veto(DateTime nowUtc, bool isAPlus, RiskSettings s)
    {
        if (HaltReason is not null) return $"Day halted: {HaltReason}";
        if (Trades >= s.MaxTradesPerDay) return $"Max trades {s.MaxTradesPerDay} reached";
        if (StreakPauseUntilUtc is { } pause)
        {
            if (nowUtc < pause) return $"Loss-streak pause until {pause:HH:mm}Z";
            if (PostStreakTradeUsed) return "Post-streak trade already used";
            if (!isAPlus) return "Post-streak: A+ only";
        }
        if (LastCloseUtc is { } last)
        {
            var until = last.AddMinutes(LastWasWin ? s.WinCooldownMinutes : s.LossCooldownMinutes);
            if (nowUtc < until) return $"Post-trade cooldown until {until:HH:mm:ss}Z";
        }
        return null;
    }

    private string? EvaluateHalt(RiskSettings s)
    {
        if (RealizedPnl <= -s.MaxDailyLossUsd) return $"Daily loss {RealizedPnl:F2} <= -{s.MaxDailyLossUsd}";
        if (RealizedPnl >= s.DailyProfitCapUsd) return $"Daily profit cap {RealizedPnl:F2} >= {s.DailyProfitCapUsd}";
        if (PeakPnl > s.GivebackArmUsd && RealizedPnl <= PeakPnl * (1 - s.GivebackPercent / 100m))
            return $"Giveback {RealizedPnl:F2} from peak {PeakPnl:F2}";
        return null;
    }
}
