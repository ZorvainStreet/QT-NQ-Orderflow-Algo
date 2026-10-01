using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class EvaluationTrackerTests
{
    private static readonly AccountRules A = new();
    private static readonly RiskSettings R = new();
    private static DateOnly D(int d) => new(2026, 10, d);

    [Fact]
    public void Floor_StartsAtInitialMinusMll() => Assert.Equal(24000m, EvaluationTracker.Start(A).MllFloor);

    [Fact]
    public void Floor_RatchetsUpOnly_AndLocks()
    {
        var t = EvaluationTracker.Start(A).WithEndOfDay(D(26), 25400m, A).WithEndOfDay(D(27), 25100m, A);
        Assert.Equal(24400m, t.MllFloor);
        Assert.Equal(25000m, t.WithEndOfDay(D(28), 26500m, A).MllFloor);
    }

    [Fact]
    public void Breach_WhenEodCloseAtOrBelowFloor()
        => Assert.True(EvaluationTracker.Start(A).WithEndOfDay(D(26), 24000m, A).Breached);

    [Fact]
    public void Consistency_IsBestDayOverTotal()
    {
        var t = EvaluationTracker.Start(A)
            .WithTradeClosed(D(26), 400m, 60, R, A).WithTradeClosed(D(27), 600m, 60, R, A)
            .WithTradeClosed(D(28), -100m, 60, R, A);
        Assert.Equal(900m, t.TotalProfit);
        Assert.Equal(600m / 900m, t.ConsistencyRatio);
        Assert.Equal(3, t.TradingDays);
    }

    [Fact]
    public void EarlyStop_UsesTargetAsMinimumDenominator_SoFirstWinDoesNotHalt()
    {
        var t = EvaluationTracker.Start(A).WithTradeClosed(D(26), 200m, 60, R, A);
        Assert.False(t.ConsistencyEarlyStop(D(26), A));                                         // 200 < 562.5
        Assert.True(t.WithTradeClosed(D(26), 400m, 60, R, A).ConsistencyEarlyStop(D(26), A));  // 600 >= 562.5
    }

    [Fact]
    public void Target_NeedsProfitDaysAndConsistency()
    {
        Assert.False(EvaluationTracker.Start(A).WithTradeClosed(D(26), 1300m, 60, R, A).TargetConditionsMet(A));
        var ok = EvaluationTracker.Start(A)
            .WithTradeClosed(D(26), 450m, 60, R, A).WithTradeClosed(D(27), 450m, 60, R, A)
            .WithTradeClosed(D(28), 400m, 60, R, A);
        Assert.True(ok.TargetConditionsMet(A));
        Assert.True(ok.TargetReached);
    }

    [Fact]
    public void Microscalp_IsShareOfWinningProfitFromHoldsAtOrUnder5s()
    {
        var t = EvaluationTracker.Start(A)
            .WithTradeClosed(D(26), 100m, 4.0, R, A).WithTradeClosed(D(26), 300m, 30, R, A)
            .WithTradeClosed(D(26), -50m, 2, R, A);
        Assert.Equal(25m, t.MicroscalpPercent);
    }
}
