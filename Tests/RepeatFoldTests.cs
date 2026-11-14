#nullable enable
// Repeated messages in result rows fold into one row with a count (and one Show over the union of the ids):
// the pure grouping (RepeatFold), its count words, the findings the card model folds from repeated warnings,
// the not-done item rows, and the views that draw them.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RepeatFoldTests
    {
        private const ReceiptLanguage En = ReceiptLanguage.En;
        private const ReceiptLanguage Ms = ReceiptLanguage.Ms;
        private const string Groups = "Changes to groups are allowed only in group edit mode";

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static List<RepeatGroup<(string Text, long[] Ids)>> Fold(params (string Text, long[] Ids)[] rows) =>
            RepeatFold.Fold(rows, r => r.Text, r => r.Ids);

        [Fact]
        public void IdenticalMessages_FoldIntoOneGroupWithTheirCount()
        {
            var rows = Enumerable.Range(0, 20).Select(i => (Text: Groups, Ids: new long[] { 100 + i, 200 + i })).ToArray();
            var g = Assert.Single(Fold(rows));
            Assert.Equal(20, g.Count);
            Assert.True(g.IsFolded);
            Assert.Equal(40, g.Ids.Count);
        }

        [Fact]
        public void NearIdenticalMessages_DifferingInWhitespaceOrCase_FoldTogether_AndTheFirstWordingWins()
        {
            var g = Assert.Single(Fold(
                (Groups, new long[0]),
                ("  " + Groups.ToUpperInvariant() + "  ", new long[0]),
                (Groups.Replace(" ", "  \t "), new long[0]),
                ("\n" + Groups + "\r\n", new long[0])));
            Assert.Equal(4, g.Count);
            Assert.Equal(Groups, g.First.Text);
        }

        [Fact]
        public void DifferentMessages_StayApart_InFirstSeenOrder()
        {
            var g = Fold(("B message", new long[0]), ("A message", new long[0]), ("B message", new long[0]), ("C message", new long[0]), ("A message", new long[0]));
            Assert.Equal(new[] { "B message", "A message", "C message" }, g.Select(x => x.First.Text).ToArray());
            Assert.Equal(new[] { 2, 2, 1 }, g.Select(x => x.Count).ToArray());
        }

        [Fact]
        public void TheIdsOfAGroup_AreTheirUnion_EachOnce_InFirstSeenOrder()
        {
            var g = Assert.Single(Fold(("m", new long[] { 5, 3 }), ("m", new long[] { 3, 9 }), (" M ", new long[] { 5, 1 })));
            Assert.Equal(new long[] { 5, 3, 9, 1 }, g.Ids.ToArray());
        }

        [Fact]
        public void ACountOfOne_IsNotFolded()
        {
            var g = Assert.Single(Fold(("only once", new long[] { 7 })));
            Assert.False(g.IsFolded);
            Assert.Equal(1, g.Count);
        }

        [Fact]
        public void BlankMessages_NeverFold()
        {
            var g = Fold(("", new long[0]), ("   ", new long[0]), ("", new long[0]));
            Assert.Equal(3, g.Count);
            Assert.All(g, x => Assert.False(x.IsFolded));
            Assert.Equal("", RepeatFold.Key(null));
        }

        [Fact]
        public void TheKey_TrimsCollapsesAndLowers()
        {
            Assert.Equal("a b c", RepeatFold.Key("  A \t B\r\n  c "));
            Assert.Equal(RepeatFold.Key(Groups), RepeatFold.Key("  " + Groups.ToUpperInvariant()));
        }

        [Fact]
        public void TheCountChip_IsXN_WithoutIds_AndElementsWithIds_InEnglishAndMalay()
        {
            Assert.Equal("x20", RepeatFold.CountLabel(20, 0, En));
            Assert.Equal("x20", RepeatFold.CountLabel(20, 0, Ms));
            Assert.Equal("20 elements", RepeatFold.CountLabel(20, 20, En));
            Assert.Equal("1 element", RepeatFold.CountLabel(2, 1, En));
            Assert.Equal("20 elemen", RepeatFold.CountLabel(20, 20, Ms));
            Assert.Equal("1 elemen", RepeatFold.CountLabel(2, 1, Ms));

            var g = Assert.Single(Fold(("m", new long[] { 1, 2 }), ("m", new long[] { 2, 3 })));
            Assert.Equal("3 elements", RepeatFold.CountLabel(g, En));
        }

        // ---- warnings folded into findings by the card model ----

        private static Finding Warn(string text, string severity, params long[] ids) =>
            new Finding { Title = text, Severity = severity, Ids = ids.ToList(), Fixable = severity == WarningSeverity.Error };

        [Fact]
        public void TwentyRepeatedWarnings_BecomeOneFindingWithTheUnionOfTheirIds()
        {
            var list = Enumerable.Range(0, 20).Select(i => Warn(Groups, WarningSeverity.Error, 1000 + i, 2000 + i)).ToList();
            list.Add(Warn("Outside any room", WarningSeverity.Warning, 7));

            var folded = ResultCardModel.FoldRepeatedFindings(list);

            Assert.Equal(2, folded.Count);
            Assert.Equal(Groups, folded[0].Title);
            Assert.Equal(20, folded[0].Repeats);
            Assert.Equal(40, folded[0].Ids.Count);
            Assert.True(folded[0].Fixable);
            Assert.Equal(1, folded[1].Repeats);
            Assert.Equal("40 elements", ItemsBody.FindingRepeatLabel(folded[0], En));
            Assert.Equal("", ItemsBody.FindingRepeatLabel(folded[1], En));
        }

        [Fact]
        public void RepeatedWarningsWithoutIds_ShowXN()
        {
            var folded = ResultCardModel.FoldRepeatedFindings(Enumerable.Range(0, 5).Select(_ => Warn("Same", "warning")).ToList());
            var f = Assert.Single(folded);
            Assert.Equal("x5", ItemsBody.FindingRepeatLabel(f, En));
        }

        [Fact]
        public void WarningsOfADifferentSeverityOrDefinition_StaySeparate()
        {
            var a = Warn("Same text", WarningSeverity.Error, 1);
            var b = Warn("Same text", WarningSeverity.Warning, 2);
            var c = Warn("Same text", WarningSeverity.Error, 3); c.DefinitionId = "guid-1";
            var d = Warn("Same text", WarningSeverity.Error, 4); d.DefinitionId = "guid-1";
            var folded = ResultCardModel.FoldRepeatedFindings(new List<Finding> { a, b, c, d });
            Assert.Equal(3, folded.Count);
            Assert.Equal(2, folded[2].Repeats);
            Assert.Equal("guid-1", folded[2].DefinitionId);
        }

        [Fact]
        public void RuleFindings_AreNeverFolded_AndKeepTheirFlags()
        {
            var r1 = new Finding { Title = "Rule A", Handle = "h1", FromRule = true };
            var r2 = new Finding { Title = "Rule A", Handle = "h2", FromRule = true };
            var folded = ResultCardModel.FoldRepeatedFindings(new List<Finding> { r1, r2 });
            Assert.Equal(2, folded.Count);
            Assert.All(folded, f => Assert.True(f.FromRule));
            Assert.Equal(new[] { "h1", "h2" }, folded.Select(f => f.Handle).ToArray());
        }

        // ---- not-done item rows ----

        [Fact]
        public void OnlyANotDoneRow_HasAMessageToFoldOn()
        {
            var notDone = new ItemRow { Kind = "row", Title = "Level 1" };
            notDone.Cells.Add(("state", Groups));
            var sheet = new ItemRow { Kind = "sheet", Title = "S" };
            sheet.Cells.Add(("state", Groups));
            Assert.Equal(Groups, ItemsBody.FoldMessage(notDone, En));
            Assert.Equal("", ItemsBody.FoldMessage(sheet, En));
            Assert.Equal("", ItemsBody.FoldMessage(new ItemRow { Kind = "row", Title = "x" }, En));
        }

        [Fact]
        public void TwentyNotDoneRowsWithOneMessage_FoldIntoOneUnit_WithTheUnionOfTheirIds()
        {
            var rows = Enumerable.Range(0, 20).Select(i =>
            {
                var r = new ItemRow { Kind = "row", Title = "Group " + i, Ids = new List<long> { 10 + i } };
                r.Cells.Add(("state", Groups));
                return r;
            }).ToList();
            var units = RepeatFold.Fold(rows, r => ItemsBody.FoldMessage(r, En), r => r.Ids);
            var g = Assert.Single(units);
            Assert.Equal("20 elements", RepeatFold.CountLabel(g, En));
            Assert.Equal(20, g.Ids.Count);
        }

        [Fact]
        public void TheShowAllLabel_CountsUnitsWhenGiven()
        {
            var s = new ItemSection { Name = "", Rows = Enumerable.Range(0, 40).Select(i => new ItemRow { Kind = "row", Title = "r" + i }).ToList() };
            Assert.Equal("Show all 30 rows", ItemsBody.ShowAllLabel(s, En));
            Assert.Equal("Show all 12 rows", ItemsBody.ShowAllLabel(s, En, 12));
        }

        // ---- the views ----

        [Fact]
        public void TheViews_UseTheFoldAndTheBoardsChip()
        {
            var items = Src("UI/Copilot/Controls/ItemsCard.cs");
            Assert.Contains("RepeatFold.Fold(section.Rows", items);
            Assert.Contains("RepeatRows.Item(", items);
            Assert.Contains("new Thickness(14, 6, 14, 6), table ? 36 : row.Count != null ? 42 : 40", items);   // the plain row is unchanged

            var find = Src("UI/Copilot/Controls/FindingsBody.cs");
            Assert.Contains("ItemsBody.FindingRepeatLabel(f, lang)", find);
            Assert.Contains("RepeatRows.CountChip(", find);

            var rows = Src("UI/Copilot/Controls/RepeatRows.cs");
            Assert.Contains("RepeatFold.CountLabel(g, lang)", rows);
            Assert.Contains("mono: true", rows);
            Assert.Contains("\"Cp.Sunken\"", rows);
            Assert.Contains("actions.Rows?.Link(line, Highlights.MarkFocus.ForElements(message, g.Ids))", rows);   // step 2: the row is the button
            Assert.DoesNotContain("\"Show\"", rows);
            Assert.Contains("\"Cp.AmberFg\"", rows);
            Assert.DoesNotContain("CardKit.Chip(", rows);

            var model = Src("UI/Copilot/Model/ResultCardModel.cs");
            Assert.Contains("m.Findings = FoldRepeatedFindings(m.Findings);", model);
        }

        [Fact]
        public void TheNewViews_UseNoColourLiterals()
        {
            var hex = new Regex("\"#[0-9a-fA-F]{3,8}\"");
            foreach (var f in new[] { "RepeatRows", "SheetPreviewRow" })
                Assert.False(hex.IsMatch(Src("UI/Copilot/Controls/" + f + ".cs")), f + " has a hex colour literal");
        }
    }
}
