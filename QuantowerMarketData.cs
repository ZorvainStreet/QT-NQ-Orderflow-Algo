using QT_MNQ_Orderflow_Algo.Data;
using TradingPlatform.BusinessLayer;

namespace QT_MNQ_Orderflow_Algo;

/// <summary>The only Quantower-facing market-data code: maps feed objects to pure-core types and loads tick history.</summary>
internal static class QuantowerMarketData
{
    private static readonly TimeSpan BackfillChunk = TimeSpan.FromHours(1);

    public static RawTrade ToRawTrade(Last last) =>
        new(AsUtc(last.Time), (decimal)last.Price, (decimal)last.Size, Map(last.AggressorFlag));

    public static RawQuote ToRawQuote(Quote quote) => new(AsUtc(quote.Time), (decimal)quote.Bid, (decimal)quote.Ask);

    /// <summary>
    /// Feeds tick history oldest-first, one hour at a time to bound memory. Returns the last tick time fed.
    /// 1.146.18: the tick aggregation carries the history type (<c>new HistoryAggregationTick(HistoryType.Last)</c>);
    /// <c>HistoryRequestParameters</c> has no HistoryType of its own. The enumeration order of
    /// <c>HistoricalData</c> is not documented, so each chunk is sorted by time (stable) before feeding.
    /// </summary>
    public static DateTime? Backfill(Symbol symbol, DateTime fromUtc, DateTime toUtc, Action<RawTrade> sink, CancellationToken ct)
    {
        DateTime? last = null;
        for (var start = fromUtc; start < toUtc; start += BackfillChunk)
        {
            ct.ThrowIfCancellationRequested();
            var end = start + BackfillChunk < toUtc ? start + BackfillChunk : toUtc;
            foreach (var trade in LoadChunk(symbol, start, end, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (last is { } prev && trade.Utc < prev) continue; // chunk-boundary overlap: never feed time backwards
                sink(trade);
                last = trade.Utc;
            }
        }
        return last;
    }

    private static List<RawTrade> LoadChunk(Symbol symbol, DateTime start, DateTime end, CancellationToken ct)
    {
        using var history = symbol.GetHistory(new HistoryRequestParameters
        {
            Symbol = symbol,
            FromTime = start,
            ToTime = end,
            Aggregation = new HistoryAggregationTick(HistoryType.Last),
            CancellationToken = ct,
        });
        var trades = new List<RawTrade>();
        if (history is null) return trades;
        foreach (var item in history)
        {
            if (item is not HistoryItemLast h) continue;
            var utc = AsUtc(h.TimeLeft);
            if (utc < start || utc >= end) continue; // keep chunks disjoint: [start, end)
            trades.Add(new RawTrade(utc, (decimal)h.Price, (decimal)h.Volume, Map(h.AggressorFlag)));
        }
        return trades.OrderBy(t => t.Utc).ToList();
    }

    /// <summary>AggressorFlag in 1.146.18 is None/Buy/Sell/NotSet; anything but Buy/Sell is Unknown (tick-rule fallback).</summary>
    private static Aggressor Map(AggressorFlag flag) => flag switch
    {
        AggressorFlag.Buy => Aggressor.Buy,
        AggressorFlag.Sell => Aggressor.Sell,
        _ => Aggressor.Unknown,
    };

    private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
