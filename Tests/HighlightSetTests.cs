#nullable enable
// Result pattern Tasks 4 and 6: highlight set from the result card, and view projection.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class HighlightSetTests
    {
        [Fact]
        public void TypeGroups_AreChangedGroupsOfTheirInstances_WithTheTypesInstanceCount()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Groups.Add(new TypeGroup { TypeName = "1X13W", IsType = true, Instances = 38, Ids = new List<long> { 101 }, Rows = { new ChangeRow { Param = "Mark", After = "A", Ids = { 101 } } } });
            card.Groups.Add(new TypeGroup { TypeName = "1X8W", IsType = true, Instances = 14, Ids = new List<long> { 202 }, Rows = { new ChangeRow { Param = "Mark", After = "B", Ids = { 202 } } } });

            var set = HighlightSet.From(card, new List<Envelope>());

            var types = set.Kinds.Where(k => k.Kind == MarkKind.TypeInstances).ToList();
            Assert.Equal(2, types.Count);
            Assert.All(types, t => Assert.Equal(MarkOutcome.Changed, t.Outcome));
            Assert.Equal(new[] { 38, 14 }, types.Select(t => t.Count));
            Assert.Contains(101, types[0].Ids);
            Assert.Equal(52, set.Total(MarkOutcome.Changed));
            Assert.False(set.IsEmpty);
        }

        [Fact]
        public void FindingsAndReadBackMismatches_NeedALook_AndADeletionMarksNothing()
        {
            var card = new ResultCardModel { Kind = ResultKind.Findings };
            card.Findings.Add(new Finding { Title = "No circuit", Fixable = true, Ids = new List<long> { 900, 901 } });
            card.Findings.Add(new Finding { Title = "Outside room", Fixable = false, Ids = new List<long> { 950 } });
            var env = new Envelope { Changes = new ChangeReportDto { Touched = new ChangesDto { Deleted = new List<ElementRef> { new(777, null) } } } };

            var set = HighlightSet.From(card, new[] { env });

            Assert.All(set.Kinds, k => Assert.Equal(MarkOutcome.NeedsLook, k.Outcome));
            Assert.Equal(3, set.Total(MarkOutcome.NeedsLook));
            Assert.DoesNotContain(set.Kinds, k => k.Ids.Contains(777));
        }

        [Fact]
        public void ASelection_DrawsNoMarks_RevitsSelectionIsItsMark()
        {
            var set = HighlightSet.From(new ResultCardModel { Kind = ResultKind.Selection, Selected = 12 }, new List<Envelope>());
            Assert.True(set.IsEmpty);
        }

        [Fact]
        public void NullChanges_IsEmpty()
        {
            var set = HighlightSet.From(new ResultCardModel(), new[] { new Envelope() });
            Assert.True(set.IsEmpty);
        }

        [Fact]
        public void Colours_GreenMadeVioletChangedAmberLook_NeverBlue_OnBothCanvases()
        {
            Assert.Equal("#0E9F55", HighlightSet.ColorOf(MarkOutcome.Made, darkCanvas: false));
            Assert.Equal("#36D97F", HighlightSet.ColorOf(MarkOutcome.Made, darkCanvas: true));
            Assert.Equal("#7C3AED", HighlightSet.ColorOf(MarkOutcome.Changed, darkCanvas: false));
            Assert.Equal("#B69CFF", HighlightSet.ColorOf(MarkOutcome.Changed, darkCanvas: true));
            Assert.Equal("#C27803", HighlightSet.ColorOf(MarkOutcome.NeedsLook, darkCanvas: false));
            Assert.Equal("#F5B841", HighlightSet.ColorOf(MarkOutcome.NeedsLook, darkCanvas: true));
            foreach (MarkOutcome o in System.Enum.GetValues(typeof(MarkOutcome)))
                foreach (var dark in new[] { false, true })
                {
                    var hex = HighlightSet.ColorOf(o, dark);
                    int Ch(int i) => System.Convert.ToInt32(hex.Substring(1 + 2 * i, 2), 16);
                    double r = Ch(0), g = Ch(1), bl = Ch(2);
                    double max = System.Math.Max(r, System.Math.Max(g, bl)), min = System.Math.Min(r, System.Math.Min(g, bl));
                    var hue = max == min ? -1 : max == bl ? 60 * ((r - g) / (max - min)) + 240 : max == g ? 60 * ((bl - r) / (max - min)) + 120 : (60 * ((g - bl) / (max - min)) + 360) % 360;
                    var saturated = max > 0 && (max - min) / max > 0.35;   // a grey with a cool cast is not blue
                    Assert.False(saturated && hue >= 190 && hue <= 245, o + " reads as blue (Revit's selection, hue 190-245): " + hex + " hue " + hue);
                }
            Assert.True(HighlightSet.Dashed(MarkOutcome.NeedsLook));
            Assert.False(HighlightSet.Dashed(MarkOutcome.Made));
        }

        [Theory]
        [InlineData("Door Tags", MarkKind.Annotation)]
        [InlineData("Room Tags", MarkKind.Annotation)]
        [InlineData("Text Notes", MarkKind.Annotation)]
        [InlineData("Dimensions", MarkKind.Dimension)]
        [InlineData("Doors", MarkKind.Element)]
        [InlineData("Walls", MarkKind.Element)]
        public void TheMark_FitsTheKindOfThing(string category, MarkKind kind) => Assert.Equal(kind, HighlightSet.KindOf(category));
    }

    public class ViewProjectionTests
    {
        private static readonly Vec3 Right = new(1, 0, 0), Up = new(0, 1, 0);

        [Fact]
        public void PlanView_MapsToPercent()
        {
            var r = ViewProjection.ToPercent(new Vec3(25, 25, 0), new Vec3(0, 0, 0), new Vec3(100, 50, 0), Right, Up);
            Assert.NotNull(r);
            Assert.Equal(25, r!.Value.XPct, 6);
            Assert.Equal(50, r.Value.YPct, 6);
        }

        [Fact]
        public void TopLeftCorner_IsZeroZero()
        {
            var r = ViewProjection.ToPercent(new Vec3(0, 50, 0), new Vec3(0, 0, 0), new Vec3(100, 50, 0), Right, Up);
            Assert.Equal(0, r!.Value.XPct, 6);
            Assert.Equal(0, r.Value.YPct, 6);
        }

        [Fact]
        public void CornerOrder_DoesNotMatter()
        {
            var a = ViewProjection.ToPercent(new Vec3(25, 25, 0), new Vec3(0, 0, 0), new Vec3(100, 50, 0), Right, Up);
            var b = ViewProjection.ToPercent(new Vec3(25, 25, 0), new Vec3(100, 50, 0), new Vec3(0, 0, 0), Right, Up);
            Assert.Equal(a, b);
        }

        [Fact]
        public void DegenerateRectangle_IsNull()
        {
            Assert.Null(ViewProjection.ToPercent(new Vec3(1, 1, 0), new Vec3(5, 5, 0), new Vec3(5, 5, 0), Right, Up));
            Assert.False(ViewProjection.InView(null));
            Assert.False(ViewProjection.InView((120, 50)));
        }
    }
}
