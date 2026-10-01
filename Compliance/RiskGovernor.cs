using QT_MNQ_Orderflow_Algo.Config;

namespace QT_MNQ_Orderflow_Algo.Compliance;

public enum RiskTier { A, B, C, Halt }

public sealed record RiskCheck(bool Ok, decimal RiskUsd, RiskTier Tier, string Reason);

/// <summary>Size is fixed, so risk is controlled by skipping wide stops and tightening as headroom shrinks.</summary>
public sealed class RiskGovernor
{
    private readonly RiskSettings _s;
    public RiskGovernor(RiskSettings settings) => _s = settings;

    public RiskTier TierFor(decimal headroom) =>
        headroom >= _s.TierAMinHeadroom ? RiskTier.A :
        headroom >= _s.TierBMinHeadroom ? RiskTier.B :
        headroom >= _s.HaltHeadroomUsd ? RiskTier.C : RiskTier.Halt;

    public int ScoreThresholdAdd(RiskTier t) => t switch
    {
        RiskTier.B => _s.ScoreAddTierB,
        RiskTier.C => _s.ScoreAddTierC,
        _ => 0,
    };

    public decimal MaxRiskUsd(RiskTier t) => t switch
    {
        RiskTier.A => _s.MaxRiskTierA,
        RiskTier.B => _s.MaxRiskTierB,
        RiskTier.C => _s.MaxRiskTierC,
        _ => 0m,
    };

    public RiskCheck CheckTrade(decimal stopPoints, decimal pointValue, decimal headroomUsd)
    {
        var tier = TierFor(headroomUsd);
        var risk = stopPoints * pointValue + 2 * _s.CommissionPerSideUsd + _s.SlippageUsd;
        if (tier == RiskTier.Halt)
            return new(false, risk, tier, $"Tier HALT: headroom {headroomUsd:F0} < {_s.HaltHeadroomUsd:F0}");
        if (tier == RiskTier.C && stopPoints > _s.MaxStopPointsTierC)
            return new(false, risk, tier, $"Tier C: stop {stopPoints} pts > {_s.MaxStopPointsTierC}");
        if (risk > MaxRiskUsd(tier))
            return new(false, risk, tier, $"Risk {risk:F2} > tier {tier} cap {MaxRiskUsd(tier):F0}");
        var pctCap = headroomUsd * _s.MaxRiskPercentOfHeadroom / 100m;
        if (risk > pctCap)
            return new(false, risk, tier, $"Risk {risk:F2} > {_s.MaxRiskPercentOfHeadroom}% of headroom ({pctCap:F2})");
        return new(true, risk, tier, "OK");
    }
}
