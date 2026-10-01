using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Persistence;
using Xunit;

public sealed class StateStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"nqstate-{Guid.NewGuid():N}", "state.json");

    [Fact]
    public void Missing_IsFresh()
    {
        var r = new StateStore(TempPath()).Load();
        Assert.True(r.Fresh);
        Assert.Null(r.Error);
    }

    [Fact]
    public void RoundTrip_PreservesRiskMemory()
    {
        var a = new AccountRules();
        var path = TempPath();
        var eval = EvaluationTracker.Start(a)
            .WithTradeClosed(new DateOnly(2026, 10, 28), 300m, 4, new RiskSettings(), a)
            .WithEndOfDay(new DateOnly(2026, 10, 28), 25300m, a);
        var day = DailyRiskState.New(new DateOnly(2026, 10, 29)) with { HaltReason = "x" };
        new StateStore(path).Save(new PersistedState(1, eval, day, new DateOnly(2026, 10, 28)));

        var loaded = new StateStore(path).Load().State!;
        Assert.Equal(24300m, loaded.Eval.MllFloor);
        Assert.Equal(300m, loaded.Eval.DailyPnl[new DateOnly(2026, 10, 28)]);
        Assert.Equal(100m, loaded.Eval.MicroscalpPercent);
        Assert.Equal("x", loaded.Day.HaltReason);
    }

    [Fact]
    public void Corrupt_ReturnsError_AndPreservesFile()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var r = new StateStore(path).Load();
        Assert.NotNull(r.Error);
        Assert.Null(r.State);
        Assert.False(r.Fresh);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "state.json.corrupt-*"));
    }

    [Fact]
    public void MissingMembers_ReturnsError_AndPreservesFile()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"SchemaVersion\":1}");
        var r = new StateStore(path).Load();
        Assert.NotNull(r.Error);
        Assert.Null(r.State);
        Assert.False(r.Fresh);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "state.json.corrupt-*"));
    }

    [Fact]
    public void UnsupportedSchemaVersion_ReturnsError()
    {
        var a = new AccountRules();
        var path = TempPath();
        var state = new PersistedState(99, EvaluationTracker.Start(a), DailyRiskState.New(new DateOnly(2026, 10, 29)), null);
        new StateStore(path).Save(state);
        var r = new StateStore(path).Load();
        Assert.NotNull(r.Error);
        Assert.Null(r.State);
        Assert.False(r.Fresh);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "state.json.corrupt-*"));
    }
}
