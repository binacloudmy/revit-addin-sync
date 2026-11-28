#nullable enable
// r13 (0.0.147), live 7703a8f9 (hide all tags, playbook hide-tags-in-view): the card's change line read "Schedules 2 · Views 1".
// The schedules were updated by Revit because the view changed; the run did not target them. A dependent category the run
// did not name (Schedules, Viewports, sketches) is listed after "updated by Revit", never as the drafter's change; a run
// that names it (a schedule playbook) keeps it as a real change, and nothing real is ever hidden.
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DependentUpdates0147Tests
    {
        private static TurnEvidence Turn(string args, SideEffectsDto touched)
        {
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse(args).RootElement.Clone() };
            var request = new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } };
            var commit = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran.",
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
                Changes = new ChangeReportDto { TouchedByCategory = touched },
            };
            var ev = new TurnEvidence();
            ev.Add(request, new[] { commit });
            return ev;
        }

        private const string HideTags = "{\"intent\":\"Hide all tags in the active view\",\"playbook\":\"hide-tags-in-view\",\"inputs\":{}}";

        [Fact]
        public void TheLiveHideTagsTurn_ListsTheViewAsTheChange_AndTheSchedulesAsUpdatedByRevit()
        {
            var ev = Turn(HideTags, new SideEffectsDto { ModifiedByCategory = { ["Views"] = 1, ["Schedules"] = 2 } });
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(ResultKind.Changes, card.Kind);
            Assert.Equal(new[] { ("Views", 1) }, card.CategoryCounts.ToArray());
            Assert.Equal(new[] { ("Schedules", 2) }, card.UpdatedByRevit.ToArray());
            Assert.Equal("1 element changed", card.Headline);
            Assert.Equal("Views 1 · updated by Revit: Schedules 2", card.Detail);
            Assert.EndsWith("dikemas kini oleh Revit: Schedules 2", ResultCardModel.From(ev, ReceiptLanguage.Ms).Detail);
        }

        [Fact]
        public void ARunThatTargetsSchedules_KeepsThemAsItsChange()
        {
            var ev = Turn("{\"intent\":\"Filter the door schedule by level\",\"playbook\":\"schedule\"}",
                new SideEffectsDto { ModifiedByCategory = { ["Schedules"] = 1 } });
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(new[] { ("Schedules", 1) }, card.CategoryCounts.ToArray());
            Assert.Empty(card.UpdatedByRevit);
            Assert.Equal("Schedules 1", card.Detail);
        }

        [Fact]
        public void OnlyDependents_AreStillTheChange_NeverACardThatSaysNothing()
        {
            var card = ResultCardModel.From(Turn(HideTags, new SideEffectsDto { ModifiedByCategory = { ["Schedules"] = 2 } }), ReceiptLanguage.En);
            Assert.Equal(ResultKind.Changes, card.Kind);
            Assert.Equal(new[] { ("Schedules", 2) }, card.CategoryCounts.ToArray());
            Assert.Empty(card.UpdatedByRevit);
        }

        [Fact]
        public void AddedOrDeletedDependents_AndOtherCategories_StayRealChanges()
        {
            var ev = Turn(HideTags, new SideEffectsDto
            {
                AddedByCategory = { ["Schedules"] = 1 },
                ModifiedByCategory = { ["Walls"] = 3, ["Viewports"] = 1 },
            });
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(new[] { ("Walls", 3), ("Schedules", 1) }, card.CategoryCounts.ToArray());
            Assert.Equal(new[] { ("Viewports", 1) }, card.UpdatedByRevit.ToArray());
        }

        [Theory]
        [InlineData("Schedules", " hide all tags in the active view hide-tags-in-view", true)]
        [InlineData("Schedules", " buat jadual pintu", false)]
        [InlineData("Viewports", " place views on sheet a101", false)]
        [InlineData("Views", " hide all tags", false)]
        [InlineData("Walls", "", false)]
        public void IsDependentUpdate_OnlyForADependentTheCallDidNotName(string category, string words, bool dependent) =>
            Assert.Equal(dependent, ResultCardModel.IsDependentUpdate(category, words));
    }
}
