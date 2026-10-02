using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record ValueArea(decimal Poc, decimal Vah, decimal Val)
{
    /// <summary>POC (ties → lowest price), then expand one traded level at a time toward the larger neighbor.</summary>
    public static ValueArea? Compute(IReadOnlyDictionary<decimal, decimal> volumeByPrice, decimal percent)
    {
        if (volumeByPrice.Count == 0) return null;
        var prices = volumeByPrice.Keys.OrderBy(p => p).ToArray();
        var total = volumeByPrice.Values.Sum();
        if (total <= 0) return null;
        int poc = 0;
        for (int i = 1; i < prices.Length; i++)
            if (volumeByPrice[prices[i]] > volumeByPrice[prices[poc]]) poc = i;
        int lo = poc, hi = poc;
        decimal acc = volumeByPrice[prices[poc]];
        var target = total * percent / 100m;
        while (acc < target && (lo > 0 || hi < prices.Length - 1))
        {
            var up = hi < prices.Length - 1 ? volumeByPrice[prices[hi + 1]] : -1m;
            var down = lo > 0 ? volumeByPrice[prices[lo - 1]] : -1m;
            if (up >= down) { hi++; acc += up; }
            else { lo--; acc += down; }
        }
        return new ValueArea(prices[poc], prices[hi], prices[lo]);
    }
}

public sealed record PriorSession(DateOnly TradingDate, decimal High, decimal Low, decimal Close, ValueArea Value);

public sealed record SessionLevels(
    DateOnly? TradingDate,
    decimal? Vwap, decimal? VwapStdDev,
    decimal? OnHigh, decimal? OnLow,
    decimal? Or5High, decimal? Or5Low, decimal? Or15High, decimal? Or15Low,
    decimal? RthHigh, decimal? RthLow,
    ValueArea? Developing,
    PriorSession? Prior)
{
    public decimal? Band(int k) => Vwap is { } v && VwapStdDev is { } sd ? v + k * sd : null;
}

/// <summary>
/// RTH-anchored VWAP and volume profile, opening ranges, overnight range and the prior RTH summary.
/// Not thread-safe: owned by MarketDataPipeline.
/// </summary>
public sealed class SessionProfile
{
    private readonly SessionClock _clock;
    private readonly DataSettings _s;
    private readonly Dictionary<decimal, decimal> _rthVolume = new();
    private DateOnly? _date;
    private decimal _sumV, _sumPV;
    private double _sumP2V;
    private decimal? _rthHigh, _rthLow, _rthClose, _onHigh, _onLow, _or5High, _or5Low, _or15High, _or15Low;

    public SessionProfile(SessionClock clock, DataSettings settings)
    {
        _clock = clock;
        _s = settings;
    }

    public PriorSession? Prior { get; private set; }

    public void OnTrade(Trade t)
    {
        var tradingDate = _clock.TradingDate(t.Utc);
        if (_date != tradingDate) Roll(tradingDate);
        var tod = _clock.ToEt(t.Utc).TimeOfDay;
        if (tod >= _s.RthOpenEt && tod < _s.RthCloseEt) AddRth(t, (tod - _s.RthOpenEt).TotalMinutes);
        else if (tod < _s.RthOpenEt || tod >= _clock.Settings.TradingDayRollEt)
        {
            _onHigh = Max(_onHigh, t.Price);
            _onLow = Min(_onLow, t.Price);
        }
    }

    public SessionLevels Snapshot()
    {
        decimal? vwap = _sumV > 0 ? _sumPV / _sumV : null;
        decimal? sd = null;
        if (vwap is { } v)
        {
            var variance = _sumP2V / (double)_sumV - (double)v * (double)v;
            sd = (decimal)Math.Sqrt(Math.Max(0, variance));
        }
        return new SessionLevels(_date, vwap, sd, _onHigh, _onLow, _or5High, _or5Low, _or15High, _or15Low,
            _rthHigh, _rthLow, ValueArea.Compute(_rthVolume, _s.ValueAreaPercent), Prior);
    }

    private void AddRth(Trade t, double minutesSinceOpen)
    {
        _rthVolume[t.Price] = (_rthVolume.TryGetValue(t.Price, out var v) ? v : 0m) + t.Size;
        _sumV += t.Size;
        _sumPV += t.Price * t.Size;
        _sumP2V += (double)t.Price * (double)t.Price * (double)t.Size;
        _rthHigh = Max(_rthHigh, t.Price);
        _rthLow = Min(_rthLow, t.Price);
        _rthClose = t.Price;
        if (minutesSinceOpen < _s.OpeningRangeShortMinutes) { _or5High = Max(_or5High, t.Price); _or5Low = Min(_or5Low, t.Price); }
        if (minutesSinceOpen < _s.OpeningRangeLongMinutes) { _or15High = Max(_or15High, t.Price); _or15Low = Min(_or15Low, t.Price); }
    }

    private void Roll(DateOnly tradingDate)
    {
        if (_date is { } prev && _sumV > 0 && ValueArea.Compute(_rthVolume, _s.ValueAreaPercent) is { } va)
            Prior = new PriorSession(prev, _rthHigh!.Value, _rthLow!.Value, _rthClose!.Value, va);
        _date = tradingDate;
        _rthVolume.Clear();
        _sumV = 0m;
        _sumPV = 0m;
        _sumP2V = 0;
        _rthHigh = _rthLow = _rthClose = _onHigh = _onLow = _or5High = _or5Low = _or15High = _or15Low = null;
    }

    private static decimal Max(decimal? a, decimal b) => a is { } x ? Math.Max(x, b) : b;
    private static decimal Min(decimal? a, decimal b) => a is { } x ? Math.Min(x, b) : b;
}
