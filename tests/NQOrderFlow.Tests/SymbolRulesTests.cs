using QT_MNQ_Orderflow_Algo.Compliance;
using Xunit;

public sealed class SymbolRulesTests
{
    [Theory]
    [InlineData("NQZ6", "NQ")]
    [InlineData("NQZ26", "NQ")]
    [InlineData("MNQZ6", "MNQ")]
    [InlineData("/NQ", "NQ")]
    [InlineData("NQ", "NQ")]
    public void ExtractRoot_StripsMonthYearCode(string name, string expected)
        => Assert.Equal(expected, SymbolRules.ExtractRoot(name));

    [Fact]
    public void IsAllowed_IsExactMatch_NotPrefix()
    {
        var nqOnly = SymbolRules.ParseAllowed("NQ");
        Assert.True(SymbolRules.IsAllowed("NQ", nqOnly));
        Assert.False(SymbolRules.IsAllowed("MNQ", nqOnly));
        Assert.False(SymbolRules.IsAllowed("NQ", SymbolRules.ParseAllowed("MNQ")));
    }

    [Fact]
    public void ParseAllowed_TrimsAndUppercases()
        => Assert.True(SymbolRules.IsAllowed("MNQ", SymbolRules.ParseAllowed(" nq , mnq ")));
}
