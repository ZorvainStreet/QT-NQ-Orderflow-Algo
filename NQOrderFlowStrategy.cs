using System.Diagnostics.Metrics;
using System.Globalization;
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Persistence;
using TradingPlatform.BusinessLayer;

namespace QT_MNQ_Orderflow_Algo;

/// <summary>Phase 1: lifecycle, inputs, clock, risk layer, persistence, metrics. Places NO orders.</summary>
public sealed class NQOrderFlowStrategy : Strategy
{
    private const int TimerPeriodMs = 1000;
    private const int TimerDisposeWaitMs = 5000;
    private const string HhMm = @"hh\:mm";

    [InputParameter("Symbol", 10)] public Symbol? symbol;
    [InputParameter("Account", 20)] public Account? account;
    [InputParameter("Allowed roots (CSV)", 30)] public string AllowedRoots = "NQ";

    [InputParameter("Initial balance", 100)] public double InitialBalance = 25000;
    [InputParameter("MLL distance USD", 110)] public double MllDistanceUsd = 1000;
    [InputParameter("MLL lock floor USD (VERIFY)", 120)] public double MllLockFloorUsd = 25000; // VERIFY
    [InputParameter("Profit target USD", 130)] public double ProfitTargetUsd = 1250;
    [InputParameter("Target buffer USD", 135)] public double TargetBufferUsd = 25;
    [InputParameter("Consistency cap %", 140)] public double ConsistencyCapPercent = 50;
    [InputParameter("Consistency early stop %", 145)] public double ConsistencyEarlyStopPercent = 45;
    [InputParameter("Max contracts allowed (VERIFY)", 150, 1, 10, 1, 0)] public int MaxContractsAllowed = 1; // VERIFY
    [InputParameter("Min trading days", 155, 1, 30, 1, 0)] public int MinTradingDays = 2;

    [InputParameter("NY AM start ET", 200)] public string NyAmStart = "09:35";
    [InputParameter("NY AM end ET", 210)] public string NyAmEnd = "11:45";
    [InputParameter("NY PM enabled", 220)] public bool NyPmEnabled = true;
    [InputParameter("NY PM start ET", 230)] public string NyPmStart = "13:30";
    [InputParameter("NY PM end ET", 240)] public string NyPmEnd = "15:30";
    [InputParameter("London enabled", 250)] public bool LondonEnabled = false;
    [InputParameter("London start ET", 252)] public string LondonStart = "03:00";
    [InputParameter("London end ET", 254)] public string LondonEnd = "05:00";
    [InputParameter("Flatten time ET", 260)] public string FlattenTimeEt = "15:55";
    [InputParameter("Lucid deadline ET", 262)] public string LucidDeadlineEt = "16:45";
    [InputParameter("Trading day roll ET", 264)] public string TradingDayRollEt = "18:00";
    [InputParameter("News CSV path", 270)] public string NewsCsvPath = "";
    [InputParameter("News block before (min)", 272, 0, 60, 1, 0)] public int NewsBeforeMin = 2;
    [InputParameter("News block after (min)", 274, 0, 60, 1, 0)] public int NewsAfterMin = 3;

