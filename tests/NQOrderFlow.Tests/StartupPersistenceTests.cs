using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using QT_MNQ_Orderflow_Algo.Persistence;
using Xunit;

public sealed class StartupPersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"nqstate-{Guid.NewGuid():N}", "state.json");

    private static LucidRiskGuard FreshGuard()
    {
        var clock = new SessionClock(SessionSettings.Default());
        return LucidRiskGuard.ForFlexEvaluation(new AccountRules(), new RiskSettings(), clock,
            NewsBlackout.Load(null, clock, 2, 3, new NullLogSink()), new NullLogSink());
    }

    [Fact]
    public void FreshGuard_AfterEnsureTradingDay_SavesStateThatLoadsCleanly()
    {
        var g = FreshGuard();
        g.EnsureTradingDay(Now);
        var path = TempPath();

        new StateStore(path).Save(new PersistedState(PersistedState.CurrentSchemaVersion, g.Eval, g.Day, null));
        var r = new StateStore(path).Load();

        Assert.Null(r.Error);
        Assert.Equal(new DateOnly(2026, 10, 1), r.State!.Day.Date);
    }

    [Fact]
    public void FreshGuard_WithoutEnsureTradingDay_HasDefaultDate_WhichLoadRejects()
    {
        var g = FreshGuard();
        var path = TempPath();

        Assert.Equal(default, g.Day.Date);
        new StateStore(path).Save(new PersistedState(PersistedState.CurrentSchemaVersion, g.Eval, g.Day, null));
        var r = new StateStore(path).Load();

        Assert.NotNull(r.Error);
        Assert.Contains("Day.Date", r.Error);
    }

    [Fact]
    public void EnsureTradingDay_IsForwardOnly()
    {
        var g = FreshGuard();
        g.EnsureTradingDay(Now);
        g.EnsureTradingDay(Now.AddDays(-3));
        Assert.Equal(new DateOnly(2026, 10, 1), g.Day.Date);
    }
}
