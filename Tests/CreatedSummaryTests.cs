#nullable enable
// The "Created" card grouped by category (boards 8-10): headline, rows, source line,
// copied list, and how the card model picks it up from commit envelopes.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CreatedSummaryTests
    {
        private static Envelope Commit(Dictionary<string, int>? touched, Dictionary<string, int>? side = null, int ids = 0, string? data = null)
        {
            var e = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Created", Count = ids,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = Enumerable.Range(0, ids).Select(i => new ElementRef(5000 + i, null)).ToList() },
                    TouchedByCategory = touched == null ? null : new SideEffectsDto { AddedByCategory = touched },
                    SideEffects = side == null ? null : new SideEffectsDto { AddedByCategory = side },
                },
                Data = JsonDocument.Parse(data ?? "{\"mode\":\"write\"}").RootElement.Clone(),
            };
            return e;
        }

        private static ToolRequestDto Request() => new ToolRequestDto
        {
            RequestId = "r", Kind = ToolRequestKinds.Write,
            Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{}").RootElement.Clone() } },
        };

        [Fact]
        public void SheetsAndViewports_HeadlineRowsAndSource()
        {
            var env = Commit(new() { ["Sheets"] = 6 }, new() { ["Viewports"] = 6 }, ids: 6);
            var s = CreatedSummary.From(new[] { env }, null, ReceiptLanguage.En)!;

            Assert.Equal("6 Sheets · 6 Viewports", s.Headline(ReceiptLanguage.En));
            Assert.Equal(new[] { "Sheets", "Viewports" }, s.Groups.Select(g => g.Category));   // equal counts: by name
            Assert.Equal(12, s.Total);
            Assert.Equal("Source: Your prompt", s.SourceLine(ReceiptLanguage.En));
            Assert.Equal("Sumber: Prompt anda", CreatedSummary.From(new[] { env }, "", ReceiptLanguage.Ms)!.SourceLine(ReceiptLanguage.Ms));
        }

        [Fact]
        public void AttachmentIsTheSource()
        {
            var s = CreatedSummary.From(new[] { Commit(new() { ["Sheets"] = 2 }, ids: 2) }, " list.xlsx ", ReceiptLanguage.En)!;
            Assert.Equal("Source: list.xlsx", s.SourceLine(ReceiptLanguage.En));
        }

        [Fact]
        public void SortsByCountDescending_AndSingularForOne()
        {
            var s = CreatedSummary.From(new[] { Commit(new() { ["Views"] = 1, ["Sheets"] = 3, ["Levels"] = 1, ["Room Separation Lines"] = 1 }, ids: 6) }, null, ReceiptLanguage.En)!;
            Assert.Equal("Sheets", s.Groups[0].Category);
            Assert.Equal("3 Sheets · 1 Level · 1 Room Separation Line · + 1 more", s.Headline(ReceiptLanguage.En));
            Assert.Equal("3 Sheets · 1 Level · 1 Room Separation Line · + 1 lagi", s.Headline(ReceiptLanguage.Ms));
            Assert.Equal("1 Category", CreatedSummary.Label(new CreatedGroup { Category = "Categories", Count = 1 }));
            Assert.Equal("1 Glass", CreatedSummary.Label(new CreatedGroup { Category = "Glass", Count = 1 }));
        }

        [Fact]
        public void CountsSumAcrossCommits_AndClassPrefixIsStripped()
        {
            var a = Commit(new() { ["Sheets"] = 2, ["class:Foo"] = 1 }, ids: 3);
            var b = Commit(new() { ["Sheets"] = 4 }, new() { ["Viewports"] = 4 }, ids: 4);
            var s = CreatedSummary.From(new[] { a, b }, null, ReceiptLanguage.En)!;
            Assert.Equal("6 Sheets · 4 Viewports · 1 Foo", s.Headline(ReceiptLanguage.En));
        }

        [Fact]
        public void ZeroAndMissingCounts_GiveNull()
        {
            Assert.Null(CreatedSummary.From(new[] { Commit(null, ids: 2) }, null, ReceiptLanguage.En));
            Assert.Null(CreatedSummary.From(new[] { Commit(new() { ["Sheets"] = 0 }, ids: 1) }, null, ReceiptLanguage.En));
            Assert.Null(CreatedSummary.From(new Envelope[0], null, ReceiptLanguage.En));
        }

        [Fact]
        public void CopyText_IsTabSeparated_WithKnownNames()
        {
            var env = Commit(new() { ["Sheets"] = 2 }, new() { ["Viewports"] = 2 }, ids: 2,
                data: "{\"mode\":\"write\",\"created\":[{\"category\":\"Sheets\",\"name\":\"EL-101 - Level 1\"},{\"category\":\"Sheets\",\"name\":\"EL-102 - Level 2\"},{\"category\":\"Grids\",\"name\":\"A\"}]}");
            var text = CreatedSummary.From(new[] { env }, null, ReceiptLanguage.En)!.CopyText();
            Assert.Equal("Category\tCount\tNames\nSheets\t2\tEL-101 - Level 1; EL-102 - Level 2\nViewports\t2\t", text);
        }

        [Fact]
        public void CardModel_UsesTheCategoryHeadline_AndKeepsElementIdsForShowThem()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { Commit(new() { ["Sheets"] = 6 }, new() { ["Viewports"] = 6 }, ids: 6) });

            var m = ResultCardModel.From(ev, ReceiptLanguage.En, "brochure.pdf");

            Assert.Equal(ResultKind.Created, m.Kind);
            Assert.Equal("6 Sheets · 6 Viewports", m.Headline);
            Assert.Equal("Source: brochure.pdf", m.CreatedBy!.SourceLine(ReceiptLanguage.En));
            Assert.Equal(6, m.Created.Count);
            Assert.Equal(new long[] { 5000, 5001, 5002, 5003, 5004, 5005 }, m.Created.Select(c => c.Id));
        }

        [Fact]
        public void CardModel_WithoutCategoryCounts_KeepsTheOldHeadline()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { Commit(null, ids: 4) });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(ResultKind.Created, m.Kind);
            Assert.Null(m.CreatedBy);
            Assert.Equal("4 created", m.Headline);
        }

        // The pane code is not loadable in the harness: source-text checks of the wiring.
        [Fact]
        public void CardView_UsesTheCategoryBody_WithShowThemCopyListAndTheHeaderUndo()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            string Src(string rel) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, rel));
            Assert.Contains("CreatedCard.TryBuild(root, card, lang, actions)", Src("UI/Copilot/Controls/ResultCardView.cs"));
            Assert.Contains("\"Undo all\"", Src("UI/Copilot/Controls/ResultCardView.cs"));
            var body = Src("UI/Copilot/Controls/CreatedCard.cs");
            Assert.Contains("\"Show them\"", body);
            Assert.Contains("\"Copy list\"", body);
            Assert.Contains("actions.ShowElements(", body);
            Assert.Contains("by.CopyText()", body);
        }
    }
}