    [InputParameter("Tier A min headroom USD", 290)] public double TierAMinHeadroom = 900;
    [InputParameter("Tier B min headroom USD", 295)] public double TierBMinHeadroom = 600;
    [InputParameter("Max risk tier A USD", 300)] public double MaxRiskTierA = 250;
    [InputParameter("Max risk tier B USD", 305)] public double MaxRiskTierB = 200;
    [InputParameter("Max risk tier C USD", 310)] public double MaxRiskTierC = 150;
    [InputParameter("Max stop points tier C", 311)] public double MaxStopPointsTierC = 7.5;
    [InputParameter("Score add tier B", 312, 0, 50, 1, 0)] public int ScoreAddTierB = 5;
    [InputParameter("Score add tier C", 313, 0, 50, 1, 0)] public int ScoreAddTierC = 10;
    [InputParameter("Max risk % of headroom", 314)] public double MaxRiskPercentOfHeadroom = 25;
    [InputParameter("Halt headroom USD", 315)] public double HaltHeadroomUsd = 350;
    [InputParameter("Max daily loss USD", 320)] public double MaxDailyLossUsd = 350;
    [InputParameter("Max consecutive losses", 325, 1, 10, 1, 0)] public int MaxConsecutiveLosses = 2;
    [InputParameter("Streak pause (min)", 327, 0, 240, 1, 0)] public int StreakPauseMinutes = 30;
    [InputParameter("Max trades/day", 330, 1, 20, 1, 0)] public int MaxTradesPerDay = 4;
    [InputParameter("Daily profit cap USD", 335)] public double DailyProfitCapUsd = 450;
    [InputParameter("Giveback arm USD", 338)] public double GivebackArmUsd = 200;
    [InputParameter("Giveback %", 340)] public double GivebackPercent = 40;
    [InputParameter("Loss cooldown (min)", 345, 0, 120, 1, 0)] public int LossCooldownMinutes = 10;
    [InputParameter("Win cooldown (min)", 350, 0, 120, 1, 0)] public int WinCooldownMinutes = 3;
    [InputParameter("Commission per side USD (VERIFY)", 355)] public double CommissionPerSideUsd = 2.5; // VERIFY
    [InputParameter("Slippage USD", 357)] public double SlippageUsd = 5;
    [InputParameter("Microscalp hold (s)", 358, 0, 60, 1, 0)] public int MicroscalpHoldSeconds = 5;
    [InputParameter("Microscalp warn %", 360)] public double MicroscalpWarnPercent = 20;
    [InputParameter("Microscalp halt %", 365)] public double MicroscalpHaltPercent = 35;
    [InputParameter("Safe-mode exception count", 370, 1, 100, 1, 0)] public int SafeModeExceptionCount = 5;

    [InputParameter("State path (blank = Documents/NQ_OrderFlow/state.json)", 900)] public string StatePath = "";
    [InputParameter("EMERGENCY FLATTEN", 999)] public bool EmergencyFlatten = false;

    private SessionClock? _clock;
    private LucidRiskGuard? _guard;
    private StateStore? _store;
    private RiskSettings _risk = new();
    private Timer? _timer;
    private int _consecutiveErrors;
    private long _lastTickUtcTicks;
    private string? _lastStatusLine;
    private bool _emergencyLogged;
    private bool _stateLoadFailed;
    private volatile bool _stopping;
    private int _timerBusy;
    private int _tickKindLogged;

    public override string[] MonitoringConnectionsIds => new[] { symbol?.ConnectionId ?? "" };

    public NQOrderFlowStrategy()
    {
        Name = "NQ OrderFlow LucidFlex";
        Description = "Phase 1 shell: clock + risk layer, no orders";
    }

