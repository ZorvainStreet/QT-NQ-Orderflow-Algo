using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.OrderFlow;

namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record DataHealth(
    bool WarmupComplete, double LastTickAgeSeconds, double FallbackPercent,
    long Accepted, long Dropped, bool FallbackWarning, bool IsHealthy,
    long FutureTicks, long LateTrades1m, long LateTrades5m);

/// <summary>
/// Single owner of all market-data accumulators. Every public member takes one lock; callers get snapshots only.
/// OnTimer must not run during backfill: historic ticks would land in buckets advanced to wall-clock time.
/// </summary>
public sealed class MarketDataPipeline
{
    private readonly object _gate = new();
    private readonly DataSettings _s;
    private readonly SessionClock _clock;
    private readonly TickNormalizer _normalizer;
    private readonly FootprintBuilder _bars1m, _bars5m;
    private readonly TapeWindow _tapeShort, _tapeMid, _tapeLong;
    private readonly BucketSeries _buckets;
    private readonly SessionProfile _profile;
    private decimal _sessionCvd;
    private DateOnly? _cvdDate;
    private DateTime? _lastTradeUtc;
    private decimal? _lastPrice;
    private bool _warm;
    private long _futureTicks;

    public MarketDataPipeline(DataSettings settings, SessionClock clock, decimal tickSize)
    {
        _s = settings;
        _clock = clock;
        _normalizer = new TickNormalizer(settings, tickSize);
        _bars1m = new FootprintBuilder(TimeSpan.FromMinutes(1), tickSize, settings.BarHistoryCapacity);
        _bars5m = new FootprintBuilder(TimeSpan.FromMinutes(5), tickSize, settings.BarHistoryCapacity);
        _tapeShort = new TapeWindow(TimeSpan.FromSeconds(settings.TapeShortSeconds));
        _tapeMid = new TapeWindow(TimeSpan.FromSeconds(settings.TapeMidSeconds));
        _tapeLong = new TapeWindow(TimeSpan.FromSeconds(settings.TapeLongSeconds));
        _buckets = new BucketSeries(TimeSpan.FromSeconds(settings.TapeMidSeconds),
            Math.Max(1, settings.BaselineMinutes * 60 / settings.TapeMidSeconds));
        _profile = new SessionProfile(clock, settings);
    }

    public DateTime? LastTradeUtc { get { lock (_gate) return _lastTradeUtc; } }

    public void OnQuote(RawQuote q) { lock (_gate) _normalizer.OnQuote(q); }

    /// <summary>Unchecked path for backfill and replay: no wall-clock comparison.</summary>
    public FootprintBar? OnTrade(RawTrade raw) => Ingest(raw, null);

    /// <summary>Live path: a print more than MaxFutureSkewSeconds ahead of <paramref name="nowUtc"/> is rejected and counted.</summary>
    public FootprintBar? OnTrade(RawTrade raw, DateTime nowUtc) => Ingest(raw, nowUtc);

    private FootprintBar? Ingest(RawTrade raw, DateTime? nowUtc)
    {
        lock (_gate)
        {
            if (nowUtc is { } now && raw.Utc > now + TimeSpan.FromSeconds(_s.MaxFutureSkewSeconds))
            {
                _futureTicks++;
                return null;
            }
            if (_normalizer.OnTrade(raw) is not { } t) return null;
            var tradingDate = _clock.TradingDate(t.Utc);
            if (_cvdDate != tradingDate) { _cvdDate = tradingDate; _sessionCvd = 0m; }
            _sessionCvd += t.IsBuy ? t.Size : -t.Size;
            _lastTradeUtc = t.Utc;
            _lastPrice = t.Price;
            _tapeShort.Add(t);
            _tapeMid.Add(t);
            _tapeLong.Add(t);
            _buckets.Add(t);
            _profile.OnTrade(t);
            _bars5m.OnTrade(t);
            return _bars1m.OnTrade(t);
        }
    }

    public FootprintBar? OnTimer(DateTime nowUtc)
    {
        lock (_gate)
        {
            var graced = nowUtc - TimeSpan.FromMilliseconds(_s.BarCloseGraceMs);
            _bars5m.CloseIfElapsed(graced);
            _buckets.AdvanceTo(graced);
            _tapeShort.Trim(nowUtc);
            _tapeMid.Trim(nowUtc);
            _tapeLong.Trim(nowUtc);
            return _bars1m.CloseIfElapsed(graced);
        }
    }

    public OrderFlowSnapshot Snapshot(DateTime nowUtc)
    {
        lock (_gate)
        {
            return OrderFlowFeatures.Compute(new FeatureInputs(nowUtc, _bars1m.Closed, _buckets.Closed, _tapeMid.Count,
                _sessionCvd, _lastPrice, _profile.Snapshot().Developing, _s, _normalizer.TickSize));
        }
    }

    public SessionLevels Levels() { lock (_gate) return _profile.Snapshot(); }

    public AbsorptionResult Absorption(decimal level, bool bullish)
    {
        lock (_gate)
            return OrderFlowFeatures.Absorption(_tapeMid.Trades, _buckets.MedianVolume, level, bullish, _s, _normalizer.TickSize);
    }

    public DataHealth Health(DateTime nowUtc)
    {
        lock (_gate)
        {
            var age = _lastTradeUtc is { } last ? (nowUtc - last).TotalSeconds : double.PositiveInfinity;
            var fallbackWarning = _normalizer.FallbackPercent > _s.FallbackWarnPercent;
            return new DataHealth(_warm, age, _normalizer.FallbackPercent, _normalizer.Accepted, _normalizer.Dropped,
                fallbackWarning, _warm && age <= _s.MaxTickAgeSeconds && age >= -_s.MaxFutureSkewSeconds,
                _futureTicks, _bars1m.LateTrades, _bars5m.LateTrades);
        }
    }

    public void MarkWarmupComplete() { lock (_gate) _warm = true; }
    public IReadOnlyList<FootprintBar> Bars1m() { lock (_gate) return _bars1m.Closed; }
    public IReadOnlyList<FootprintBar> Bars5m() { lock (_gate) return _bars5m.Closed; }
}
