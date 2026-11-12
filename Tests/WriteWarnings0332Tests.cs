#nullable enable
// AI-332: new elements land on top of existing ones without a warning. The write's transaction deletes Revit's warnings so no
// dialog blocks Revit, and the model sees them, but the pane showed them only when the turn had no commit. A committed write
// now shows "Revit warned about N things" in its card.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WriteWarnings0332Tests
    {
        private static WarningDto W(string text, string sev = WarningSeverity.Warning, string? def = "def-overlap", params long[] ids) =>
            new WarningDto { Text = text, Severity = sev, DefinitionId = def, Ids = ids.ToList() };

        private static (ToolRequestDto Request, Envelope[] Envs) Turn(string phase, string status, params WarningDto[] warnings)
        {
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"Place 40 chairs\"}").RootElement.Clone() };
            var request = new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } };
            var env = new Envelope
            {
                Ok = status != EnvelopeStatus.Failed, Status = status, Phase = phase, Summary = "Ran.",
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
                Changes = new ChangeReportDto { TouchedByCategory = new SideEffectsDto { AddedByCategory = { ["Furniture"] = 40 } } },
                Warnings = warnings.ToList(),
            };
            return (request, new[] { env });
        }

        private static ResultCardModel Card(params (string Phase, string Status, WarningDto[] Warnings)[] steps)
        {
            var ev = new TurnEvidence();
            foreach (var s in steps)
            {
                var (request, envs) = Turn(s.Phase, s.Status, s.Warnings);
                ev.Add(request, envs);
            }
            return ResultCardModel.From(ev, ReceiptLanguage.En);
        }

        [Fact]
        public void ACommittedWritesWarnings_ReachTheCard()
        {
            var card = Card((EnvelopePhase.Commit, EnvelopeStatus.Done, new[]
            {
                W("Highlighted elements are identical instances in the same place.", WarningSeverity.Warning, "dup", 10, 11),
            }));
            var w = Assert.Single(card.WriteWarnings);
            Assert.Equal("Highlighted elements are identical instances in the same place.", w.Title);
            Assert.Equal(new long[] { 10, 11 }, w.Ids);
        }

        [Fact]
        public void TwentyIdenticalWarnings_AreOneRow_WithACount_AndTheUnionOfIds()
        {
            var many = Enumerable.Range(0, 20).Select(i => W("Highlighted elements overlap.", WarningSeverity.Warning, "ovl", 100 + i, 200 + i)).ToArray();
            var card = Card((EnvelopePhase.Commit, EnvelopeStatus.Done, many));
            var w = Assert.Single(card.WriteWarnings);
            Assert.Equal(20, w.Repeats);
            Assert.Equal(40, w.Ids.Count);
            Assert.Equal(20, WriteWarningText.Count(card.WriteWarnings));
        }

        [Fact]
        public void DifferentMessagesOrDefinitions_StayApart_InFirstSeenOrder()
        {
            var card = Card((EnvelopePhase.Commit, EnvelopeStatus.Done, new[]
            {
                W("Walls overlap.", WarningSeverity.Warning, "a", 1),
                W("Identical instances.", WarningSeverity.Warning, "b", 2),
                W("Walls overlap.", WarningSeverity.Warning, "c", 3),
            }));
            Assert.Equal(new[] { "Walls overlap.", "Identical instances.", "Walls overlap." }, card.WriteWarnings.Select(w => w.Title));
        }

        [Fact]
        public void APreviewsWarnings_AreNotShown_TheyDescribeAWriteThatWasRolledBack()
        {
            var card = Card(
                (EnvelopePhase.Preview, EnvelopeStatus.Failed, new[] { W("Changes to groups are allowed only in group edit mode.", WarningSeverity.Error, "grp", 1) }),
                (EnvelopePhase.Commit, EnvelopeStatus.Done, new WarningDto[0]));
            Assert.Empty(card.WriteWarnings);
        }

        [Fact]
        public void ABlankWarningText_IsDropped()
        {
            var card = Card((EnvelopePhase.Commit, EnvelopeStatus.Done, new[] { W("  ", WarningSeverity.Warning, "x", 1), W("Real one.", WarningSeverity.Warning, "y", 2) }));
            Assert.Equal("Real one.", Assert.Single(card.WriteWarnings).Title);
        }

        [Fact]
        public void TheCardKeepsItsKind_AndANoWarningWriteHasNone()
        {
            var card = Card((EnvelopePhase.Commit, EnvelopeStatus.Done, new WarningDto[0]));
            Assert.Empty(card.WriteWarnings);
            Assert.Equal(ResultKind.Changes, card.Kind);
        }

        [Fact]
        public void TheTitle_CountsEveryWarning_InBothLanguages()
        {
            Assert.Equal("Revit warned about 3 things", WriteWarningText.Title(3, ReceiptLanguage.En));
            Assert.Equal("Revit warned about 1 thing", WriteWarningText.Title(1, ReceiptLanguage.En));
            Assert.Equal("Revit memberi amaran tentang 3 perkara", WriteWarningText.Title(3, ReceiptLanguage.Ms));
            Assert.Equal(5, WriteWarningText.Count(new[] { new Finding { Repeats = 3 }, new Finding { Repeats = 1 }, new Finding { Repeats = 1 } }));
            Assert.Equal(1, WriteWarningText.Count(new[] { new Finding { Repeats = 0 } }));
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheCardDrawsTheBlock_InsideTheCard_WithTheFindingsRowAndAShowButton()
        {
            Assert.Contains("WriteWarningsBlock.Add(root, card, lang, actions);", Src("UI/Copilot/Controls/ResultCardView.cs"));
            var block = Src("UI/Copilot/Controls/WriteWarningsBlock.cs");
            Assert.Contains("FindingsBody.Row(card.WriteWarnings[i], lang, actions, top: true)", block);
            Assert.Contains("Cp.AmberBg", block);
            Assert.DoesNotContain("\"#", block);
            Assert.Contains("internal static FrameworkElement Row(Finding f,", Src("UI/Copilot/Controls/FindingsBody.cs"));
        }

        [Fact]
        public void TheWritePathStillRecordsEveryWarningItDeletes()
        {
            // The envelope's warnings come from FailureLog, which SwallowWarnings fills for EVERY failure before deleting it:
            // that is what makes a duplicate placement reportable at all.
            var tx = Src("BinaVibe/Mcp/Tools/TxGuard.cs");
            Assert.Contains("Record(f, text, isWarning);", tx);
            Assert.Contains("a.DeleteWarning(f);", tx);
            Assert.Contains("FailureLog.Current.Add(text,", tx);
        }
    }
}
