namespace QT_MNQ_Orderflow_Algo.Config;

/// <summary>Lucid account rules. Every value is an input; VERIFY on the Lucid dashboard.</summary>
public sealed record AccountRules(
    decimal InitialBalance = 25000m,
    decimal MllDistanceUsd = 1000m,
    decimal MllLockFloorUsd = 25000m,       // VERIFY: level at which the trailing floor stops rising
    decimal ProfitTargetUsd = 1250m,
    decimal TargetBufferUsd = 25m,
    decimal ConsistencyCapPercent = 50m,
    decimal ConsistencyEarlyStopPercent = 45m,
    int MinTradingDays = 2,
    int MaxContractsAllowed = 1);           // VERIFY

public sealed record SessionWindow(string Name, TimeSpan StartEt, TimeSpan EndEt, bool Enabled, int ScoreAdd);

public sealed record SessionSettings(
    IReadOnlyList<SessionWindow> Windows,
    TimeSpan FlattenTimeEt,
    TimeSpan LucidDeadlineEt,
    TimeSpan TradingDayRollEt,               // 18:00 ET futures session roll
    int NewsBeforeMin,
    int NewsAfterMin,
    IReadOnlyList<TimeSpan> RecurringNewsEt)
{
    public static SessionSettings Default() => new(
        new[]
        {
            new SessionWindow("NY_AM_KILLZONE", new(9, 35, 0), new(11, 45, 0), true, 0),
            new SessionWindow("NY_PM", new(13, 30, 0), new(15, 30, 0), true, 5),
            new SessionWindow("LONDON_OPEN", new(3, 0, 0), new(5, 0, 0), false, 0),
        },
        new(15, 55, 0), new(16, 45, 0), new(18, 0, 0), 2, 3,
        new[] { new TimeSpan(8, 30, 0), new TimeSpan(10, 0, 0) });
}

public sealed record RiskSettings(
    decimal TierAMinHeadroom = 900m, decimal TierBMinHeadroom = 600m, decimal HaltHeadroomUsd = 350m,
    decimal MaxRiskTierA = 250m, decimal MaxRiskTierB = 200m, decimal MaxRiskTierC = 150m,
    decimal MaxStopPointsTierC = 7.5m,
    int ScoreAddTierB = 5, int ScoreAddTierC = 10,
    decimal MaxRiskPercentOfHeadroom = 25m,
    decimal CommissionPerSideUsd = 2.50m,    // VERIFY with Lucid/Rithmic fee schedule
    decimal SlippageUsd = 5m,
    decimal MaxDailyLossUsd = 350m,
    int MaxConsecutiveLosses = 2,
    int StreakPauseMinutes = 30,
    int MaxTradesPerDay = 4,
    decimal DailyProfitCapUsd = 450m,
    decimal GivebackArmUsd = 200m,
    decimal GivebackPercent = 40m,
    int LossCooldownMinutes = 10,
    int WinCooldownMinutes = 3,
    int MicroscalpHoldSeconds = 5,
    decimal MicroscalpWarnPercent = 20m,
    decimal MicroscalpHaltPercent = 35m,
    int SafeModeExceptionCount = 5);
