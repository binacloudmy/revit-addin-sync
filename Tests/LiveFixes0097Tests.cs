#nullable enable
// Live test of 0.0.97: the creation toast stayed for twenty minutes and kept the first turn's text; the grid's Type column
// showed a parameter name and an empty Before showed nothing.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LiveFixes0097Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        // ---- the toast ----

        [Fact]
        public void TheToast_FadesForAResultThatMadeThings_AsForOneThatChangedThings()
        {
            var s = Src("UI/Copilot/Highlights/InModelResult.cs");

            Assert.Contains("kind == ResultKind.Changes || kind == ResultKind.Created", s);
            Assert.Contains("!FadesAway(_set.Card.Kind)", s);                       // StartFade
            Assert.Contains("FadesAway(kind) ? \" · fades in 8 s, click to keep\"", s);   // and the toast says so
            Assert.DoesNotContain("_set.Card.Kind != ResultKind.Changes) return;", s);
        }

        [Fact]
        public void AnOlderResultsLateDrawing_CanNeverShowOverANewerOneOrAfterAClear()
        {
            var s = Src("UI/Copilot/Highlights/InModelResult.cs");

            Assert.Contains("var generation = _generation;", s);
            Assert.Contains("if (generation != _generation) return;", s);
            var clear = s.IndexOf("public void Clear()", System.StringComparison.Ordinal);
            Assert.True(s.IndexOf("_generation++;", clear, System.StringComparison.Ordinal) > clear, "Clear bumps the generation");
        }

        [Fact]
        public void TheOldResult_IsClearedWhenANewTurnStarts_WhenTheNewOneHasNothingToDraw_AndOnANewChat()
        {
            var vm = Src("UI/Copilot/CopilotViewModel.cs");

            Assert.Contains("public event Action ModelResultStale;", vm);
            Assert.Equal(3, vm.Split("ModelResultStale?.Invoke()").Length - 1);   // turn start, nothing to draw, new chat
            Assert.Contains("else ModelResultStale?.Invoke();", vm);                // the latest turn drew nothing
            Assert.Contains("_vm.ModelResultStale += () => _inModel.Clear();", Src("UI/Copilot/CopilotPanel.xaml.cs"));
        }

        // ---- the creation headline ----

        [Fact]
        public void AGenericBucket_ReadsOther_NotElement()
        {
            var g = new CreatedGroup { Category = "Element", Count = 4 };

            Assert.Equal("4 other", CreatedSummary.Label(g));
            Assert.Equal("4 lain-lain", CreatedSummary.Label(g, ReceiptLanguage.Ms));
            Assert.Equal("other", CreatedSummary.DisplayName("Other", ReceiptLanguage.En));
            Assert.Equal("Sheets", CreatedSummary.DisplayName("Sheets", ReceiptLanguage.En));
            Assert.True(CreatedSummary.IsGeneric("class:Element".Substring(6)));
            Assert.False(CreatedSummary.IsGeneric("Doors"));
        }

        [Fact]
        public void TheHeadlineAndTheCopiedList_UseTheDisplayName()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Created", Count = 12,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto(),
                    TouchedByCategory = new SideEffectsDto { AddedByCategory = new Dictionary<string, int> { ["class:Element"] = 4, ["Viewports"] = 4, ["Sheets"] = 2, ["Floors"] = 1, ["Doors"] = 1 } },
                },
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
            };

            var by = CreatedSummary.From(new[] { env }, null, ReceiptLanguage.En)!;

            Assert.Equal("4 other · 4 Viewports · 2 Sheets · + 2 more", by.Headline(ReceiptLanguage.En));
            Assert.Contains("\nother\t4\t", by.CopyText());
            Assert.DoesNotContain("Element", by.CopyText());
        }

        // ---- the grid ----

        private static ResultCardModel InstanceWrite(string? category, Dictionary<string, int>? modifiedBy)
        {
            var data = category == null ? "{\"param\":\"Comments\",\"type_param\":false}" : "{\"param\":\"Comments\",\"type_param\":false,\"instances_category\":\"" + category + "\"}";
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Comments", Count = 3,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Modified = new List<FieldChangeDto>
                    {
                        new FieldChangeDto { Id = 1, Field = "Comments", Before = null, After = "R5-CHECK" },
                        new FieldChangeDto { Id = 2, Field = "Comments", Before = null, After = "R5-CHECK" },
                    } },
                    TouchedByCategory = modifiedBy == null ? null : new SideEffectsDto { ModifiedByCategory = modifiedBy },
                },
                Data = JsonDocument.Parse(data).RootElement.Clone(),
            };
            var ev = new TurnEvidence();
            ev.Add(new ToolRequestDto { RequestId = "r", Kind = ToolRequestKinds.Write, Calls = new List<ToolCallDto>
                { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.SetParameters, Args = JsonDocument.Parse("{\"param\":\"Comments\"}").RootElement.Clone() } } },
                new[] { env });
            return ResultCardModel.From(ev, ReceiptLanguage.En);
        }

        [Fact]
        public void TheTypeColumn_IsTheCategory_NeverTheParametersName()
        {
            var named = InstanceWrite("Doors", null);
            var derived = InstanceWrite(null, new Dictionary<string, int> { ["Doors"] = 2 });      // the envelope names no category: the one touched
            var unknown = InstanceWrite(null, new Dictionary<string, int> { ["Doors"] = 1, ["Windows"] = 1 });
            var none = InstanceWrite(null, null);

            Assert.Equal("Doors", ChangeTable.Rows(named, ReceiptLanguage.En)[0][0]);
            Assert.Equal("Doors", ChangeTable.Rows(derived, ReceiptLanguage.En)[0][0]);
            Assert.Equal("Elements", ChangeTable.Rows(unknown, ReceiptLanguage.En)[0][0]);
            Assert.Equal("Elements", ChangeTable.Rows(none, ReceiptLanguage.En)[0][0]);
            Assert.All(new[] { named, derived, unknown, none }, c => Assert.NotEqual("Comments", c.Groups[0].TypeName));
        }

        [Fact]
        public void AnEmptyValue_ReadsEmptyInTheGridAndTheCopiedTable_LikeTheCard()
        {
            var card = InstanceWrite("Doors", null);

            var row = ChangeTable.Rows(card, ReceiptLanguage.En)[0];
            Assert.Equal(new[] { "Doors", "Comments", "empty", "R5-CHECK" }, row.Take(4).ToArray());
            Assert.Equal("kosong", ChangeTable.Rows(card, ReceiptLanguage.Ms)[0][2]);
            Assert.Contains("\tempty\tR5-CHECK\t", ChangeTable.TabText(card));
        }

        [Fact]
        public void OnlyCategory_NamesTheOneThatWasTouched()
        {
            Assert.Equal("Doors", ResultCardModel.OnlyCategory(new Dictionary<string, int> { ["Doors"] = 3, ["Walls"] = 0 }));
            Assert.Null(ResultCardModel.OnlyCategory(new Dictionary<string, int> { ["Doors"] = 3, ["Walls"] = 1 }));
            Assert.Null(ResultCardModel.OnlyCategory(null));
            Assert.Null(ResultCardModel.OnlyCategory(new Dictionary<string, int>()));
        }
    }
}
