namespace QT_MNQ_Orderflow_Algo.Data;

public sealed record Bucket(DateTime StartUtc, decimal Volume, decimal Delta, int Trades);

/// <summary>Fixed-length time buckets for baselines. Empty intervals are zero-filled so quiet periods count.</summary>
public sealed class BucketSeries
{
    private readonly TimeSpan _length;
    private readonly int _capacity;
    private readonly Queue<Bucket> _closed = new();
    private DateTime? _start;
    private decimal _volume, _delta;
    private int _trades;

    public BucketSeries(TimeSpan length, int capacity)
    {
        if (length <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(length));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _length = length;
        _capacity = capacity;
    }

    public IReadOnlyList<Bucket> Closed => _closed.ToArray();
    public decimal MedianVolume => Stats.Median(_closed.Select(b => b.Volume).ToList());

    public void Add(Trade t)
    {
        if (_start is { } current && t.Utc < current) return; // stale: never book into the current bucket
        AdvanceTo(t.Utc);
        _volume += t.Size;
        _delta += t.IsBuy ? t.Size : -t.Size;
        _trades++;
    }

    public void AdvanceTo(DateTime utc)
    {
        var start = FootprintBuilder.BucketStart(utc, _length);
        if (_start is null) { _start = start; return; }
        if (start <= _start.Value) return;
        Push(new Bucket(_start.Value, _volume, _delta, _trades));
        var empty = (int)Math.Min(_capacity, (start - _start.Value).Ticks / _length.Ticks - 1);
        for (int i = empty; i >= 1; i--) Push(new Bucket(start - TimeSpan.FromTicks(_length.Ticks * i), 0m, 0m, 0));
        _start = start;
        _volume = 0m;
        _delta = 0m;
        _trades = 0;
    }

    private void Push(Bucket b)
    {
        _closed.Enqueue(b);
        while (_closed.Count > _capacity) _closed.Dequeue();
    }
}
