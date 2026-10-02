namespace QT_MNQ_Orderflow_Algo.Config;

/// <summary>Phase 2 data and order-flow tunables. Every field is an [InputParameter] in the strategy.</summary>
public sealed record DataSettings(
    int BadTickMaxTicks = 40,
    int BadTickConfirmCount = 3,
    bool DropIdenticalPrints = false,
    double FallbackWarnPercent = 5,
    int MaxTickAgeSeconds = 3,
    int BarHistoryCapacity = 500,
    decimal ImbalanceRatio = 3.0m,
    decimal ImbalanceMinVolume = 12m,
    int StackedLevels = 3,
    int TapeShortSeconds = 5,
    int TapeMidSeconds = 20,
    int TapeLongSeconds = 60,
    int BaselineMinutes = 30,
    decimal AbsorptionVolMultiple = 2.0m,
    int AbsorptionMaxProgressTicks = 3,
    decimal AbsorptionDominancePercent = 65m,
    decimal DeltaFlipMinMultiple = 0.5m,
    double VelocityZLimit = 3.0,
    decimal ValueAreaPercent = 70m,
    int OpeningRangeShortMinutes = 5,
    int OpeningRangeLongMinutes = 15,
    int MedianBarLookback = 30,
    int MaxFutureSkewSeconds = 5,
    int BarCloseGraceMs = 1500)
{
    public TimeSpan RthOpenEt { get; init; } = new(9, 30, 0);
    public TimeSpan RthCloseEt { get; init; } = new(16, 0, 0);
}
