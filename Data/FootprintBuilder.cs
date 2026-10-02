namespace QT_MNQ_Orderflow_Algo.Data;

/// <summary>Buckets trades into fixed-period footprint bars. Not thread-safe: owned by MarketDataPipeline.</summary>
public sealed class FootprintBuilder
{
    private readonly TimeSpan _period;
    private readonly decimal _tickSize;
    private readonly int _capacity;
    private readonly List<FootprintBar> _closed = new();
    private DateTime? _lastClosedStart;

    public FootprintBuilder(TimeSpan period, decimal tickSize, int capacity)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _period = period;
        _tickSize = tickSize;
        _capacity = capacity;
    }

    public FootprintBar? Current { get; private set; }
    public IReadOnlyList<FootprintBar> Closed => _closed.ToArray();
    public long LateTrades { get; private set; }

    public static DateTime BucketStart(DateTime utc, TimeSpan period) =>
        new(utc.Ticks - utc.Ticks % period.Ticks, DateTimeKind.Utc);

    public FootprintBar? OnTrade(Trade t)
    {
        var start = BucketStart(t.Utc, _period);

        // Reject if trade is from a bucket already closed, or older than current bar
        if (_lastClosedStart.HasValue && start <= _lastClosedStart)
        {
            LateTrades++;
            return null;
        }
        if (Current is not null && start < Current.StartUtc)
        {
            LateTrades++;
            return null;
        }

        FootprintBar? closed = null;
        if (Current is not null && start != Current.StartUtc) closed = CloseCurrent();
        Current ??= new FootprintBar(start, _tickSize);
        Current.Add(t);
        return closed;
    }

    public FootprintBar? CloseIfElapsed(DateTime nowUtc) =>
        Current is not null && nowUtc >= Current.StartUtc + _period ? CloseCurrent() : null;

    private FootprintBar CloseCurrent()
    {
        var bar = Current!;
        _lastClosedStart = bar.StartUtc;
        _closed.Add(bar);
        if (_closed.Count > _capacity) _closed.RemoveAt(0);
        Current = null;
        return bar;
    }
}
