using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class DailyRiskStateTests
{
    private static readonly RiskSettings S = new();
    private static readonly DateTime T0 = new(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
    private static DailyRiskState Fresh() => DailyRiskState.New(new DateOnly(2026, 10, 29));

    [Fact]
    public void Loss_StartsTenMinuteCooldown()
    {
        var s = Fresh().WithTradeClosed(-100m, T0, S);
        Assert.False(s.CanEnter(T0.AddMinutes(9), false, S, out var r));
        Assert.Contains("cooldown", r);
        Assert.True(s.CanEnter(T0.AddMinutes(10), false, S, out _));
    }

    [Fact]
    public void Win_StartsThreeMinuteCooldown()
    {
        var s = Fresh().WithTradeClosed(50m, T0, S);
        Assert.False(s.CanEnter(T0.AddMinutes(2), false, S, out _));
        Assert.True(s.CanEnter(T0.AddMinutes(3), false, S, out _));
    }

    [Fact]
    public void DailyLoss_Halts()
    {
        var s = Fresh().WithTradeClosed(-200m, T0, S).WithTradeClosed(-160m, T0.AddMinutes(20), S);
        Assert.NotNull(s.HaltReason);
        Assert.False(s.CanEnter(T0.AddHours(2), true, S, out _));
    }

    [Fact]
    public void TwoLossStreak_Pauses30Min_ThenOneAPlusOnly()
    {
        var s = Fresh().WithTradeClosed(-50m, T0, S).WithTradeClosed(-50m, T0.AddMinutes(15), S);
        var resume = T0.AddMinutes(45);
        Assert.False(s.CanEnter(resume.AddMinutes(-1), true, S, out _));
        Assert.False(s.CanEnter(resume, false, S, out var r));
        Assert.Contains("A+", r);
        Assert.True(s.CanEnter(resume, true, S, out _));
        var s2 = s.WithTradeClosed(10m, resume.AddMinutes(5), S);
        Assert.False(s2.CanEnter(resume.AddHours(1), true, S, out _));
    }

    [Fact]
    public void ProfitCap_Halts() => Assert.NotNull(Fresh().WithTradeClosed(460m, T0, S).HaltReason);

    [Fact]
    public void Giveback40PercentOfPeakAbove200_Halts()
        => Assert.NotNull(Fresh().WithTradeClosed(300m, T0, S).WithTradeClosed(-120m, T0.AddMinutes(10), S).HaltReason);

    [Fact]
    public void MaxTradesPerDay_Blocks()
    {
        var s = Fresh();
        for (int i = 0; i < 4; i++) s = s.WithTradeClosed(10m, T0.AddMinutes(i * 10), S);
        Assert.False(s.CanEnter(T0.AddHours(3), true, S, out var r));
        Assert.Contains("trades", r);
    }

    [Fact]
    public void WithTradeClosed_DoesNotMutateOriginal()
    {
        var a = Fresh();
        _ = a.WithTradeClosed(-100m, T0, S);
        Assert.Equal(0, a.Trades);
    }
}
