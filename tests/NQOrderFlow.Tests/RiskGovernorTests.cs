using QT_MNQ_Orderflow_Algo.Compliance;
using QT_MNQ_Orderflow_Algo.Config;
using Xunit;

public sealed class RiskGovernorTests
{
    private readonly RiskGovernor _g = new(new RiskSettings());

    [Theory]
    [InlineData(1000, RiskTier.A)] [InlineData(900, RiskTier.A)]
    [InlineData(899.99, RiskTier.B)] [InlineData(600, RiskTier.B)]
    [InlineData(599, RiskTier.C)] [InlineData(350, RiskTier.C)]
    [InlineData(349.99, RiskTier.Halt)] [InlineData(-5, RiskTier.Halt)]
    public void Tiers_MatchSpecBoundaries(double headroom, RiskTier expected)
        => Assert.Equal(expected, _g.TierFor((decimal)headroom));

    [Fact]
    public void RiskUsd_IncludesRoundTripCommissionAndSlippage()
    {
        var r = _g.CheckTrade(10m, 20m, 1000m);
        Assert.Equal(210m, r.RiskUsd);  // 200 + 2 x 2.50 + 5
        Assert.True(r.Ok);
    }

    [Fact]
    public void TierCap_IsInclusive()
    {
        Assert.True(_g.CheckTrade(12m, 20m, 1000m).Ok);      // 250 == cap
        Assert.False(_g.CheckTrade(12.25m, 20m, 1000m).Ok);  // 255 > cap
    }

    [Fact]
    public void Rejects_AboveQuarterOfHeadroom()
    {
        var r = _g.CheckTrade(8m, 20m, 620m);  // 170 <= B cap 200, but > 25% of 620 = 155
        Assert.False(r.Ok);
        Assert.Contains("headroom", r.Reason);
    }

    [Fact]
    public void TierC_RejectsStopsWiderThan7_5Points()
        => Assert.False(_g.CheckTrade(7.75m, 2m, 500m).Ok);

    [Fact]
    public void Halt_RejectsEverything()
        => Assert.False(_g.CheckTrade(1m, 2m, 300m).Ok);
}
