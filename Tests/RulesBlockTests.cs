#nullable enable
// The "Rules checked" block and the single Export button (user feedback on 0.0.103: no raw dev dump under a result).
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RulesBlockTests
    {
        private static ResultTable Check(params (string Id, string Cite, string Sev, int Pass, int Fail, int Nv, string Why, string? Handle)[] rules)
        {
            var t = new ResultTable
            {
                Kind = ResultTable.CheckKind, Title = "Check model-health 2026.10.0",
                Columns = { "Rule", "Cite", "Severity", "Pass", "Fail", "Not verified", "Why not verified" },
            };
            foreach (var r in rules)
            {
                t.Rows.Add(new System.Collections.Generic.List<string> { r.Id, r.Cite, r.Sev, r.Pass.ToString(), r.Fail.ToString(), r.Nv.ToString(), r.Why });
                t.RowHandles.Add(r.Handle);
            }
            return t;
        }

        private static readonly ResultTable Sample = Check(
            ("MH-01", "Warnings in the model", "warning", 0, 3, 0, "", "h1"),
            ("MH-02", "Revit Review Warnings, the full list kept whole so it can wrap on two lines", "error", 0, 11, 0, "", "h2"),
            ("MH-03", "Rooms placed", "info", 12, 0, 0, "", null),
            ("MH-04", "Rooms enclosed", "info", 12, 0, 0, "", null),
            ("MH-05", "Doors tagged", "info", 0, 0, 5, "regex did not compile", null),
            ("MH-06", "Levels named", "info", 4, 0, 0, "", null));

        [Fact]
        public void Rows_ReadTheCheckTable_InOrder_WithTheirHandles()
        {
            var rows = RulesBlockText.Rows(new[] { Sample });
            Assert.Equal(6, rows.Count);
            Assert.Equal("MH-02", rows[1].Id);
            Assert.Equal(11, rows[1].Fail);
            Assert.Equal("h2", rows[1].Handle);
            Assert.Contains("kept whole so it can wrap", rows[1].Name);   // never cut with an ellipsis
            Assert.Equal(5, rows[4].NotVerified);
        }

        [Fact]
        public void OnlyCheckTablesCount()
        {
            var plain = new ResultTable { Kind = "", Title = "Doors" };
            Assert.Empty(RulesBlockText.Rows(new[] { plain }));
            Assert.Empty(RulesBlockText.Rows(null));
        }

        [Fact]
        public void Counts_FailedNotVerifiedPassed()
        {
            var rows = RulesBlockText.Rows(new[] { Sample });
            Assert.Equal(2, RulesBlockText.Failed(rows));
            Assert.Equal(1, RulesBlockText.NotVerified(rows));
            Assert.Equal(3, RulesBlockText.Passed(rows));
            Assert.True(RulesBlockText.IsOpen(rows[4]));
            Assert.False(RulesBlockText.IsOpen(rows[2]));
        }

        [Fact]
        public void Title_SaysWhatNeedsALook_NotTheRuleSetVersion()
        {
            var rows = RulesBlockText.Rows(new[] { Sample });
            Assert.Equal("Rules checked · 2 failed", RulesBlockText.Title(rows, ms: false));
            Assert.Equal("Peraturan disemak · 2 gagal", RulesBlockText.Title(rows, ms: true));
            var onlyUnverified = RulesBlockText.Rows(new[] { Check(("A", "x", "info", 1, 0, 2, "", null)) });
            Assert.Equal("Rules checked · 1 not verified", RulesBlockText.Title(onlyUnverified, false));
            var allPass = RulesBlockText.Rows(new[] { Check(("A", "x", "info", 4, 0, 0, "", null)) });
            Assert.Equal("Rules checked · all passed", RulesBlockText.Title(allPass, false));
            Assert.DoesNotContain("2026", RulesBlockText.Title(rows, false));
        }

        [Fact]
        public void Chips_AndThePassedLine()
        {
            Assert.Equal("11 fail", RulesBlockText.FailChip(11, false));
            Assert.Equal("11 gagal", RulesBlockText.FailChip(11, true));
            Assert.Equal("5 not verified", RulesBlockText.NotVerifiedChip(5, false));
            Assert.Equal("3 rules passed", RulesBlockText.PassedLine(3, false));
            Assert.Equal("1 rule passed", RulesBlockText.PassedLine(1, false));
            Assert.Equal("3 peraturan lulus", RulesBlockText.PassedLine(3, true));
            Assert.Equal("Export", RulesBlockText.ExportLabel(false));
            Assert.Equal("Eksport", RulesBlockText.ExportLabel(true));
        }

        [Fact]
        public void AnErrorSeverityRule_IsRed_TheRestAmber()
        {
            var rows = RulesBlockText.Rows(new[] { Sample });
            Assert.False(RulesBlockText.IsError(rows[0]));
            Assert.True(RulesBlockText.IsError(rows[1]));
        }

        // ---- the views ----

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        [Fact]
        public void TheEvidenceView_HasNoDetailsExpander_NoExportPills_AndNoLinePerRule()
        {
            var s = Src("UI/Copilot/Controls/TurnEvidenceView.cs");
            Assert.DoesNotContain("new Expander", s.Split("if (ev.HasWrites && !hasCard)")[0]);
            Assert.DoesNotContain("EvidenceDedupe.ExportLabels(", s);
            Assert.DoesNotContain("ReceiptTemplates.CheckRuleLine(", s);
            Assert.DoesNotContain("rules passed (all rules in the Excel export)", s);
            Assert.Contains("RulesBlock.Build(", s);
        }

        [Fact]
        public void TheRulesBlock_IsInsideTheCard_AndExportIsOneGhostButtonInTheActionRow()
        {
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("if (actions.RulesBlock != null) root.Children.Add(actions.RulesBlock);", s);
            Assert.Contains("var export = ExportButton(actions, ms);", s);
            Assert.Contains("CardKit.Ghost(RulesBlockText.ExportLabel(ms), () => actions.Export!(only), CardKit.Icons.Grid)", s);
        }

        [Fact]
        public void TheRulesBlock_UsesTheThreeButtons_AndNoHexAndNoPill()
        {
            var s = Src("UI/Copilot/Controls/RulesBlock.cs");
            Assert.Contains("CardKit.Ghost(", s);
            Assert.DoesNotContain("CardKit.Chip(", s);
            Assert.DoesNotContain("\"#", s);
            Assert.Contains("Cp.AmberBg", s);
            Assert.Contains("Cp.IssueBg", s);
        }

        [Fact]
        public void AFailingRuleIsNotDrawnTwice_TheFindingsListSkipsRuleFindingsWhenTheBlockIsThere()
        {
            Assert.Contains("f.FromRule && actions.RulesBlock != null", Src("UI/Copilot/Controls/FindingsBody.cs"));
            Assert.Contains("FromRule = true", Src("UI/Copilot/Model/ResultCardModel.cs"));
        }

        [Fact]
        public void NoCallerUsesTheOldPill_AnywhereInThePane()
        {
            var hits = Directory.GetFiles(Path.Combine(Root(), "UI"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("CardKit.cs"))
                .SelectMany(f => File.ReadAllLines(f).Where(l => l.Contains("CardKit.Chip(")).Select(l => Path.GetFileName(f) + ": " + l.Trim()))
                .ToList();
            Assert.Single(hits);   // the "type . n" filter pill only
        }
    }
}
