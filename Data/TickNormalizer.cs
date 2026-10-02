using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Data;

/// <summary>
/// Filters and classifies raw prints. The exchange aggressor flag is the truth; the tick rule is the fallback.
/// Not thread-safe: owned by MarketDataPipeline under its lock.
/// </summary>
public sealed class TickNormalizer
{
    private readonly DataSettings _s;
    private RawQuote? _quote;
    private bool _quoteChangedSinceTrade;
    private DateTime _lastTime = DateTime.MinValue;
    private decimal? _lastPrice;
    private bool _lastIsBuy = true;
    private RawTrade? _lastRaw;
    private int _jumpDrops;
    private decimal? _jumpCandidate;

    public TickNormalizer(DataSettings settings, decimal tickSize)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be positive.");
        _s = settings;
        TickSize = tickSize;
    }

    public decimal TickSize { get; }
    public long Accepted { get; private set; }
    public long Dropped { get; private set; }
    public long Fallbacks { get; private set; }
    public double FallbackPercent => Accepted == 0 ? 0 : 100.0 * Fallbacks / Accepted;

    public decimal RoundToTick(decimal price) => Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;

    public void OnQuote(RawQuote q)
    {
        if (q.Bid <= 0 || q.Ask <= 0 || q.Bid > q.Ask) return;
        _quote = q with { Bid = RoundToTick(q.Bid), Ask = RoundToTick(q.Ask) };
        _quoteChangedSinceTrade = true;
    }

    public Trade? OnTrade(RawTrade raw)
    {
        if (raw.Size <= 0 || raw.Price <= 0 || raw.Utc < _lastTime) return Drop();
        if (_s.DropIdenticalPrints && _lastRaw == raw) return Drop();
        var price = RoundToTick(raw.Price);
        if (IsUnconfirmedJump(price)) return Drop();

        var (isBuy, fallback) = Classify(raw.Side, price);
        _lastTime = raw.Utc;
        _lastPrice = price;
        _lastIsBuy = isBuy;
        _lastRaw = raw;
        _quoteChangedSinceTrade = false;
        _jumpDrops = 0;
        _jumpCandidate = null;
        Accepted++;
        if (fallback) Fallbacks++;
        return new Trade(raw.Utc, price, raw.Size, isBuy, fallback);
    }

    private bool IsUnconfirmedJump(decimal price)
    {
        if (_lastPrice is not { } last) return false;
        if (Math.Abs(price - last) <= _s.BadTickMaxTicks * TickSize) return false;

        // It's a jump. Check if quote corroborates it.
        if (_quoteChangedSinceTrade && _quote is { } q)
        {
            var maxDistance = _s.BadTickMaxTicks * TickSize;
            if (Math.Abs(price - q.Ask) <= maxDistance || Math.Abs(price - q.Bid) <= maxDistance)
            {
                return false;
            }
        }

        // Jump needs confirmation. Track a candidate price.
        if (_jumpCandidate is not { } candidate)
        {
            _jumpCandidate = price;
            _jumpDrops = 1;
        }
        else if (Math.Abs(price - candidate) <= _s.BadTickMaxTicks * TickSize)
        {
            _jumpDrops++;
        }
        else
        {
            // Different jump price, restart.
            _jumpCandidate = price;
            _jumpDrops = 1;
        }

        return _jumpDrops < _s.BadTickConfirmCount;
    }

    private (bool IsBuy, bool Fallback) Classify(Aggressor side, decimal price)
    {
        if (side == Aggressor.Buy) return (true, false);
        if (side == Aggressor.Sell) return (false, false);
        if (_quote is { } q)
        {
            if (price >= q.Ask) return (true, true);
            if (price <= q.Bid) return (false, true);
        }
        if (_lastPrice is { } last && price != last) return (price > last, true);
        return (_lastIsBuy, true);
    }

    private Trade? Drop()
    {
        Dropped++;
        return null;
    }
}
