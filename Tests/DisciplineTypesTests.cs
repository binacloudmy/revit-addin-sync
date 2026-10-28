using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class DisciplineTypesTests
    {
        [Fact]
        public void MechanicalLabel_IsMechanical() => Assert.Equal("Mechanical", DisciplineTypes.ToLabel("Mechanical"));

        [Theory]
        [InlineData("HVAC")]
        [InlineData("hvac")]
        [InlineData("Mechanical")]
        public void ToApiValue_MapsToMechanical(string input) => Assert.Equal("Mechanical", DisciplineTypes.ToApiValue(input));

        [Theory]
        [InlineData("HVAC_Level1.rvt")]
        [InlineData("Mechanical_Level1.rvt")]
        public void FromFileName_AcceptsHvacAndMechanical(string name) => Assert.Equal("Mechanical", DisciplineTypes.FromFileName(name));
    }
}