    protected override void OnRun()
    {
        _guard = null;
        _store = null;
        _clock = null;
        _stateLoadFailed = false;
        _stopping = false;
        _lastStatusLine = null;
        Interlocked.Exchange(ref _lastTickUtcTicks, 0);
        Interlocked.Exchange(ref _tickKindLogged, 0);
        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            Log($"OnRun failed: {ex}", StrategyLoggingLevel.Error);
            _guard?.HaltSafeMode($"OnRun exception: {ex.Message}");
            Stop();
        }
    }

    private void RunCore()
    {
        if (symbol is null || account is null || symbol.ConnectionId != account.ConnectionId)
        {
            Log("Symbol/Account missing or on different connections", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        symbol = Core.GetSymbol(symbol.CreateInfo());
        if (symbol is null)
        {
            Log("Symbol could not be resolved from the connection", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        var root = SymbolRules.ExtractRoot(symbol.Name);
        if (!SymbolRules.IsAllowed(root, SymbolRules.ParseAllowed(AllowedRoots)))
        {
            Log($"Rejected symbol root '{root}' (allowed: {AllowedRoots})", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        if (!TryBuildSessionSettings(out var session, out var badField))
        {
            Log($"Invalid HH:mm in input '{badField}'", StrategyLoggingLevel.Error);
            Stop();
            return;
        }

        var log = new QuantowerLogSink(this);
        _risk = BuildRiskSettings();
        _clock = new SessionClock(session);
        var news = NewsBlackout.Load(string.IsNullOrWhiteSpace(NewsCsvPath) ? null : NewsCsvPath, _clock, NewsBeforeMin, NewsAfterMin, log);
        _store = new StateStore(ResolveStatePath());
        var loaded = _store.Load();
        _guard = LucidRiskGuard.ForFlexEvaluation(BuildAccountRules(), _risk, _clock, news, log, loaded.State?.Eval, loaded.State?.Day);
        _stateLoadFailed = loaded.Error is not null;
        if (loaded.Error is not null) _guard.HaltSafeMode(loaded.Error);
        _guard.EnsureTradingDay(NowUtc());

        LogWindowsInIst(log);
        symbol.NewLast += OnNewLast;
        _timer = new Timer(_ => OnTimer(), null, TimerPeriodMs, TimerPeriodMs);
        Log($"Started {_clock.Stamp(NowUtc())} | Phase 1: NO ORDERS", StrategyLoggingLevel.Trading);
    }

    protected override void OnStop()
    {
        _stopping = true;
        var timer = Interlocked.Exchange(ref _timer, null);
        if (timer is not null)
        {
            using var done = new ManualResetEvent(false);
            if (timer.Dispose(done)) done.WaitOne(TimerDisposeWaitMs); // wait for an in-flight callback
        }
        if (symbol is not null) symbol.NewLast -= OnNewLast;
        SaveState();
    }

    protected override void OnRemove()
    {
        symbol = null;
        account = null;
    }

    /// <summary>
    /// Non-obsolete metrics API in 1.146.18 (OnGetMetrics is [Obsolete]). Instruments are numeric only,
    /// so text values (clock, tier name, windows, flags) go to the log via <see cref="LogStatusIfChanged"/>.
    /// </summary>
    protected override void OnInitializeMetrics(Meter meter)
    {
        base.OnInitializeMetrics(meter);
        Gauge(meter, "Equity", () => Equity());
        Gauge(meter, "MLL floor", () => _guard?.Eval.MllFloor);
        Gauge(meter, "Headroom", () => Equity() is { } e ? _guard?.Eval.Headroom(e) : null);
        Gauge(meter, "Tier (0=A 1=B 2=C 3=Halt)", () => Equity() is { } e && _guard is not null ? (int)_guard.TierFor(e) : null);
        Gauge(meter, "Day PnL", () => _guard?.Day.RealizedPnl);
        Gauge(meter, "Day trades", () => _guard?.Day.Trades);
        Gauge(meter, "Loss streak", () => _guard?.Day.ConsecutiveLosses);
        Gauge(meter, "Eval profit", () => _guard?.Eval.TotalProfit);
        Gauge(meter, "Profit target", () => (decimal)ProfitTargetUsd);
        Gauge(meter, "Consistency best-day %", () => _guard is null ? null : _guard.Eval.ConsistencyRatio * 100m);
        Gauge(meter, "Microscalp %", () => _guard?.Eval.MicroscalpPercent);
        Gauge(meter, "Breached", () => _guard is null ? null : _guard.IsBreached ? 1 : 0);
        Gauge(meter, "Daily halt", () => _guard is null ? null : _guard.DailyHaltActive ? 1 : 0);
        Gauge(meter, "Manual re-enable", () => _guard is null ? null : _guard.ManualReenableRequired ? 1 : 0);
        Gauge(meter, "Minutes to next window", () => MinutesToNextWindow());
        Gauge(meter, "Last tick age (s)", () => TickAgeSeconds());
    }

    private void Gauge(Meter meter, string name, Func<decimal?> read) =>
        meter.CreateObservableGauge(name, () =>
        {
            try
            {
                var v = read();
                return v is null ? Array.Empty<Measurement<double>>() : new[] { new Measurement<double>((double)v.Value) };
            }
            catch (Exception ex)
            {
                Log($"Metric '{name}' failed: {ex.Message}", StrategyLoggingLevel.Error);
                return Array.Empty<Measurement<double>>();
            }
        });

    private decimal? Equity() => account is null ? null : (decimal)account.Balance;

    private decimal? MinutesToNextWindow()
    {
        if (_clock is null) return null;
        var now = NowUtc();
        return _clock.NextWindowOpen(now) is { } nx ? (decimal)(nx.OpensUtc - now).TotalMinutes : null;
    }

    private decimal? TickAgeSeconds()
    {
        var ticks = Interlocked.Read(ref _lastTickUtcTicks);
        return ticks == 0 ? null : (decimal)(NowUtc() - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
    }

    private void OnNewLast(Symbol s, Last last) => Guarded(() =>
    {
        if (Interlocked.Exchange(ref _tickKindLogged, 1) == 0)
            Log($"First tick: Last.Time={last.Time:O} Kind={last.Time.Kind} (tick age assumes UTC)", StrategyLoggingLevel.Info);
        Interlocked.Exchange(ref _lastTickUtcTicks, last.Time.Ticks);
    });

    private void OnTimer()
    {
        if (_stopping || Interlocked.Exchange(ref _timerBusy, 1) == 1) return; // stopped, or previous tick still running
        try
        {
            if (!_stopping) Guarded(TimerTick);
        }
        finally
        {
            Interlocked.Exchange(ref _timerBusy, 0);
        }
    }

    private void TimerTick()
    {
        var now = NowUtc();
        _guard!.EnsureTradingDay(now);
        if (EmergencyFlatten && !_emergencyLogged)
        {
            Log("EmergencyFlatten is set; Phase 1 holds no positions (Phase 5 wires the flatten)", StrategyLoggingLevel.Trading);
            _emergencyLogged = true;
        }
        if (!EmergencyFlatten) _emergencyLogged = false;
        if (_guard.ShouldFlattenNow(now)) { /* Phase 5: ExecutionEngine.FlattenAll() */ }
        LogStatusIfChanged(now);
    }

    /// <summary>Text-valued status (formerly OnGetMetrics strings) logged whenever it changes.</summary>
    private void LogStatusIfChanged(DateTime now)
    {
        if (_guard is null || _clock is null || Equity() is not { } equity) return;
        var next = _clock.NextWindowOpen(now) is { } nx
            ? $"{nx.Window.Name} at IST {_clock.ToIst(nx.OpensUtc):ddd HH:mm}"
            : "none";
        var line = $"Tier={_guard.TierFor(equity)} | Active={_clock.ActiveWindow(now)?.Name ?? "none"} | Next={next} | " +
                   $"breached={_guard.IsBreached} dayHalt={_guard.DailyHaltActive} manual={_guard.ManualReenableRequired}";
        if (line == _lastStatusLine) return;
        _lastStatusLine = line;
        Log($"{_clock.Stamp(now)} | {line}", StrategyLoggingLevel.Info);
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
            Interlocked.Exchange(ref _consecutiveErrors, 0);
        }
        catch (Exception ex)
        {
            Log($"Handler error: {ex}", StrategyLoggingLevel.Error);
            if (Interlocked.Increment(ref _consecutiveErrors) >= _risk.SafeModeExceptionCount)
                _guard?.HaltSafeMode("Repeated handler exceptions");
        }
    }

    private static DateTime NowUtc() => Core.Instance.TimeUtils.DateTimeUtcNow;

    private void SaveState()
    {
        try
        {
            if (_stateLoadFailed)
            {
                Log("State not saved: load failed this run; fix or remove the state file to re-enable (fail closed)", StrategyLoggingLevel.Error);
                return;
            }
            if (_guard is null || _store is null) return;
            if (_guard.Day.Date == default)
            {
                // Mirrors StateStore.Validate ("missing Day.Date"): never write a file the next load would reject.
                Log("State not saved: trading day was never rolled (Day.Date unset)", StrategyLoggingLevel.Error);
                return;
            }
            _store.Save(new PersistedState(PersistedState.CurrentSchemaVersion, _guard.Eval, _guard.Day, null));
        }
        catch (Exception ex)
        {
            Log($"State save failed: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private string ResolveStatePath() => string.IsNullOrWhiteSpace(StatePath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NQ_OrderFlow", "state.json")
        : StatePath;

    private bool TryBuildSessionSettings(out SessionSettings settings, out string badField)
    {
        settings = SessionSettings.Default();
        var fields = new (string Name, string Value)[]
        {
            (nameof(NyAmStart), NyAmStart), (nameof(NyAmEnd), NyAmEnd), (nameof(NyPmStart), NyPmStart),
            (nameof(NyPmEnd), NyPmEnd), (nameof(LondonStart), LondonStart), (nameof(LondonEnd), LondonEnd),
            (nameof(FlattenTimeEt), FlattenTimeEt), (nameof(LucidDeadlineEt), LucidDeadlineEt),
            (nameof(TradingDayRollEt), TradingDayRollEt),
        };
        var parsed = new Dictionary<string, TimeSpan>();
        foreach (var (name, value) in fields)
        {
            if (!TimeSpan.TryParseExact(value?.Trim(), HhMm, CultureInfo.InvariantCulture, out var t)) { badField = name; return false; }
            parsed[name] = t;
        }
        settings = settings with
        {
            Windows = new[]
            {
                new SessionWindow("NY_AM_KILLZONE", parsed[nameof(NyAmStart)], parsed[nameof(NyAmEnd)], true, 0),
                new SessionWindow("NY_PM", parsed[nameof(NyPmStart)], parsed[nameof(NyPmEnd)], NyPmEnabled, 5),
                new SessionWindow("LONDON_OPEN", parsed[nameof(LondonStart)], parsed[nameof(LondonEnd)], LondonEnabled, 0),
            },
            FlattenTimeEt = parsed[nameof(FlattenTimeEt)],
            LucidDeadlineEt = parsed[nameof(LucidDeadlineEt)],
            TradingDayRollEt = parsed[nameof(TradingDayRollEt)],
            NewsBeforeMin = NewsBeforeMin,
            NewsAfterMin = NewsAfterMin,
        };
        badField = "";
        return true;
    }

    private AccountRules BuildAccountRules() => new()
    {
        InitialBalance = (decimal)InitialBalance,
        MllDistanceUsd = (decimal)MllDistanceUsd,
        MllLockFloorUsd = (decimal)MllLockFloorUsd,
        ProfitTargetUsd = (decimal)ProfitTargetUsd,
        ConsistencyCapPercent = (decimal)ConsistencyCapPercent,
        ConsistencyEarlyStopPercent = (decimal)ConsistencyEarlyStopPercent,
        MaxContractsAllowed = MaxContractsAllowed,
        TargetBufferUsd = (decimal)TargetBufferUsd,
        MinTradingDays = MinTradingDays,
    };

    private RiskSettings BuildRiskSettings() => new()
    {
        MaxRiskTierA = (decimal)MaxRiskTierA,
        MaxRiskTierB = (decimal)MaxRiskTierB,
        MaxRiskTierC = (decimal)MaxRiskTierC,
        HaltHeadroomUsd = (decimal)HaltHeadroomUsd,
        MaxDailyLossUsd = (decimal)MaxDailyLossUsd,
        MaxConsecutiveLosses = MaxConsecutiveLosses,
        MaxTradesPerDay = MaxTradesPerDay,
        DailyProfitCapUsd = (decimal)DailyProfitCapUsd,
        GivebackPercent = (decimal)GivebackPercent,
        LossCooldownMinutes = LossCooldownMinutes,
        WinCooldownMinutes = WinCooldownMinutes,
        CommissionPerSideUsd = (decimal)CommissionPerSideUsd,
        MicroscalpWarnPercent = (decimal)MicroscalpWarnPercent,
        MicroscalpHaltPercent = (decimal)MicroscalpHaltPercent,
        TierAMinHeadroom = (decimal)TierAMinHeadroom,
        TierBMinHeadroom = (decimal)TierBMinHeadroom,
        MaxStopPointsTierC = (decimal)MaxStopPointsTierC,
        ScoreAddTierB = ScoreAddTierB,
        ScoreAddTierC = ScoreAddTierC,
        MaxRiskPercentOfHeadroom = (decimal)MaxRiskPercentOfHeadroom,
        SlippageUsd = (decimal)SlippageUsd,
        StreakPauseMinutes = StreakPauseMinutes,
        GivebackArmUsd = (decimal)GivebackArmUsd,
        MicroscalpHoldSeconds = MicroscalpHoldSeconds,
        SafeModeExceptionCount = SafeModeExceptionCount,
    };

    private void LogWindowsInIst(ILogSink log)
    {
        var etToday = _clock!.ToEt(NowUtc()).Date;
        foreach (var w in _clock.Settings.Windows.Where(w => w.Enabled))
        {
            var startUtc = _clock.EtToUtc(etToday + w.StartEt);
            var endUtc = _clock.EtToUtc(etToday + w.EndEt);
            log.Info($"{w.Name}: {w.StartEt:hh\\:mm}-{w.EndEt:hh\\:mm} ET = {_clock.ToIst(startUtc):HH:mm}-{_clock.ToIst(endUtc):HH:mm} IST (today)");
        }
    }

    private sealed class QuantowerLogSink : ILogSink
    {
        private readonly NQOrderFlowStrategy _s;
        public QuantowerLogSink(NQOrderFlowStrategy s) => _s = s;
        public void Info(string m) => _s.Log(m, StrategyLoggingLevel.Info);
        public void Trading(string m) => _s.Log(m, StrategyLoggingLevel.Trading);
        public void Error(string m) => _s.Log(m, StrategyLoggingLevel.Error);
    }
}
