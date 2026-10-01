using System.Collections.Immutable;
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

/// <summary>Lucid Flex evaluation memory. Lucid checks MLL at EOD only; intraday protection is the guard's job.</summary>
public sealed record EvaluationTracker(
    decimal EodHighBalance, decimal MllFloor, ImmutableDictionary<DateOnly, decimal> DailyPnl,
    decimal WinProfitTotal, decimal WinProfitShortHold, bool Breached, bool TargetReached)
{
    public static EvaluationTracker Start(AccountRules a) => new(
        a.InitialBalance, a.InitialBalance - a.MllDistanceUsd, ImmutableDictionary<DateOnly, decimal>.Empty, 0m, 0m, false, false);

    public decimal TotalProfit => DailyPnl.Values.Sum();
    public decimal BestDay => DailyPnl.IsEmpty ? 0m : Math.Max(0m, DailyPnl.Values.Max());
    public decimal ConsistencyRatio => TotalProfit <= 0 ? 0m : BestDay / TotalProfit;
    public int TradingDays => DailyPnl.Count;
    public decimal MicroscalpPercent => WinProfitTotal <= 0 ? 0m : WinProfitShortHold / WinProfitTotal * 100m;
    public decimal Headroom(decimal equity) => equity - MllFloor;

    public EvaluationTracker WithEndOfDay(DateOnly date, decimal closingBalance, AccountRules a)
    {
        var high = Math.Max(EodHighBalance, closingBalance);
        var floor = Math.Max(MllFloor, Math.Min(high - a.MllDistanceUsd, a.MllLockFloorUsd));
        return this with { EodHighBalance = high, MllFloor = floor, Breached = Breached || closingBalance <= MllFloor };
    }

    public EvaluationTracker WithTradeClosed(DateOnly date, decimal pnl, double holdSeconds, RiskSettings r, AccountRules a)
    {
        var isWin = pnl > 0;
        var next = this with
        {
            DailyPnl = DailyPnl.SetItem(date, (DailyPnl.TryGetValue(date, out var v) ? v : 0m) + pnl),
            WinProfitTotal = WinProfitTotal + (isWin ? pnl : 0m),
            WinProfitShortHold = WinProfitShortHold + (isWin && holdSeconds <= r.MicroscalpHoldSeconds ? pnl : 0m),
        };
        return next with { TargetReached = TargetReached || next.TargetConditionsMet(a) };
    }

    public bool ConsistencyEarlyStop(DateOnly today, AccountRules a)
    {
        var todayPnl = DailyPnl.TryGetValue(today, out var v) ? v : 0m;
        return todayPnl > 0 && todayPnl >= Math.Max(TotalProfit, a.ProfitTargetUsd) * a.ConsistencyEarlyStopPercent / 100m;
    }

    public bool TargetConditionsMet(AccountRules a) =>
        TotalProfit >= a.ProfitTargetUsd + a.TargetBufferUsd
        && TradingDays >= a.MinTradingDays
        && ConsistencyRatio <= a.ConsistencyCapPercent / 100m;
}
