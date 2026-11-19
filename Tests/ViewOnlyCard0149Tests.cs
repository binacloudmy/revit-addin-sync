#nullable enable
// r13 (0.0.149), live 01a8e152 on 0.0.148 ("section box view untuk aras lantai 01" in {3D}; frames V8-B1-03, -04): the card for the one made
// view still said "1 view made · Not visible in this view", offered Select all, Zoom to all and Hide marks, and listed "and 10 supporting
// elements" under "Views 1". A view, sheet or schedule is no element of the active view: such a turn has no marks (no header, no buttons),
// no supporting line, and its item row keeps "Open ›". A turn that made walls or tags keeps its card as before.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ViewOnlyCard0149Tests
    {
        private const long View = 3431003, Camera = 3431001, Box = 3431002;

        private static (ResultCardModel Card, HighlightSet Set) Run(Dictionary<long, string> added, string data, string result, bool keepFull = true)
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran: Create a section box 3D view.",
                Data = JsonDocument.Parse(data).RootElement.Clone(),
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = added.Keys.Select(i => new ElementRef(i, null)).ToList() },
                    TouchedByCategory = new SideEffectsDto(),
                },
            };
            foreach (var g in added.GroupBy(kv => kv.Value)) env.Changes.TouchedByCategory.AddedByCategory[g.Key] = g.Count();
            if (keepFull) CommitIds.Remember(env, new ChangesDto { Added = added.Keys.Select(i => new ElementRef(i, null)).ToList() }, added);
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"x\"}").RootElement.Clone() };
            var ev = new TurnEvidence();
            ev.Add(new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } }, new[] { env });
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(CopilotJson.Deserialize<TurnResultDto>(result));
            return (card, HighlightSet.From(card, new[] { env }));
        }

        private static Dictionary<long, string> SectionBoxAdded() => new Dictionary<long, string>
        {
            [Camera] = "Cameras", [Box] = "Section Boxes", [View] = "Views", [3431004] = "class:Element", [3431005] = "class:Element",
            [3431006] = "class:Element", [3431008] = "Sun Path", [3431009] = "Sun Path", [3431010] = "Work Plane Grid", [3431011] = "class:SketchPlane",
        };

        private const string Listed = "{\"mode\":\"write\",\"created\":[{\"id\":3431003,\"category\":\"Views\",\"name\":\"Section Box - PTBR_Aras 01\"}],\"created_count\":1}";
        private const string ViewResult = "{\"kind\":\"created\",\"lang\":\"en\",\"reply\":\"Section box made.\",\"items\":[{\"ids\":[3431003],\"kind\":\"view\",\"title\":\"Section Box - PTBR_Aras 01\"}],\"counts\":{\"created\":1}}";

        [Fact]
        public void TheSectionBoxRun_HasNoMarksHeader_NoSupportingLine_AndKeepsOpen()
        {
            var (card, set) = Run(SectionBoxAdded(), Listed, ViewResult);
            Assert.Equal(ResultKind.Created, card.Kind);
            // The marks still know the view (0.0.148), but a made view only is nothing to draw: HasMarks, the header and the turn-end draw follow Drawable.
            Assert.False(set.IsEmpty);
            Assert.True(set.OnlyViewsMade);
            Assert.False(set.Drawable);
            // The card's rows: "Views 1" and no "and 10 supporting elements" line drawn for it.
            var by = card.CreatedBy!;
            Assert.Equal(new[] { "Views 1" }, by.Groups.Select(g => g.Category + " " + g.Count));
            Assert.True(by.OnlyViews);
            Assert.Equal(10 - 1, by.Supporting);                          // the count is kept (Undo still removes all), only not drawn
            // The item row keeps "Open ›".
            var row = card.Items!.Sections.SelectMany(s => s.Rows).Single();
            Assert.Equal(ItemButton.OpenView, row.Button);
            Assert.Equal("Open ›", ItemText.OpenCue(row.Button, ReceiptLanguage.En));
        }

        [Fact]
        public void TheSectionBoxRun_ReopenedFromHistory_StillHasNoMarks()
        {
            // No full id lists: the camera, section box and by-products come back with no category. Every category added beside the
            // listed view is one Revit makes along with it, so they are no Made marks and the card stays as on the live turn.
            var (card, set) = Run(SectionBoxAdded(), Listed, ViewResult, keepFull: false);
            Assert.Equal(new long[] { View }, set.Kinds.Where(k => k.Outcome == MarkOutcome.Made).SelectMany(k => k.Ids));
            Assert.True(set.OnlyViewsMade);
            Assert.False(set.Drawable);
            Assert.True(card.CreatedBy!.OnlyViews);
        }

        [Fact]
        public void OnlyAlongBesideListed_NeedsEveryOtherCategoryToBeAViewByProduct()
        {
            var listed = new List<(long, string)> { (View, "Views") };
            Assert.True(HighlightSet.OnlyAlongBesideListed(new Dictionary<string, int> { ["Views"] = 1, ["Cameras"] = 1, ["Section Boxes"] = 1, ["class:Element"] = 3 }, listed));
            Assert.False(HighlightSet.OnlyAlongBesideListed(new Dictionary<string, int> { ["Views"] = 1, ["Cameras"] = 1, ["Walls"] = 2 }, listed));
            Assert.False(HighlightSet.OnlyAlongBesideListed(new Dictionary<string, int> { ["Views"] = 2, ["Cameras"] = 1 }, listed));   // a view not listed
            Assert.False(HighlightSet.OnlyAlongBesideListed(null, listed));
        }

        private const long Sheet = 8001, TitleBlock = 8002, Viewport = 8003;
        private const string SheetListed = "{\"mode\":\"write\",\"created\":[{\"id\":8001,\"category\":\"Sheets\",\"name\":\"A-101 - Aras 01\"}],\"created_count\":1}";
        private const string SheetResult = "{\"kind\":\"created\",\"lang\":\"en\",\"reply\":\"Sheet made.\",\"items\":[{\"ids\":[8001],\"kind\":\"sheet\",\"title\":\"A-101 - Aras 01\"}],\"counts\":{\"created\":1}}";

        private static Dictionary<long, string> SheetAdded() => new Dictionary<long, string>
        {
            [Sheet] = "Sheets", [TitleBlock] = "Title Blocks", [Viewport] = "Viewports", [8004] = "class:Element", [8005] = "class:SketchPlane",
            [8006] = "Schedule Graphics", [8007] = "class:GraphicsStyle",
        };

        [Theory]
        [InlineData(true)]
        [InlineData(false)]   // reopened from history: no full id lists, the title block and viewport come back with no category
        public void ASheetRun_WithItsTitleBlockAndViewport_HasNoMarks(bool keepFull)
        {
            // Audit P2: a sheet turn also adds a title block (Round7 live C1/C5); it is part of the sheet, not "1 title block made".
            var (card, set) = Run(SheetAdded(), SheetListed, SheetResult, keepFull);
            Assert.Equal(new long[] { Sheet }, set.Kinds.Where(k => k.Outcome == MarkOutcome.Made).SelectMany(k => k.Ids));
            Assert.DoesNotContain(set.Kinds, k => k.Ids.Contains(TitleBlock) || k.Ids.Contains(Viewport));
            Assert.True(set.OnlyViewsMade);
            Assert.False(set.Drawable);
            Assert.True(card.CreatedBy!.OnlyViews);
            Assert.Equal(new[] { "Sheets 1" }, card.CreatedBy.Groups.Select(g => g.Category + " " + g.Count));
        }

        [Fact]
        public void ASheetRunThatAlsoTaggedDoors_KeepsTheTagMarks()
        {
            var added = SheetAdded();
            added[500001] = "Door Tags";
            foreach (var keepFull in new[] { true, false })
            {
                var (_, set) = Run(added, SheetListed, SheetResult, keepFull);
                Assert.True(set.Drawable);
                Assert.Contains(500001L, set.Kinds.Where(k => k.Outcome == MarkOutcome.Made).SelectMany(k => k.Ids));
            }
        }

        [Fact]
        public void ASheetOrScheduleOnly_IsViewLikeToo()
        {
            foreach (var c in new[] { "Views", "Sheets", "Schedules", " views " })
                Assert.True(CreatedSummary.IsViewLike(c), c);
            foreach (var c in new[] { "Walls", "Door Tags", "Viewports", "Cameras", "Elements", "", null })
                Assert.False(CreatedSummary.IsViewLike(c), c);
            var set = new HighlightSet();
            set.Kinds.Add(new MarkGroup { Category = "Sheets", Outcome = MarkOutcome.Made, Ids = { 1, 2 }, Count = 2 });
            set.Kinds.Add(new MarkGroup { Category = "Schedules", Outcome = MarkOutcome.Made, Ids = { 3 }, Count = 1 });
            Assert.True(set.OnlyViewsMade);
            Assert.False(set.Drawable);
        }

        [Fact]
        public void WallsOrTagsMadeAlongTheView_KeepTheirMarks()
        {
            var added = SectionBoxAdded();
            added[500001] = "Door Tags"; added[500002] = "Door Tags";
            var (_, set) = Run(added, Listed, ViewResult);
            Assert.False(set.OnlyViewsMade);
            Assert.True(set.Drawable);
            Assert.Equal("3 elements made", MarkKey.Title(set.Kinds));
        }

        [Fact]
        public void AnElementMakingTurn_IsUnchanged()
        {
            var walls = new Dictionary<long, string> { [700001] = "Walls", [700002] = "Walls", [700003] = "class:SketchPlane" };
            var (card, set) = Run(walls, "{\"mode\":\"write\"}", "{\"kind\":\"created\",\"lang\":\"en\",\"reply\":\"2 walls made.\",\"counts\":{\"created\":2}}");
            Assert.False(set.OnlyViewsMade);
            Assert.True(set.Drawable);
            Assert.Equal("2 walls made", MarkKey.Title(set.Kinds));
            var by = card.CreatedBy;
            if (by != null)
            {
                Assert.False(by.OnlyViews);
                Assert.Equal("and 1 supporting element", by.SupportingLine(ReceiptLanguage.En));
            }
        }

        [Fact]
        public void FoundAndChangedSets_AreNeverViewOnly()
        {
            Assert.False(HighlightSet.Found(new long[] { 1 }).OnlyViewsMade);
            Assert.False(HighlightSet.FoundInSelection().OnlyViewsMade);
            Assert.False(new HighlightSet().OnlyViewsMade);
            var changed = new HighlightSet();
            changed.Kinds.Add(new MarkGroup { Category = "Views", Outcome = MarkOutcome.Changed, Ids = { 9 }, Count = 1 });
            Assert.False(changed.OnlyViewsMade);
            Assert.True(changed.Drawable);
        }

        [Fact]
        public void TheCardAndTheModelFollowDrawable()
        {
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("HighlightSet.From(m.ResultCard, m.Evidence.Calls.Select(c => c.Env).ToList()).Drawable;", vm);   // HasMarks
            Assert.Contains("if (highlights != null && highlights.Drawable) ModelResultReady", vm);
            Assert.Contains("return !set.Drawable ? \"\" : HL.MarkKey.Title(", vm);
            var created = Src("UI/Copilot/Controls/CreatedCard.cs");
            Assert.Contains("if (!by.OnlyViews && by.SupportingLine(lang) is string supporting)", created);
            Assert.Contains("if (!by.OnlyViews) actions.Rows?.Link(", created);
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }
    }
}
