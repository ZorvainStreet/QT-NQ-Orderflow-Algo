using QT_MNQ_Orderflow_Algo.Telemetry;
using Xunit;

public sealed class BoundedQueueTests
{
    [Fact]
    public void DropsWhenFull_AndDrainsInOrder()
    {
        var q = new BoundedQueue<int>(2);
        Assert.True(q.TryEnqueue(1));
        Assert.True(q.TryEnqueue(2));
        Assert.False(q.TryEnqueue(3));
        Assert.Equal(1, q.Dropped);
        Assert.Equal(new[] { 1 }, q.Drain(1));
        Assert.True(q.TryEnqueue(4));
        Assert.Equal(new[] { 2, 4 }, q.Drain(10));
        Assert.Equal(0, q.Count);
    }
}
