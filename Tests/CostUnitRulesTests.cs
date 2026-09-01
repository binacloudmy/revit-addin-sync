using RevitWebAppSync.Services;
using Xunit;

// Twin-parity contract: these cases mirror bina-ai tests/test_cost_units.py
// (TestNormalizeUnit / TestUnitCompatible). If a case is added there, add it
// here — CostUnitRules.cs must stay behaviorally identical to cost_units.py.
public class CostUnitRulesTests
{
    [Theory]
    [InlineData("m²", "m2")]
    [InlineData("m2", "m2")]
    [InlineData("SQM", "m2")]
    [InlineData(" m² ", "m2")]
    [InlineData("m³", "m3")]
    [InlineData("CU.M", "m3")]
    [InlineData("lm", "m")]
    [InlineData("mtr", "m")]
    [InlineData("no.", "unit")]
    [InlineData("Each", "unit")]
    [InlineData("kg", "kg")]
    [InlineData("MT", "tonne")]
    [InlineData("ton", "tonne")]
    public void NormalizeMapsAliasesToCanonical(string raw, string expected)
    {
        Assert.Equal(expected, CostUnitRules.Normalize(raw));
    }

    [Theory]
    [InlineData("bag")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeUnknownReturnsNull(string raw)
    {
        Assert.Null(CostUnitRules.Normalize(raw));
    }

    [Fact]
    public void IdenticalCanonicalUnitsCompatible()
    {
        Assert.True(CostUnitRules.Compatible("m²", "m2"));
        Assert.True(CostUnitRules.Compatible("unit", "no."));
        Assert.True(CostUnitRules.Compatible("lm", "m"));
    }

    [Fact]
    public void CrossUnitIncompatible()
    {
        Assert.False(CostUnitRules.Compatible("m²", "m"));
        Assert.False(CostUnitRules.Compatible("unit", "m2"));
        Assert.False(CostUnitRules.Compatible("kg", "tonne"));
    }

    [Fact]
    public void AreaVolumeRequiresThickness()
    {
        Assert.False(CostUnitRules.Compatible("m³", "m²"));
        Assert.False(CostUnitRules.Compatible("m³", "m²", 0));
        Assert.True(CostUnitRules.Compatible("m³", "m²", 200));
        Assert.True(CostUnitRules.Compatible("m²", "m³", 200));
    }

    [Fact]
    public void UnknownUnitsFallBackToRawEquality()
    {
        Assert.True(CostUnitRules.Compatible("bag", "bag"));
        Assert.False(CostUnitRules.Compatible("bag", "roll"));
    }
}
