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
        public void TypeGroups_GoToTypeIds_AsChanged()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Groups.Add(new TypeGroup { TypeName = "1X13W", IsType = true, Instances = 38, Ids = new List<long> { 101 } });
            card.Groups.Add(new TypeGroup { TypeName = "1X8W", IsType = true, Instances = 14, Ids = new List<long> { 202 } });

            var set = HighlightSet.From(card, new List<Envelope>());

            Assert.Equal(2, set.Count(MarkState.Changed));
            Assert.Contains(101, set.TypeIds[MarkState.Changed]);
            Assert.False(set.Ids.ContainsKey(MarkState.Changed));
            Assert.False(set.IsEmpty);
        }

        [Fact]
        public void InstanceGroups_FindingsAndDeleted_MapToStates()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Groups.Add(new TypeGroup { TypeName = "Doors", Ids = Enumerable.Range(1, 52).Select(i => (long)i).ToList(), Instances = 52 });
            card.Findings.Add(new Finding { Title = "No circuit", Fixable = true, Ids = new List<long> { 900, 901 } });
            card.Findings.Add(new Finding { Title = "Outside room", Fixable = false, Ids = new List<long> { 950 } });
            var env = new Envelope { Changes = new ChangeReportDto { Touched = new ChangesDto { Deleted = new List<ElementRef> { new(777, null) } } } };

            var set = HighlightSet.From(card, new[] { env });

            Assert.Equal(52, set.Ids[MarkState.Changed].Count);
            Assert.Equal(2, set.Count(MarkState.Fixable));
            Assert.Equal(1, set.Count(MarkState.NeedsYou));
            Assert.Contains(777, set.Ids[MarkState.Deleted]);
        }

        [Fact]
        public void NullChanges_IsEmpty()
        {
            var set = HighlightSet.From(new ResultCardModel(), new[] { new Envelope() });
            Assert.True(set.IsEmpty);
        }

        [Fact]
        public void Colors()
        {
            Assert.Equal("#4CC77F", HighlightSet.ColorOf(MarkState.Changed));
            Assert.Equal("#6B9BFF", HighlightSet.ColorOf(MarkState.Created));
            Assert.Equal("#6B9BFF", HighlightSet.ColorOf(MarkState.Selected));
            Assert.Equal("#FF7B6B", HighlightSet.ColorOf(MarkState.Fixable));
            Assert.Equal("#E7A84A", HighlightSet.ColorOf(MarkState.NeedsYou));
            Assert.Equal("#E7A84A", HighlightSet.ColorOf(MarkState.Deleted));
        }
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
