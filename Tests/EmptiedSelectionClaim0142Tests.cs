#nullable enable
// 0.0.141 live check of the 3-option hold card: after Cancel the card said "Selected in Revit: 10" while the status bar showed 0. A later code run that set an
// EMPTY selection (data.ui.selected = 0) must reset the card's selection, not leave the earlier find's number.
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EmptiedSelectionClaim0142Tests
    {
        private static Envelope Find(int selected, int visible) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Found " + selected, Count = selected,
            Highlight = new HighlightDto { Selected = selected, VisibleInView = visible },
        };

        private static Envelope CodeSelected(int n) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Ran (read only).",
            Data = JsonDocument.Parse("{\"mode\":\"read\",\"ui\":{\"selected\":" + n + "}}").RootElement.Clone(),
        };

        [Fact]
        public void ALaterRunThatEmptiedTheSelection_ClearsTheClaim()
        {
            Assert.Equal((10, 8), ResultCardModel.FinalSelection(new[] { Find(10, 8) }));
            Assert.Equal((0, 0), ResultCardModel.FinalSelection(new[] { Find(10, 8), CodeSelected(0) }));          // the empty selection wins
            Assert.Equal((3, 0), ResultCardModel.FinalSelection(new[] { Find(10, 8), CodeSelected(0), CodeSelected(3) }));
            Assert.Equal((10, 8), ResultCardModel.FinalSelection(new[] { Find(10, 8), new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read } }));   // a call that says nothing about the selection changes nothing
        }
    }
}
