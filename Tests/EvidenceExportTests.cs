#nullable enable
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>One result, one "Export to Excel" chip per distinct table (live test 0.0.93: the same rows twice).</summary>
    public class EvidenceExportTests
    {
        private static ResultTable T(string title, int rows, params string[] cols)
        {
            var t = new ResultTable { Title = title, Columns = cols.Length == 0 ? new List<string> { "id", "Level" } : cols.ToList() };
            for (var i = 1; i <= rows; i++) t.Rows.Add(t.Columns.Select(c => c + i).ToList());
            return t;
        }

        [Fact]
        public void ReadTwice_CappedThenWhole_IsOneChip_TheFullerTable()
        {
            var capped = T("Found 12 doors (first 5)", 5);
            var whole = T("Found 12 doors", 12);

            var chips = EvidenceDedupe.ExportTables(new[] { capped, whole });

            Assert.Same(whole, Assert.Single(chips));
        }

        [Fact]
        public void SameRowsUnderTwoTitles_IsOneChip_TheFirst()
        {
            var a = T("Doors on Level 1", 4);
            var b = T("Found 4 doors", 4);

            Assert.Same(a, Assert.Single(EvidenceDedupe.ExportTables(new[] { a, b })));
        }

        [Fact]
        public void TwoReallyDifferentTables_KeepTwoChips_WithTitles()
        {
            var doors = T("Doors", 3);
            var rooms = T("Rooms", 3, "number", "name");

            var chips = EvidenceDedupe.ExportTables(new[] { doors, rooms });

            Assert.Equal(2, chips.Count);
            Assert.Equal(new[] { "Export to Excel · Doors", "Export to Excel · Rooms" }, EvidenceDedupe.ExportLabels(chips, ms: false));
        }

        [Fact]
        public void SameColumns_DifferentRows_AreNotMerged()
        {
            var l1 = T("Doors L1", 3);
            var l2 = T("Doors L2", 3);
            l2.Rows[0][0] = "other";

            Assert.Equal(2, EvidenceDedupe.ExportTables(new[] { l1, l2 }).Count);
        }

        [Fact]
        public void CheckTable_IsNeverMergedIntoAReadTable()
        {
            var read = T("Found 3", 3, "Rule", "Cite");
            var check = T("Check model-health", 3, "Rule", "Cite");
            check.Kind = ResultTable.CheckKind;

            Assert.Equal(2, EvidenceDedupe.ExportTables(new[] { read, check }).Count);
        }

        [Fact]
        public void SingleTable_IsLabelledPlainly_AndInMalay()
        {
            var one = new[] { T("Found 12 doors", 12) };

            Assert.Equal("Export to Excel", Assert.Single(EvidenceDedupe.ExportLabels(one, ms: false)));
            Assert.Equal("Eksport ke Excel", Assert.Single(EvidenceDedupe.ExportLabels(one, ms: true)));
        }

        [Fact]
        public void SameTitleTwice_GetsNumbers_AndEmptyTitleGetsItsPosition()
        {
            var a = T("Doors", 3); var b = T("Doors", 3); b.Rows[0][0] = "x";
            var c = T("", 2, "q");

            var labels = EvidenceDedupe.ExportLabels(new[] { a, b, c }, ms: false);

            Assert.Equal(new[] { "Export to Excel · Doors (1)", "Export to Excel · Doors (2)", "Export to Excel · 3" }, labels);
        }

        [Fact]
        public void DuplicateEvidenceIsDroppedBeforeLabelling_SoTheChipCountMatchesTheTables()
        {
            // The turn's reply has a table and a read returned the same table: one chip.
            var read = T("Found 3 doors", 3);
            var again = T("Found 3 doors", 3);

            var chips = EvidenceDedupe.ExportTables(new[] { read, again });

            Assert.Single(chips);
            Assert.Single(EvidenceDedupe.ExportLabels(chips, ms: false));
        }
    }
}
