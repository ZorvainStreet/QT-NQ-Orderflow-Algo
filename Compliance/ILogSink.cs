namespace QT_MNQ_Orderflow_Algo.Compliance;

public interface ILogSink
{
    void Info(string message);
    void Trading(string message);
    void Error(string message);
}

public sealed class NullLogSink : ILogSink
{
    public void Info(string message) { }
    public void Trading(string message) { }
    public void Error(string message) { }
}
