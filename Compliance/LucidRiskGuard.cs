using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public enum Side { Buy, Sell }
public sealed record EntryRequest(DateTime NowUtc, Side Side, int Quantity, decimal StopPoints, decimal PointValue, bool IsAPlus);
public sealed record GuardContext(decimal Equity, int OpenPositionQty, Side? OpenPositionSide, bool DataHealthy, bool Connected);
public sealed record GuardDecision(bool Allowed, string Reason, RiskTier Tier);

/// <summary>Veto authority over every entry. Every rejection is logged at Trading level as the audit trail.</summary>
public sealed class LucidRiskGuard
{
    public const int LockedQuantity = 1;

    private readonly object _gate = new();
    private readonly AccountRules _a;
    private readonly RiskSettings _r;
    private readonly SessionClock _clock;
    private readonly NewsBlackout _news;
    private readonly RiskGovernor _gov;
    private readonly ILogSink _log;
    private EvaluationTracker _eval;
    private DailyRiskState _day;
    private string? _safeModeReason;

    private LucidRiskGuard(AccountRules a, RiskSettings r, SessionClock c, NewsBlackout n, ILogSink log, EvaluationTracker e, DailyRiskState d)
    {
        _a = a; _r = r; _clock = c; _news = n; _log = log;
        _gov = new RiskGovernor(r);
        _eval = e; _day = d;
    }

    public static LucidRiskGuard ForFlexEvaluation(AccountRules a, RiskSettings r, SessionClock clock, NewsBlackout news,
        ILogSink log, EvaluationTracker? eval = null, DailyRiskState? day = null)
        => new(a, r, clock, news, log, eval ?? EvaluationTracker.Start(a), day ?? DailyRiskState.New(DateOnly.MinValue));

    public EvaluationTracker Eval { get { lock (_gate) return _eval; } }
    public DailyRiskState Day { get { lock (_gate) return _day; } }
    public bool IsBreached { get { lock (_gate) return _eval.Breached; } }
    public bool DailyHaltActive { get { lock (_gate) return _day.HaltReason is not null; } }
    public bool ManualReenableRequired { get { lock (_gate) return _eval.TargetReached || _safeModeReason is not null; } }

    public RiskTier TierFor(decimal equity) { lock (_gate) return _gov.TierFor(_eval.Headroom(equity)); }

    public void HaltSafeMode(string reason)
    {
        lock (_gate) { _safeModeReason = reason; _log.Trading($"SAFE MODE: {reason}"); }
    }

    public bool ShouldFlattenNow(DateTime utc)
    {
        lock (_gate) return _clock.IsPastFlatten(utc) || _eval.Breached || _safeModeReason is not null;
    }

    public GuardDecision CanEnter(EntryRequest q, GuardContext ctx)
    {
        lock (_gate)
        {
            RollDay(q.NowUtc);
            var tier = _gov.TierFor(_eval.Headroom(ctx.Equity));
            var reason = FirstVeto(q, ctx);
            if (reason is not null) _log.Trading($"VETO [{_clock.Stamp(q.NowUtc)}] {q.Side} x{q.Quantity}: {reason}");
            return new GuardDecision(reason is null, reason ?? "OK", tier);
        }
    }

    public void OnTradeClosed(DateTime closeUtc, decimal pnl, double holdSeconds)
    {
        lock (_gate)
        {
            RollDay(closeUtc);
            _day = _day.WithTradeClosed(pnl, closeUtc, _r);
            _eval = _eval.WithTradeClosed(_day.Date, pnl, holdSeconds, _r, _a);
            if (_eval.MicroscalpPercent >= _r.MicroscalpWarnPercent) _log.Trading($"WARN microscalp share {_eval.MicroscalpPercent:F1}%");
            if (_day.HaltReason is not null) _log.Trading($"DAY HALT: {_day.HaltReason}");
            if (_eval.TargetReached) _log.Trading("TARGET REACHED: trading stopped, manual re-enable required");
        }
    }

    public void OnEndOfDay(DateOnly date, decimal closingBalance)
    {
        lock (_gate)
        {
            _eval = _eval.WithEndOfDay(date, closingBalance, _a);
            _log.Trading($"EOD {date}: close {closingBalance:F2}, floor {_eval.MllFloor:F2}, breached {_eval.Breached}");
        }
    }

    private string? FirstVeto(EntryRequest q, GuardContext ctx)
    {
        if (_safeModeReason is not null) return $"Safe mode: {_safeModeReason}";
        if (_eval.Breached) return "Account breached (EOD MLL)";
        if (_eval.TargetReached) return "Profit target reached: manual re-enable required";
        if (!ctx.Connected) return "Disconnected";
        if (!ctx.DataHealthy) return "Data health failing";
        if (q.Quantity != LockedQuantity) return $"Quantity {q.Quantity} != locked {LockedQuantity}";
        if (ctx.OpenPositionQty != 0)
            return ctx.OpenPositionSide is { } s && s != q.Side ? "Hedge blocked: opposite side of open position" : "Single-position rule";
        if (q.Quantity > _a.MaxContractsAllowed) return $"Exceeds MaxContractsAllowed {_a.MaxContractsAllowed}";
        if (!_clock.IsEntryWeekday(q.NowUtc)) return "No entries Fri/Sat/Sun";
        if (_clock.IsPastFlatten(q.NowUtc)) return "Past flatten time";
        if (_clock.ActiveWindow(q.NowUtc) is null) return "Outside session windows";
        if (_news.IsBlocked(q.NowUtc, out var news)) return news;
        if (_eval.MicroscalpPercent >= _r.MicroscalpHaltPercent)
            return $"Microscalp {_eval.MicroscalpPercent:F1}% >= {_r.MicroscalpHaltPercent}%";
        if (_eval.ConsistencyEarlyStop(_day.Date, _a)) return "Consistency early stop for today";
        if (!_day.CanEnter(q.NowUtc, q.IsAPlus, _r, out var dayReason)) return dayReason;
        var risk = _gov.CheckTrade(q.StopPoints, q.PointValue, _eval.Headroom(ctx.Equity));
        return risk.Ok ? null : risk.Reason;
    }

    private void RollDay(DateTime utc)
    {
        var td = _clock.TradingDate(utc);
        if (td != _day.Date) _day = DailyRiskState.New(td);
    }
}
