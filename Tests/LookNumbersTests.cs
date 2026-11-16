#nullable enable
// The "Gabung" result view, G4: what needs a look gets amber numbered badges (1, 2, 3 ... in the card's order), never
// dimmed and never merged into a count badge; two that would overlap are one badge with the lower number and a "+".
// The bar has a stepper "‹ 1 of 2 ›" that selects that element in Revit and zooms to it, like Revit's Review Warnings.
// Above 99 the rest are plain amber dots. Names are invented.
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Highlights;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LookNumbersTests
    {
        private static HighlightSet Set() => new HighlightSet
        {
            Kinds =
            {
                new MarkGroup { Title = "Lighting Fixtures", Category = "Lighting Fixtures", Outcome = MarkOutcome.Changed, Ids = Enumerable.Range(1, 52).Select(i => (long)i).ToList(), Count = 52 },
                new MarkGroup { Title = "Read back different", Outcome = MarkOutcome.NeedsLook, Ids = { 17, 3 }, Count = 2 },
                new MarkGroup { Title = "Demo_dl_13W no longer matches its family name", Outcome = MarkOutcome.NeedsLook, Ids = { 3, 40 }, Count = 2 },
            },
        };

        [Fact]
        public void TheNumbers_FollowTheCardsOrder_EachElementOnce()
        {
            var order = MarkKey.LookOrder(Set());
            Assert.Equal(new long[] { 17, 3, 40 }, order);                                     // 3 is in two groups: numbered once
            var place = order.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
            Assert.Equal(1, MarkKey.NumberOf(17, place));
            Assert.Equal(2, MarkKey.NumberOf(3, place));
            Assert.Equal(3, MarkKey.NumberOf(40, place));
            Assert.Equal(0, MarkKey.NumberOf(5, place));                                       // a changed one: no number
            Assert.Empty(MarkKey.LookOrder(new HighlightSet()));
        }

        [Fact]
        public void Above99_TheRestArePlainAmberDots()
        {
            var place = Enumerable.Range(0, 120).ToDictionary(i => (long)(1000 + i), i => i);
            Assert.Equal(99, MarkKey.NumberOf(1098, place));
            Assert.Equal(0, MarkKey.NumberOf(1099, place));
            Assert.Equal(99, MarkKey.MaxNumbers);
        }

        [Fact]
        public void TheStepper_GoesRound_AndSaysWhereItIs()
        {
            Assert.Equal(0, MarkKey.StepTo(-1, 1, 2));     // › from nothing: the first
            Assert.Equal(1, MarkKey.StepTo(-1, -1, 2));    // ‹ from nothing: the last
            Assert.Equal(0, MarkKey.StepTo(-1, 0, 2));     // the label from nothing: the first
            Assert.Equal(1, MarkKey.StepTo(0, 1, 2));
            Assert.Equal(0, MarkKey.StepTo(1, 1, 2));      // round at the end
            Assert.Equal(1, MarkKey.StepTo(0, -1, 2));     // and at the start
            Assert.Equal(1, MarkKey.StepTo(1, 0, 2));
            Assert.Equal(-1, MarkKey.StepTo(0, 1, 0));
            Assert.Equal("1 of 2", MarkKey.StepLabel(-1, 2));
            Assert.Equal("2 of 2", MarkKey.StepLabel(1, 2));
            Assert.Equal("", MarkKey.StepLabel(-1, 0));
        }

        [Fact]
        public void TheBarSaysWhatNeedsALook_AndTheNumberedBadgesAreNeverDimOrCounted()
        {
            var key = MarkKey.Build(Set(), null, "{3D}");
            Assert.Equal("52 lighting fixtures changed", key.Title);
            Assert.Equal(" · 4 need a look", key.Look);                                         // the groups' own counts

            var s = ResultMarksFocusTests.Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("private MarkTone ToneOf(Mark m) => m.Number > 0 ? MarkTone.Normal : MarkFocus.ToneOf(m.Id, _focusIds);", s);
            Assert.Contains("MarkCluster.Group(numbered.Select(m => screen(m.Point)).ToList(), MarkKey.BadgeDip)", s);
            Assert.Contains("MarkImages.BadgePath(MarkOutcome.NeedsLook, MarkCluster.NumberLabel(first.Number, members.Count > 1)", s);
            Assert.Contains("_marks.Where(m => !m.Frame && m.Number == 0).GroupBy(m => (m.Outcome, m.Tone))", s);   // only plain dots are counted
            Assert.Contains("if (m.Outcome == MarkOutcome.NeedsLook) m.Number = MarkKey.NumberOf(m.Id, _lookPlace);", s);
            var step = s.Substring(s.IndexOf("public void Step(int delta)", System.StringComparison.Ordinal));
            step = step.Substring(0, step.IndexOf("public void ZoomToAll()", System.StringComparison.Ordinal));
            Assert.Contains("Select(app, eids, \"look-step\", generation);", step);              // through the one selection helper
            Assert.Contains("BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, here)", step);
            Assert.DoesNotContain("ShowElements", step);

            var bar = ResultMarksFocusTests.Src("UI", "Copilot", "Highlights", "HighlightOverlayWindow.xaml.cs");
            Assert.Contains("KeyButton(\"‹\", () => step(-1)", bar);
            Assert.Contains("KeyButton(k.Step, () => step(0)", bar);
            Assert.Contains("KeyButton(\"›\", () => step(1)", bar);
        }
    }
}
