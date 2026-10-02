namespace QT_MNQ_Orderflow_Algo.Data;

/// <summary>Rolling window of the last N seconds of trades. Not thread-safe: owned by MarketDataPipeline.</summary>
public sealed class TapeWindow
{
    private readonly Queue<Trade> _trades = new();
    private readonly TimeSpan _length;

    public TapeWindow(TimeSpan length) => _length = length;

    public decimal BuyVolume { get; private set; }
    public decimal SellVolume { get; private set; }
    public decimal Volume => BuyVolume + SellVolume;
    public decimal Delta => BuyVolume - SellVolume;
    public int Count => _trades.Count;
    public IReadOnlyList<Trade> Trades => _trades.ToArray();

    public void Add(Trade t)
    {
        _trades.Enqueue(t);
        if (t.IsBuy) BuyVolume += t.Size; else SellVolume += t.Size;
        Trim(t.Utc);
    }

    public void Trim(DateTime nowUtc)
    {
        while (_trades.Count > 0 && nowUtc - _trades.Peek().Utc >= _length)
        {
            var old = _trades.Dequeue();
            if (old.IsBuy) BuyVolume -= old.Size; else SellVolume -= old.Size;
        }
    }
}
