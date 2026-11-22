#nullable enable
// Hand test 17 Nov (turn c61a1d44): the add-in reported "selected 2" for a code run's selection request and the next read found none.
// The host now reads the selection back right after the set and says so when Revit holds fewer.
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UiSelectionReadBack0127Tests
    {
        [Fact] public void AllHeld_NoWarning() => Assert.Null(UiSelectionReadBack.Warning(2, 2));
        [Fact] public void MoreHeldThanAsked_NoWarning() => Assert.Null(UiSelectionReadBack.Warning(2, 3));
        [Fact] public void NothingAsked_NoWarning() => Assert.Null(UiSelectionReadBack.Warning(0, 0));

        [Fact]
        public void NoneHeld_SaysDoNotReportSelected()
        {
            var w = UiSelectionReadBack.Warning(2, 0)!;
            Assert.Contains("2 element(s)", w);
            Assert.Contains("holds none", w);
        }

        [Fact]
        public void SomeHeld_SaysHowMany()
        {
            var w = UiSelectionReadBack.Warning(5, 3)!;
            Assert.Contains("only 3", w);
        }
    }
}
