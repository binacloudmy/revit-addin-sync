#nullable enable
// r12 (0.0.145): the in-model marker is a 30 DIP target (coloured ring, white halo, solid dot) instead of the 10 DIP dot, and
// find results (highlight) and a card's Show draw Found marks too, not only write turns. No transaction, no Undo entry.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using RevitWebAppSync.UI.Copilot.Highlights;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FoundMarks0145Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void Found_IsAMarkSet_InMagenta()
        {
            var set = HighlightSet.Found(new long[] { 5, 6, 6, 7, 0 });
            Assert.False(set.IsEmpty);
            Assert.Equal(new long[] { 5, 6, 7 }, set.Kinds.Single().Ids);
            Assert.Equal(MarkOutcome.Found, set.Kinds[0].Outcome);
            Assert.Equal("3 elements found", MarkKey.Title(set.Kinds));
            Assert.Equal("#D61F69", HighlightSet.ColorOf(MarkOutcome.Found, false));
            Assert.Equal("#FF5FA2", HighlightSet.ColorOf(MarkOutcome.Found, true));
            Assert.False(HighlightSet.Dashed(MarkOutcome.Found));
            Assert.False(set.SelectsAtTheEnd);   // the find or the click selected already
        }

        [Fact]
        public void AShowClick_NeverWipesAWriteTurnsOwnMarks()
        {
            // r12 audit: the Warnings row's Show on a write card cleared that turn's Made/Changed marks and the bar's Undo.
            Assert.Equal(FoundClick.KeepTurnMarks, HighlightSet.FoundClickFor(true, MarksState.Shown));
            Assert.Equal(FoundClick.KeepTurnMarks, HighlightSet.FoundClickFor(true, MarksState.Hidden));
            Assert.Equal(FoundClick.ShowTurnResult, HighlightSet.FoundClickFor(true, MarksState.None));
            Assert.Equal(FoundClick.ShowFound, HighlightSet.FoundClickFor(false, MarksState.None));
            Assert.Equal(FoundClick.ShowFound, HighlightSet.FoundClickFor(false, MarksState.Shown));
        }

        [Fact]
        public void FoundInSelection_IsNotEmpty_UntilRevitIsAsked()
        {
            var set = HighlightSet.FoundInSelection("t1");
            Assert.True(set.FromSelection);
            Assert.False(set.IsEmpty);
            Assert.Equal("t1", set.TurnId);
        }

        [Fact]
        public void TheMarkerIsBig_AndANewCacheName()
        {
            Assert.InRange(MarkKey.MarkerDip, 28, 32);
            var images = Src("UI/Copilot/Highlights/MarkImages.cs");
            Assert.Contains("Cached(\"marker-\" + Key(outcome)", images);
            Assert.Contains("Brushes.White", images);
        }

        [Fact]
        public void FindsAndShowClicks_DrawFoundMarks()
        {
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("highlights = HL.HighlightSet.FoundInSelection();", vm);
            Assert.Contains("var set = HL.HighlightSet.Found(ids, m?.TurnId ?? \"\");", vm);
            Assert.Contains("Revit.ShowMarks(set, () => UndoTurn(m), () => CanUndoTurn(m));", vm);
            var view = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("ShowElements = ids => { SelectElements(ids); Vm?.MarkFoundInModel(m, ids); }", view);
            var inModel = Src("UI/Copilot/Highlights/InModelResult.cs");
            Assert.Contains("if (set.FromSelection) FillFromSelection(app, set);", inModel);
            Assert.Contains("public const int MaxMarks = 400;", inModel);
        }

        // Live 19 Nov 20:24 (turn 46d35bc5): "Select all the fire doors on PTBR_Aras 01." selected 6 doors; the server's rows (3 types,
        // count 2 each, ids, a rooms cell) are a tally, so WithServerResult made the card Created with Selected = 0 and the turn-end
        // hook (Selection && Selected > 0) never ran: no Found marks, no Show marks button, the rows' click did nothing.
        private const string FireDoors = "{\"kind\":\"selection\",\"lang\":\"en\",\"reply\":\"Selected the 6 fire doors on PTBR_Aras 01.\","
            + "\"counts\":{\"elements\":6},\"items\":["
            + "{\"ids\":[977255,977284],\"kind\":\"element\",\"cells\":{\"rooms\":\"BILIK SUIS 1, BILIK TCR 1\"},\"count\":2,\"group\":\"Fire doors, PTBR_Aras 01\",\"title\":\"FD1 (PTa2) 1700x 2400 - 1 HOUR\"},"
            + "{\"ids\":[968434,1128977],\"kind\":\"element\",\"cells\":{\"rooms\":\"TANGGA 2, TANGGA 1\"},\"count\":2,\"group\":\"Fire doors, PTBR_Aras 01\",\"title\":\"FD2 (PTa1) 1000 x 2400 - 1 HOUR\"},"
            + "{\"ids\":[967821,1327450],\"kind\":\"element\",\"cells\":{\"rooms\":\"ELE, ELE\"},\"count\":2,\"group\":\"Fire doors, PTBR_Aras 01\",\"title\":\"FD3 (PTa1) 700 x 1800 - 2 HOUR\"}]}";

        [Fact]
        public void AFindsCardWithElementRows_DrawsFoundMarks_OnTheirIds()
        {
            var result = JsonSerializer.Deserialize<TurnResultDto>(FireDoors)!;
            var card = new ResultCardModel { Kind = ResultKind.Selection, Selected = 6 }.WithServerResult(result);
            Assert.Equal(ResultKind.Created, card.Kind);   // the tally turn, as live
            var find = new List<Envelope> { new Envelope { Ok = true, Status = "done", Phase = "read" } };
            var set = HighlightSet.From(card, find);
            Assert.False(set.IsEmpty);                      // HasMarks: the card's Show marks button, and the turn-end marks
            var g = Assert.Single(set.Kinds);
            Assert.Equal(MarkOutcome.Found, g.Outcome);
            Assert.Equal(new long[] { 977255, 977284, 968434, 1128977, 967821, 1327450 }, g.Ids);
            Assert.False(set.SelectsAtTheEnd);              // the find selected them already
            Assert.Equal(3, set.Rows.Count);                // each row's click focuses its 2 doors
            Assert.Equal("6 elements found", MarkKey.Title(set.Kinds));
        }

        [Fact]
        public void ASelectionCardWithElementRows_DrawsFoundMarks_Too()
        {
            var card = new ResultCardModel { Kind = ResultKind.Selection, Selected = 2 };
            card.Items = new ResultItems { Sections = { new ItemSection { Rows = { new ItemRow { Kind = ResultItemKinds.Element, Title = "FD1", Ids = { 5, 6 } } } } } };
            var set = HighlightSet.From(card, new List<Envelope> { new Envelope { Ok = true, Status = "done", Phase = "read" } });
            Assert.Equal(new long[] { 5, 6 }, set.Kinds.Single().Ids);
            Assert.Equal(MarkOutcome.Found, set.Kinds[0].Outcome);
            // A preview or a write that did not commit lists rows too: they were not found, so no Found marks.
            var write = new Envelope { Phase = "preview" };
            Assert.True(HighlightSet.From(card, new List<Envelope> { write }).IsEmpty);
            // r12 audit: no calls at all, a failed write's validation and a read-only rejection (both phase read), a failed turn.
            Assert.True(HighlightSet.From(card, new List<Envelope>()).IsEmpty);
            Assert.True(HighlightSet.From(card, new List<Envelope> { new Envelope { Ok = false, Status = "failed", Phase = "read" } }).IsEmpty);
            Assert.True(HighlightSet.From(card, new List<Envelope> { new Envelope { Ok = true, Status = "done", Phase = "read" }, new Envelope { Ok = false, Status = "rejected", Phase = "read" } }).IsEmpty);
            card.Failed = true;
            Assert.True(HighlightSet.From(card, new List<Envelope> { new Envelope { Ok = true, Status = "done", Phase = "read" } }).IsEmpty);
            // The card's "Show in model" / "Hide marks" header is gated on HasMarks only, a Selection card too.
            Assert.Contains("if (MarksHeader(actions) is FrameworkElement header) root.Children.Add(header);", Src("UI/Copilot/Controls/ResultCardView.cs"));
        }
    }
}
