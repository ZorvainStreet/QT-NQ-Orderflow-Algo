using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class SettingsValidatorTests
{
    private static string? Check(AccountRules? a = null, RiskSettings? r = null, SessionSettings? s = null) =>
        SettingsValidator.FirstError(a ?? new AccountRules(), r ?? new RiskSettings(), s ?? SessionSettings.Default());

    [Fact]
    public void Defaults_AreValid() => Assert.Null(Check());

    [Fact]
    public void TierBNotBelowTierA_IsRejected() =>
        Assert.Contains("TierBMinHeadroom", Check(r: new RiskSettings(TierAMinHeadroom: 600m, TierBMinHeadroom: 600m)));

    [Fact]
    public void HaltNotBelowTierB_IsRejected() =>
        Assert.Contains("HaltHeadroomUsd", Check(r: new RiskSettings(HaltHeadroomUsd: 600m)));

    [Fact]
    public void NegativeMoneyCap_IsRejected() =>
        Assert.Contains("MaxRiskTierA", Check(r: new RiskSettings(MaxRiskTierA: -1m)));

    [Fact]
    public void NegativeAccountLimit_IsRejected() =>
        Assert.Contains("MllDistanceUsd", Check(a: new AccountRules(MllDistanceUsd: -5m)));

    [Theory]
    [InlineData(0)]
    [InlineData(100.5)]
    [InlineData(-1)]
    public void MaxRiskPercent_MustBeInZeroExclusiveToHundred(double v) =>
        Assert.Contains("MaxRiskPercentOfHeadroom", Check(r: new RiskSettings(MaxRiskPercentOfHeadroom: (decimal)v)));

    [Fact]
    public void MaxRiskPercent_HundredIsAllowed() =>
        Assert.Null(Check(r: new RiskSettings(MaxRiskPercentOfHeadroom: 100m)));

    [Fact]
    public void OtherPercents_OutOfRange_AreRejected()
    {
        Assert.Contains("GivebackPercent", Check(r: new RiskSettings(GivebackPercent: 101m)));
        Assert.Contains("MicroscalpHaltPercent", Check(r: new RiskSettings(MicroscalpHaltPercent: -1m)));
        Assert.Contains("ConsistencyCapPercent", Check(a: new AccountRules(ConsistencyCapPercent: 150m)));
    }

    [Fact]
    public void EnabledWindowStartAfterEnd_IsRejected()
    {
        var s = SessionSettings.Default() with
        {
            Windows = new[] { new SessionWindow("NY_AM_KILLZONE", new(12, 0, 0), new(11, 0, 0), true, 0) },
        };
        Assert.Contains("NY_AM_KILLZONE", Check(s: s));
    }

    [Fact]
    public void DisabledWindowWithBadOrder_IsIgnored()
    {
        var s = SessionSettings.Default() with
        {
            Windows = new[] { new SessionWindow("LONDON_OPEN", new(12, 0, 0), new(11, 0, 0), false, 0) },
        };
        Assert.Null(Check(s: s));
    }

    [Fact]
    public void FlattenNotBeforeRoll_IsRejected() =>
        Assert.Contains("FlattenTimeEt", Check(s: SessionSettings.Default() with { FlattenTimeEt = new(18, 0, 0) }));

    [Fact]
    public void ParseTimeCsv_ParsesAndTrims()
    {
        Assert.True(SettingsValidator.TryParseTimeCsv(" 08:30, 10:00 ", out var t));
        Assert.Equal(new[] { new TimeSpan(8, 30, 0), new TimeSpan(10, 0, 0) }, t);
    }

    [Theory]
    [InlineData("8:30x")]
    [InlineData("08:30,,10:00")]
    [InlineData("25:00")]
    public void ParseTimeCsv_BadValue_Fails(string csv) => Assert.False(SettingsValidator.TryParseTimeCsv(csv, out _));

    [Fact]
    public void ParseTimeCsv_Blank_YieldsEmptyList()
    {
        Assert.True(SettingsValidator.TryParseTimeCsv("  ", out var t));
        Assert.Empty(t);
    }
}
