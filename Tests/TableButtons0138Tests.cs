#nullable enable
// UX review Q5 (18 Nov): "Export", "Open as grid" and "Copy table" appeared with no table, or a one-row table (above a question card, under a one-row
// write card). They are for a table: two rows or more. Visibility only.
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TableButtons0138Tests
    {
        private static ResultTable Table(int rows)
        {
            var t = new ResultTable { Title = "t", Columns = new List<string> { "a" } };
            for (var i = 0; i < rows; i++) t.Rows.Add(new List<string> { "r" + i });
            return t;
        }

        [Fact]
        public void ExportIsOfferedFromTwoRows()
        {
            Assert.False(EvidenceDedupe.WorthExporting(null));
            Assert.False(EvidenceDedupe.WorthExporting(Table(0)));
            Assert.False(EvidenceDedupe.WorthExporting(Table(1)));
            Assert.True(EvidenceDedupe.WorthExporting(Table(2)));
            Assert.True(EvidenceDedupe.WorthExporting(Table(40)));
        }

        [Fact]
        public void TheChangeCard_OffersGridAndCopy_OnlyForTwoRowsOrMore()
        {
            var one = new ResultCardModel { Kind = ResultKind.Changes };
            var g1 = new TypeGroup { TypeName = "Doors" }; g1.Rows.Add(new ChangeRow { Param = "Mark" }); one.Groups.Add(g1);
            Assert.False(ChangeTable.WorthATable(one));
            var two = new ResultCardModel { Kind = ResultKind.Changes };
            var g2 = new TypeGroup { TypeName = "Doors" }; g2.Rows.Add(new ChangeRow { Param = "Mark" }); g2.Rows.Add(new ChangeRow { Param = "Comments" }); two.Groups.Add(g2);
            Assert.True(ChangeTable.WorthATable(two));
            Assert.False(ChangeTable.WorthATable(null));
        }

        [Fact]
        public void TheViews_AskTheRules()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.Contains("Where(EvidenceDedupe.WorthExporting)", File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/ResultCardView.cs")));
            Assert.Contains("if (ChangeTable.WorthATable(card))", File.ReadAllText(Path.Combine(dir.FullName, "UI/Copilot/Controls/ChangesBody.cs")));
        }
    }
}
