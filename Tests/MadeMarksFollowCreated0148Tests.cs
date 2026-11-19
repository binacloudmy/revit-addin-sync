#nullable enable
// r13 (0.0.148), live fb713fc2 on 0.0.147 (frame R7-18): the section box run added 10 elements and listed one in data.created (the view
// "Section Box - PTBR_Aras 01"). The card said "Views 1 and 10 supporting elements", but the marks header said "3 elements made" (the view,
// its camera, its section box). When data.created lists what was made, the Made marks follow it; what Revit made along with the view is no
// mark. Any other addition of the same run (tags, walls: never in data.created) stays a Made mark, and a run with no list is unchanged.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MadeMarksFollowCreated0148Tests
    {
        private const long View = 3431003, Camera = 3431001, Box = 3431002;

        private static HighlightSet Run(Dictionary<long, string> added, string data)
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
            CommitIds.Remember(env, new ChangesDto { Added = added.Keys.Select(i => new ElementRef(i, null)).ToList() }, added);
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"Section box\"}").RootElement.Clone() };
            var ev = new TurnEvidence();
            ev.Add(new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } }, new[] { env });
            // The server's result of fb713fc2: kind created, the one view row.
            var result = CopilotJson.Deserialize<TurnResultDto>("{\"kind\":\"created\",\"lang\":\"en\",\"reply\":\"Section box made.\",\"items\":[{\"ids\":[3431003],\"kind\":\"view\",\"title\":\"Section Box - PTBR_Aras 01\"}],\"counts\":{\"created\":1}}");
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(result);
            Assert.NotEqual(ResultKind.None, card.Kind);
            return HighlightSet.From(card, new[] { env });
        }

        private static Dictionary<long, string> SectionBoxAdded() => new Dictionary<long, string>
        {
            [Camera] = "Cameras", [Box] = "Section Boxes", [View] = "Views", [3431004] = "class:Element", [3431005] = "class:Element",
            [3431006] = "class:Element", [3431008] = "Sun Path", [3431009] = "Sun Path", [3431010] = "Work Plane Grid", [3431011] = "class:SketchPlane",
        };

        private const string Listed = "{\"mode\":\"write\",\"created\":[{\"id\":3431003,\"category\":\"Views\",\"name\":\"Section Box - PTBR_Aras 01\"}],\"created_count\":1}";

        [Fact]
        public void TheSectionBoxRun_MarksTheOneViewItMade()
        {
            var set = Run(SectionBoxAdded(), Listed);
            var made = set.Kinds.Where(k => k.Outcome == MarkOutcome.Made).ToList();
            Assert.Equal(new long[] { View }, made.SelectMany(k => k.Ids));
            Assert.DoesNotContain(set.Kinds, k => k.Ids.Contains(Camera) || k.Ids.Contains(Box));   // neither made nor changed
        }

        [Fact]
        public void WithoutTheList_TheMarksAreAsBefore()
        {
            var set = Run(SectionBoxAdded(), "{\"mode\":\"write\"}");
            var made = set.Kinds.Where(k => k.Outcome == MarkOutcome.Made).SelectMany(k => k.Ids).OrderBy(i => i).ToList();
            Assert.Equal(new long[] { Camera, Box, View }, made);
        }

        [Fact]
        public void AnotherAdditionOfTheSameRun_StaysAMadeMark()
        {
            var added = SectionBoxAdded();
            added[500001] = "Door Tags"; added[500002] = "Door Tags";
            var set = Run(added, Listed);
            var made = set.Kinds.Where(k => k.Outcome == MarkOutcome.Made).SelectMany(k => k.Ids).OrderBy(i => i).ToList();
            Assert.Equal(new long[] { View, 500001, 500002 }, made);
        }

        [Fact]
        public void OnlyWhatRevitMakesAlongWithAView_IsLeftOut()
        {
            foreach (var c in new[] { "Cameras", "Section Boxes", "Viewports", "Sun Path", "class:Element", "" })
                Assert.True(HighlightSet.MadeAlongWithCreated(c), c);
            foreach (var c in new[] { "Door Tags", "Walls", "Rooms", "Scope Boxes" })
                Assert.False(HighlightSet.MadeAlongWithCreated(c), c);
            // The by-product list used for edits is unchanged: a camera or a section box is not a by-product there.
            Assert.False(CreatedSummary.IsByProduct("Cameras"));
            Assert.False(CreatedSummary.IsByProduct("Section Boxes"));
        }
    }
}
