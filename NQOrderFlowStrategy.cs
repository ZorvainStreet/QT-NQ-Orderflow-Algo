using System.Diagnostics.Metrics;
using System.Globalization;
using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Data;
using QT_MNQ_Orderflow_Algo.Persistence;
using QT_MNQ_Orderflow_Algo.Telemetry;
using TradingPlatform.BusinessLayer;

namespace QT_MNQ_Orderflow_Algo;

/// <summary>Phase 2: Phase 1 shell plus live feed, tick backfill, order-flow pipeline and bar debug log. Places NO orders.</summary>
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
    [InputParameter("NY AM score add", 215, 0, 50, 1, 0)] public int NyAmScoreAdd = 0;
    [InputParameter("NY PM enabled", 220)] public bool NyPmEnabled = true;
    [InputParameter("NY PM start ET", 230)] public string NyPmStart = "13:30";
    [InputParameter("NY PM end ET", 240)] public string NyPmEnd = "15:30";
    [InputParameter("NY PM score add", 245, 0, 50, 1, 0)] public int NyPmScoreAdd = 5;
    [InputParameter("London enabled", 250)] public bool LondonEnabled = false;
    [InputParameter("London start ET", 252)] public string LondonStart = "03:00";
    [InputParameter("London end ET", 254)] public string LondonEnd = "05:00";
    [InputParameter("London score add", 256, 0, 50, 1, 0)] public int LondonScoreAdd = 0;
    [InputParameter("Flatten time ET", 260)] public string FlattenTimeEt = "15:55";
    [InputParameter("Lucid deadline ET", 262)] public string LucidDeadlineEt = "16:45";
    [InputParameter("Trading day roll ET", 264)] public string TradingDayRollEt = "18:00";
    [InputParameter("News CSV path", 270)] public string NewsCsvPath = "";
    [InputParameter("News block before (min)", 272, 0, 60, 1, 0)] public int NewsBeforeMin = 2;
    [InputParameter("News block after (min)", 274, 0, 60, 1, 0)] public int NewsAfterMin = 3;
    [InputParameter("Recurring news times ET", 276)] public string RecurringNewsTimesEt = "08:30,10:00";

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

    // Phase 2 data / order-flow inputs (sort 400-499). Defaults equal the DataSettings record defaults;
    // ranges are checked by SettingsValidator.Validate(DataSettings), not by the UI min/max.
    [InputParameter("Bad-tick max distance (ticks)", 400, 0, 10000, 1, 0)] public int BadTickMaxTicks = 40;
    [InputParameter("Bad-tick confirm count", 402, 0, 100, 1, 0)] public int BadTickConfirmCount = 3;
    [InputParameter("Drop identical prints", 404)] public bool DropIdenticalPrints = false;
    [InputParameter("Aggressor fallback warn %", 406)] public double FallbackWarnPercent = 5;
    [InputParameter("Max tick age (s)", 408, 0, 3600, 1, 0)] public int MaxTickAgeSeconds = 3;
    [InputParameter("Bar history capacity", 410, 0, 100000, 1, 0)] public int BarHistoryCapacity = 500;
    [InputParameter("Imbalance ratio", 420)] public double ImbalanceRatio = 3.0;
    [InputParameter("Imbalance min volume", 422)] public double ImbalanceMinVolume = 12;
    [InputParameter("Stacked imbalance levels", 424, 0, 100, 1, 0)] public int StackedLevels = 3;
    [InputParameter("Tape short window (s)", 430, 0, 3600, 1, 0)] public int TapeShortSeconds = 5;
    [InputParameter("Tape mid window (s)", 432, 0, 3600, 1, 0)] public int TapeMidSeconds = 20;
    [InputParameter("Tape long window (s)", 434, 0, 3600, 1, 0)] public int TapeLongSeconds = 60;
    [InputParameter("Baseline (min)", 436, 0, 1440, 1, 0)] public int BaselineMinutes = 30;
    [InputParameter("Median bar lookback", 438, 0, 1000, 1, 0)] public int MedianBarLookback = 30;
    [InputParameter("Absorption volume multiple", 440)] public double AbsorptionVolMultiple = 2.0;
    [InputParameter("Absorption max progress (ticks)", 442, 0, 1000, 1, 0)] public int AbsorptionMaxProgressTicks = 3;
    [InputParameter("Absorption dominance %", 444)] public double AbsorptionDominancePercent = 65;
    [InputParameter("Delta flip min multiple", 446)] public double DeltaFlipMinMultiple = 0.5;
    [InputParameter("Velocity Z limit", 448)] public double VelocityZLimit = 3.0;
    [InputParameter("Value area %", 450)] public double ValueAreaPercent = 70;
    [InputParameter("Opening range short (min)", 460, 0, 600, 1, 0)] public int OpeningRangeShortMinutes = 5;
    [InputParameter("Opening range long (min)", 462, 0, 600, 1, 0)] public int OpeningRangeLongMinutes = 15;
    [InputParameter("Max future tick skew (s)", 412, 0, 3600, 1, 0)] public int MaxFutureSkewSeconds = 5;
    [InputParameter("Bar close grace (ms)", 414, 0, 10000, 1, 0)] public int BarCloseGraceMs = 1500;
    [InputParameter("RTH open ET", 470)] public string RthOpenEt = "09:30";
    [InputParameter("RTH close ET", 472)] public string RthCloseEt = "16:00";

    [InputParameter("State path (blank = Documents/NQ_OrderFlow/state.json)", 900)] public string StatePath = "";
    [InputParameter("EMERGENCY FLATTEN", 999)] public bool EmergencyFlatten = false;

    private SessionClock? _clock;
    private LucidRiskGuard? _guard;
    private StateStore? _store;
    private RiskSettings _risk = new();
    private Timer? _timer;
    private int _tickErrors;   // separate counters: a healthy handler must not reset a failing one
    private int _timerErrors;
    private int _quoteErrors;
    private string? _lastStatusLine;
    private bool _emergencyLogged;
    private bool _stateLoadFailed;
    private volatile bool _stopping;
    // Deliberately NOT reset in OnRun. A stale timer callback that outlives a stop/start clears this in its
    // own finally block; forcing it to 0 here would let a new callback overlap that stale one.
    private int _timerBusy;
    private int _tickKindLogged;

    private const int LogDrainPerTick = 50;
    private const int LiveBufferCapacity = 200_000;
    private const int LogQueueCapacity = 1_000;
    private const string ErrorPrefix = "ERROR ";
    private volatile MarketDataPipeline? _pipeline;
    private readonly object _feedGate = new();
    private readonly Queue<RawTrade> _liveBuffer = new();  // guarded by _feedGate
    private bool _backfilling;                              // guarded by _feedGate
    private bool _liveOverflow;                             // guarded by _feedGate
    private volatile BoundedQueue<string> _logQueue = new(LogQueueCapacity);
    private CancellationTokenSource? _backfillCts;
    private string? _lastHealthLine;

    public override string[] MonitoringConnectionsIds => new[] { symbol?.ConnectionId ?? "" };

    public NQOrderFlowStrategy()
    {
        Name = "NQ OrderFlow LucidFlex";
        Description = "Phase 2: clock, risk layer, order-flow data pipeline; no orders";
    }

    protected override void OnRun()
    {
        _guard = null;
        _store = null;
        _clock = null;
        _stateLoadFailed = false;
        _stopping = false;
        _lastStatusLine = null;
        Interlocked.Exchange(ref _tickErrors, 0);
        Interlocked.Exchange(ref _timerErrors, 0);
        Interlocked.Exchange(ref _quoteErrors, 0);
        Interlocked.Exchange(ref _tickKindLogged, 0);
        _pipeline = null;
        _lastHealthLine = null;
        lock (_feedGate)
        {
            _backfilling = false;
            _liveOverflow = false;
            _liveBuffer.Clear();
        }
        _logQueue = new BoundedQueue<string>(LogQueueCapacity);
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
        if (!TryCheckFiniteInputs(out var nonFinite))
        {
            Log($"Input '{nonFinite}' must be a finite number (NaN/Infinity rejected)", StrategyLoggingLevel.Error);
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
        var rangeError = SettingsValidator.FirstError(BuildAccountRules(), _risk, session);
        if (rangeError is not null)
        {
            Log($"Invalid input: {rangeError}", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        if (!TryBuildDataSettings(out var dataSettings, out var badDataField))
        {
            Log($"Invalid HH:mm in input '{badDataField}'", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        var dataError = SettingsValidator.Validate(dataSettings);
        if (dataError is not null)
        {
            Log($"Invalid data input: {dataError}", StrategyLoggingLevel.Error);
            Stop();
            return;
        }
        _clock = new SessionClock(session);
        var news = NewsBlackout.Load(string.IsNullOrWhiteSpace(NewsCsvPath) ? null : NewsCsvPath, _clock, NewsBeforeMin, NewsAfterMin, log);
        _store = new StateStore(ResolveStatePath());
        var loaded = _store.Load();
        _guard = LucidRiskGuard.ForFlexEvaluation(BuildAccountRules(), _risk, _clock, news, log, loaded.State?.Eval, loaded.State?.Day);
        _stateLoadFailed = loaded.Error is not null;
        if (loaded.Error is not null) _guard.HaltSafeMode(loaded.Error);
        _guard.EnsureTradingDay(NowUtc());

        LogWindowsInIst(log);
        log.Info($"Recurring news blocks ET: {string.Join(", ", session.RecurringNewsEt.Select(t => t.ToString(@"hh\:mm")))}");
        StartFeed(symbol, dataSettings);
        _timer = new Timer(_ => OnTimer(), null, TimerPeriodMs, TimerPeriodMs);
        Log($"Started {_clock.Stamp(NowUtc())} | Phase 2 data: NO ORDERS", StrategyLoggingLevel.Trading);
    }

    /// <summary>Subscribes the live feed (buffered while backfilling), then backfills from the prior session start.</summary>
    private void StartFeed(Symbol sym, DataSettings dataSettings)
    {
        var pipeline = new MarketDataPipeline(dataSettings, _clock!, (decimal)sym.TickSize);
        lock (_feedGate) { _backfilling = true; }
        _pipeline = pipeline;
        sym.NewLast += OnNewLast;
        sym.NewQuote += OnNewQuote;
        var now = NowUtc();
        var fromUtc = _clock!.SessionStartUtc(_clock.PreviousTradingDate(_clock.TradingDate(now)));
        var cts = new CancellationTokenSource();
        _backfillCts = cts;
        var token = cts.Token;
        Log($"Backfill started {_clock.Stamp(fromUtc)} → {_clock.Stamp(now)}", StrategyLoggingLevel.Info);
        _ = Task.Run(() => RunBackfill(sym, pipeline, fromUtc, now, token), token);
    }

    private void RunBackfill(Symbol sym, MarketDataPipeline pipeline, DateTime fromUtc, DateTime toUtc, CancellationToken token)
    {
        try
        {
            var lastBackfilled = QuantowerMarketData.Backfill(sym, fromUtc, toUtc, raw => pipeline.OnTrade(raw), token);
            lock (_feedGate)
            {
                // A cancelled or superseded run must not touch the new run's hand-off state.
                if (token.IsCancellationRequested || !ReferenceEquals(_pipeline, pipeline)) return;
                var replayed = 0;
                while (_liveBuffer.Count > 0)
                {
                    var raw = _liveBuffer.Dequeue();
                    if (lastBackfilled is null || raw.Utc > lastBackfilled.Value) { pipeline.OnTrade(raw); replayed++; }
                }
                _backfilling = false;
                if (_liveOverflow)
                {
                    _logQueue.TryEnqueue($"{ErrorPrefix}Live buffer overflowed during backfill: data stays unhealthy (warmup incomplete). Restart the strategy.");
                    return;
                }
                pipeline.MarkWarmupComplete();
                _logQueue.TryEnqueue($"Backfill done {_clock!.Stamp(fromUtc)} → {_clock.Stamp(toUtc)}; last {lastBackfilled:O}; replayed {replayed} live ticks");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            _logQueue.TryEnqueue($"Backfill stopped during shutdown: {ex.Message}");
        }
        catch (Exception ex)
        {
            lock (_feedGate)
            {
                if (!ReferenceEquals(_pipeline, pipeline)) return; // stale run: never log into the new run's queue
                _logQueue.TryEnqueue($"{ErrorPrefix}Backfill FAILED: {ex.Message}. Data stays unhealthy (warmup incomplete).");
                _backfilling = false; // live ticks flow again, but warmup is never marked complete (fail closed)
                _liveBuffer.Clear();
            }
        }
    }

    private void BufferLive(RawTrade raw)
    {
        if (_liveBuffer.Count < LiveBufferCapacity) _liveBuffer.Enqueue(raw);
        else _liveOverflow = true;
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
        var cts = Interlocked.Exchange(ref _backfillCts, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
        if (symbol is not null)
        {
            symbol.NewLast -= OnNewLast;
            symbol.NewQuote -= OnNewQuote;
        }
        DrainLogQueue(int.MaxValue);
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
        Gauge(meter, "last_tick_age_s", () => TickAgeSeconds());
        Gauge(meter, "data_warm", () => DataHealthNow() is { } h ? (h.WarmupComplete ? 1 : 0) : null);
        Gauge(meter, "data_healthy", () => DataHealthNow() is { } h ? (h.IsHealthy ? 1 : 0) : null);
        Gauge(meter, "fallback_pct", () => DataHealthNow() is { } h ? (decimal)h.FallbackPercent : null);
        Gauge(meter, "ticks_accepted", () => DataHealthNow()?.Accepted);
        Gauge(meter, "ticks_dropped", () => DataHealthNow()?.Dropped);
        Gauge(meter, "session_cvd", () => _pipeline?.Snapshot(NowUtc()).SessionCvd);
        Gauge(meter, "vwap", () => _pipeline?.Levels().Vwap);
        Gauge(meter, "last_bar_delta", () => _pipeline?.Snapshot(NowUtc()).LastBarDelta);
        Gauge(meter, "late_trades_1m", () => DataHealthNow()?.LateTrades1m);
        Gauge(meter, "future_ticks", () => DataHealthNow()?.FutureTicks);
        Gauge(meter, "log_dropped", () => _logQueue.Dropped);
    }

    private DataHealth? DataHealthNow() => _pipeline?.Health(NowUtc());

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

    private decimal? TickAgeSeconds() =>
        DataHealthNow() is { } h && double.IsFinite(h.LastTickAgeSeconds) ? (decimal)h.LastTickAgeSeconds : null;

    private void OnNewLast(Symbol s, Last last) => GuardedQueued(() =>
    {
        if (Interlocked.Exchange(ref _tickKindLogged, 1) == 0)
            _logQueue.TryEnqueue($"First tick: Last.Time={last.Time:O} Kind={last.Time.Kind} (tick age assumes UTC)");
        var raw = QuantowerMarketData.ToRawTrade(last);
        FootprintBar? closed;
        lock (_feedGate)
        {
            if (_backfilling) { BufferLive(raw); return; }
            closed = _pipeline?.OnTrade(raw, NowUtc());
        }
        if (closed is not null) EnqueueBarLog(closed);
    }, ref _tickErrors);

    /// <summary>Quotes are dropped while backfilling so live bid/ask never classifies historical ticks.</summary>
    private void OnNewQuote(Symbol s, Quote quote) => GuardedQueued(() =>
    {
        var raw = QuantowerMarketData.ToRawQuote(quote);
        lock (_feedGate)
        {
            if (_backfilling) return;
        }
        _pipeline?.OnQuote(raw);
    }, ref _quoteErrors);

    private void EnqueueBarLog(FootprintBar b)
    {
        var pipeline = _pipeline;
        if (pipeline is null || _clock is null) return;
        var lv = pipeline.Levels();
        var s = pipeline.Snapshot(b.StartUtc.AddMinutes(1));
        _logQueue.TryEnqueue(
            $"BAR {_clock.ToEt(b.StartUtc):HH:mm} ET O {b.Open} H {b.High} L {b.Low} C {b.Close} V {b.TotalVolume} " +
            $"Δ {b.Delta} (max {b.MaxDelta} min {b.MinDelta}) POC {b.Poc} stackB {s.StackedBuyLevels} stackS {s.StackedSellLevels} " +
            $"| VWAP {lv.Vwap:F2} σ {lv.VwapStdDev:F2} dPOC {lv.Developing?.Poc} VAH {lv.Developing?.Vah} VAL {lv.Developing?.Val} " +
            $"| CVD {s.SessionCvd} flip {s.DeltaFlip} velZ {s.VelocityZ:F1} ctx {s.Context}");
    }

    /// <summary>Pipeline timer and health transitions. OnTimer never runs while backfilling (checked under _feedGate).</summary>
    private void DataTick(DateTime now)
    {
        var pipeline = _pipeline;
        if (pipeline is null) return;
        FootprintBar? closed = null;
        lock (_feedGate)
        {
            if (!_backfilling) closed = pipeline.OnTimer(now);
        }
        if (closed is not null) EnqueueBarLog(closed);
        var h = pipeline.Health(now);
        var line = $"Data {(h.IsHealthy ? "HEALTHY" : "UNHEALTHY")} warm={h.WarmupComplete} fallbackWarning={h.FallbackWarning}";
        if (line == _lastHealthLine) return;
        _lastHealthLine = line;
        Log($"{line} (fallback {h.FallbackPercent:F1}%, tick age {h.LastTickAgeSeconds:F1}s)", StrategyLoggingLevel.Trading);
    }

    private void DrainLogQueue(int max)
    {
        foreach (var line in _logQueue.Drain(max))
        {
            if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal)) Log(line[ErrorPrefix.Length..], StrategyLoggingLevel.Error);
            else Log(line, StrategyLoggingLevel.Info);
        }
    }

    private void OnTimer()
    {
        if (_stopping || Interlocked.Exchange(ref _timerBusy, 1) == 1) return; // stopped, or previous tick still running
        try
        {
            if (!_stopping) Guarded(TimerTick, ref _timerErrors);
        }
        finally
        {
            Interlocked.Exchange(ref _timerBusy, 0);
        }
    }

    private void TimerTick()
    {
        try
        {
            var now = NowUtc();
            RiskTick(now); // Phase 1 risk/day roll first, unchanged
            DataTick(now);
        }
        finally
        {
            DrainLogQueue(LogDrainPerTick); // drains even if a tick step threw
        }
    }

    private void RiskTick(DateTime now)
    {
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

    private void Guarded(Action action, ref int consecutiveErrors)
    {
        try
        {
            action();
            Interlocked.Exchange(ref consecutiveErrors, 0);
        }
        catch (Exception ex)
        {
            Log($"Handler error: {ex}", StrategyLoggingLevel.Error);
            if (Interlocked.Increment(ref consecutiveErrors) >= _risk.SafeModeExceptionCount)
                _guard?.HaltSafeMode("Repeated handler exceptions");
        }
    }

    /// <summary>Feed-handler variant of <see cref="Guarded"/>: the error line goes through _logQueue (no blocking I/O).</summary>
    private void GuardedQueued(Action action, ref int consecutiveErrors)
    {
        try
        {
            action();
            Interlocked.Exchange(ref consecutiveErrors, 0);
        }
        catch (Exception ex)
        {
            _logQueue.TryEnqueue($"{ErrorPrefix}Feed handler error: {ex}");
            if (Interlocked.Increment(ref consecutiveErrors) >= _risk.SafeModeExceptionCount)
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

    private bool TryCheckFiniteInputs(out string badField)
    {
        var values = new (string Name, double Value)[]
        {
            (nameof(InitialBalance), InitialBalance),
            (nameof(MllDistanceUsd), MllDistanceUsd),
            (nameof(MllLockFloorUsd), MllLockFloorUsd),
            (nameof(ProfitTargetUsd), ProfitTargetUsd),
            (nameof(TargetBufferUsd), TargetBufferUsd),
            (nameof(ConsistencyCapPercent), ConsistencyCapPercent),
            (nameof(ConsistencyEarlyStopPercent), ConsistencyEarlyStopPercent),
            (nameof(TierAMinHeadroom), TierAMinHeadroom),
            (nameof(TierBMinHeadroom), TierBMinHeadroom),
            (nameof(MaxRiskTierA), MaxRiskTierA),
            (nameof(MaxRiskTierB), MaxRiskTierB),
            (nameof(MaxRiskTierC), MaxRiskTierC),
            (nameof(MaxStopPointsTierC), MaxStopPointsTierC),
            (nameof(MaxRiskPercentOfHeadroom), MaxRiskPercentOfHeadroom),
            (nameof(HaltHeadroomUsd), HaltHeadroomUsd),
            (nameof(MaxDailyLossUsd), MaxDailyLossUsd),
            (nameof(DailyProfitCapUsd), DailyProfitCapUsd),
            (nameof(GivebackArmUsd), GivebackArmUsd),
            (nameof(GivebackPercent), GivebackPercent),
            (nameof(CommissionPerSideUsd), CommissionPerSideUsd),
            (nameof(SlippageUsd), SlippageUsd),
            (nameof(MicroscalpWarnPercent), MicroscalpWarnPercent),
            (nameof(MicroscalpHaltPercent), MicroscalpHaltPercent),
            (nameof(FallbackWarnPercent), FallbackWarnPercent),
            (nameof(ImbalanceRatio), ImbalanceRatio),
            (nameof(ImbalanceMinVolume), ImbalanceMinVolume),
            (nameof(AbsorptionVolMultiple), AbsorptionVolMultiple),
            (nameof(AbsorptionDominancePercent), AbsorptionDominancePercent),
            (nameof(DeltaFlipMinMultiple), DeltaFlipMinMultiple),
            (nameof(VelocityZLimit), VelocityZLimit),
            (nameof(ValueAreaPercent), ValueAreaPercent),
        };
        foreach (var (name, value) in values)
            if (!double.IsFinite(value)) { badField = name; return false; }
        badField = "";
        return true;
    }

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
        if (!SettingsValidator.ResolveRecurringNews(RecurringNewsTimesEt, out var recurring)) { badField = nameof(RecurringNewsTimesEt); return false; }
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
                new SessionWindow("NY_AM_KILLZONE", parsed[nameof(NyAmStart)], parsed[nameof(NyAmEnd)], true, NyAmScoreAdd),
                new SessionWindow("NY_PM", parsed[nameof(NyPmStart)], parsed[nameof(NyPmEnd)], NyPmEnabled, NyPmScoreAdd),
                new SessionWindow("LONDON_OPEN", parsed[nameof(LondonStart)], parsed[nameof(LondonEnd)], LondonEnabled, LondonScoreAdd),
            },
            FlattenTimeEt = parsed[nameof(FlattenTimeEt)],
            LucidDeadlineEt = parsed[nameof(LucidDeadlineEt)],
            TradingDayRollEt = parsed[nameof(TradingDayRollEt)],
            NewsBeforeMin = NewsBeforeMin,
            NewsAfterMin = NewsAfterMin,
            RecurringNewsEt = recurring,
        };
        badField = "";
        return true;
    }

    private bool TryBuildDataSettings(out DataSettings settings, out string badField)
    {
        settings = new DataSettings();
        if (!TimeSpan.TryParseExact(RthOpenEt?.Trim(), HhMm, CultureInfo.InvariantCulture, out var rthOpen)) { badField = nameof(RthOpenEt); return false; }
        if (!TimeSpan.TryParseExact(RthCloseEt?.Trim(), HhMm, CultureInfo.InvariantCulture, out var rthClose)) { badField = nameof(RthCloseEt); return false; }
        settings = BuildDataSettings() with { RthOpenEt = rthOpen, RthCloseEt = rthClose };
        badField = "";
        return true;
    }

    private DataSettings BuildDataSettings() => new(
        BadTickMaxTicks: BadTickMaxTicks,
        BadTickConfirmCount: BadTickConfirmCount,
        DropIdenticalPrints: DropIdenticalPrints,
        FallbackWarnPercent: FallbackWarnPercent,
        MaxTickAgeSeconds: MaxTickAgeSeconds,
        BarHistoryCapacity: BarHistoryCapacity,
        ImbalanceRatio: (decimal)ImbalanceRatio,
        ImbalanceMinVolume: (decimal)ImbalanceMinVolume,
        StackedLevels: StackedLevels,
        TapeShortSeconds: TapeShortSeconds,
        TapeMidSeconds: TapeMidSeconds,
        TapeLongSeconds: TapeLongSeconds,
        BaselineMinutes: BaselineMinutes,
        AbsorptionVolMultiple: (decimal)AbsorptionVolMultiple,
        AbsorptionMaxProgressTicks: AbsorptionMaxProgressTicks,
        AbsorptionDominancePercent: (decimal)AbsorptionDominancePercent,
        DeltaFlipMinMultiple: (decimal)DeltaFlipMinMultiple,
        VelocityZLimit: VelocityZLimit,
        ValueAreaPercent: (decimal)ValueAreaPercent,
        OpeningRangeShortMinutes: OpeningRangeShortMinutes,
        OpeningRangeLongMinutes: OpeningRangeLongMinutes,
        MedianBarLookback: MedianBarLookback,
        MaxFutureSkewSeconds: MaxFutureSkewSeconds,
        BarCloseGraceMs: BarCloseGraceMs);

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
            DateTime startUtc, endUtc;
            try
            {
                startUtc = _clock.EtToUtc(etToday + w.StartEt);
                endUtc = _clock.EtToUtc(etToday + w.EndEt);
            }
            catch (ArgumentException ex) // DST-gap local time does not exist today
            {
                log.Error($"{w.Name}: {w.StartEt.ToString(HhMm)}-{w.EndEt.ToString(HhMm)} ET not shown, invalid local time today: {ex.Message}");
                continue;
            }
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
