namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record PriceLevel(decimal Price, decimal BidVolume, decimal AskVolume)
{
    public decimal Total => BidVolume + AskVolume;
}

/// <summary>
/// One footprint bar. Mutable while it is the builder's current bar (hot path); never mutated after it closes.
/// Not thread-safe: reachable only through MarketDataPipeline.
/// </summary>
public sealed class FootprintBar
{
    private readonly SortedDictionary<decimal, (decimal Bid, decimal Ask)> _levels = new();

    public FootprintBar(DateTime startUtc, decimal tickSize)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        StartUtc = startUtc;
        TickSize = tickSize;
    }

    public DateTime StartUtc { get; }
    public decimal TickSize { get; }
    public decimal Open { get; private set; }
    public decimal High { get; private set; }
    public decimal Low { get; private set; }
    public decimal Close { get; private set; }
    public decimal TotalVolume { get; private set; }
    public decimal Delta { get; private set; }
    public decimal MaxDelta { get; private set; }
    public decimal MinDelta { get; private set; }
    public int TradeCount { get; private set; }

    public void Add(Trade t)
    {
        if (TradeCount == 0) Open = High = Low = t.Price;
        High = Math.Max(High, t.Price);
        Low = Math.Min(Low, t.Price);
        Close = t.Price;
        var level = _levels.TryGetValue(t.Price, out var v) ? v : (0m, 0m);
        _levels[t.Price] = t.IsBuy ? (level.Item1, level.Item2 + t.Size) : (level.Item1 + t.Size, level.Item2);
        TotalVolume += t.Size;
        Delta += t.IsBuy ? t.Size : -t.Size;
        MaxDelta = TradeCount == 0 ? Delta : Math.Max(MaxDelta, Delta);
        MinDelta = TradeCount == 0 ? Delta : Math.Min(MinDelta, Delta);
        TradeCount++;
    }

    public IReadOnlyList<PriceLevel> Levels => _levels.Select(kv => new PriceLevel(kv.Key, kv.Value.Item1, kv.Value.Item2)).ToList();
    public decimal BidAt(decimal price) => _levels.TryGetValue(price, out var v) ? v.Bid : 0m;
    public decimal AskAt(decimal price) => _levels.TryGetValue(price, out var v) ? v.Ask : 0m;

    public decimal? Poc
    {
        get
        {
            decimal? best = null;
            decimal bestVol = -1m;
            foreach (var (price, v) in _levels)
            {
                if (v.Bid + v.Ask > bestVol) { best = price; bestVol = v.Bid + v.Ask; }
            }
            return best;
        }
    }

    public IReadOnlyList<decimal> BuyImbalances(decimal ratio, decimal minVolume) =>
        _levels.Where(kv => kv.Value.Ask > 0 && kv.Value.Ask >= minVolume && kv.Value.Ask >= ratio * BidAt(kv.Key - TickSize))
               .Select(kv => kv.Key).ToList();

    public IReadOnlyList<decimal> SellImbalances(decimal ratio, decimal minVolume) =>
        _levels.Where(kv => kv.Value.Bid > 0 && kv.Value.Bid >= minVolume && kv.Value.Bid >= ratio * AskAt(kv.Key + TickSize))
               .Select(kv => kv.Key).ToList();

    public static int LongestStack(IReadOnlyList<decimal> prices, decimal tickSize)
    {
        if (prices.Count == 0) return 0;
        var sorted = prices.Distinct().OrderBy(p => p).ToArray();
        if (sorted.Length == 0) return 0;
        int best = 1, run = 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            run = sorted[i] - sorted[i - 1] == tickSize ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        return best;
    }

    public bool UnfinishedHigh => TradeCount > 0 && BidAt(High) > 0 && AskAt(High) > 0;
    public bool UnfinishedLow => TradeCount > 0 && BidAt(Low) > 0 && AskAt(Low) > 0;
}
