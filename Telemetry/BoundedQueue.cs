using System.Collections.Concurrent;

namespace QT_MNQ_Orderflow_Algo.Telemetry;

/// <summary>Lock-free bounded queue: producers never block; items beyond capacity are counted and dropped.</summary>
public sealed class BoundedQueue<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly int _capacity;
    private int _count;
    private long _dropped;

    public BoundedQueue(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public int Count => Volatile.Read(ref _count);

    public bool TryEnqueue(T item)
    {
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
            return false;
        }
        _items.Enqueue(item);
        return true;
    }

    public IReadOnlyList<T> Drain(int max)
    {
        var drained = new List<T>();
        while (drained.Count < max && _items.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            drained.Add(item);
        }
        return drained;
    }
}
