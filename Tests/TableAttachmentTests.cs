#nullable enable
// W5 Task 4: Excel/CSV in as attachments[kind=table] (protocol 3), and a
// result table back out. The parsing core is Revit-free and ClosedXML-free
// (TableAttachment); the .xlsx adapter (TableXlsx) only turns a worksheet into
// the same grid. Element ids and values keep their meaning: numbers stay
// numbers, "2700 mm" and "007" stay text, empty cells are null.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TableAttachmentTests
    {
        [Fact]
        public void ParsesHeaderAndRows()
        {
            var csv =
                "Element Id,Mark,\"Height Offset, from level\",Fire Rated\r\n" +
                "312045,D-01,2700 mm,true\r\n" +
                "312048,007,,FALSE\r\n" +
                "312051,\"D-\"\"03\"\"\",2400,\r\n" +
                "\r\n";

            var t = TableAttachment.ParseCsv("doors.csv", csv);

            Assert.Equal(new[] { "Element Id", "Mark", "Height Offset, from level", "Fire Rated" }, t.Columns);
            Assert.Equal(3, t.Rows.Count);                       // the blank trailing line is not a row
            Assert.Equal(312045L, t.Rows[0][0]);
            Assert.Equal("D-01", t.Rows[0][1]);
            Assert.Equal("2700 mm", t.Rows[0][2]);               // values with units stay text
            Assert.Equal(true, t.Rows[0][3]);
            Assert.Equal("007", t.Rows[1][1]);                   // leading zeros stay text
            Assert.Null(t.Rows[1][2]);                           // empty cell is null
            Assert.Equal(false, t.Rows[1][3]);
            Assert.Equal("D-\"03\"", t.Rows[2][1]);              // quoted quotes
            Assert.Equal(2400L, t.Rows[2][2]);
            Assert.Null(t.Rows[2][3]);                           // short row padded
            Assert.False(t.Truncated);

            var a = TableAttachment.ToAttachment(t);
            Assert.Equal(AttachmentKinds.Table, a.Kind);
            Assert.Equal("doors.csv", a.Name);
            Assert.Equal(t.Columns, a.Columns);
            Assert.Equal(3, a.Rows!.Count);
            Assert.All(a.Rows, r => Assert.Equal(4, r.Count));   // one cell per column
            Assert.Equal(JsonValueKind.Number, a.Rows[0][0].ValueKind);
            Assert.Equal(JsonValueKind.String, a.Rows[0][2].ValueKind);
            Assert.Equal(JsonValueKind.True, a.Rows[0][3].ValueKind);
            Assert.Equal(JsonValueKind.Null, a.Rows[1][2].ValueKind);
        }

        [Fact]
        public void HeaderGapsAndDuplicatesGetNames_GridSkipsLeadingBlankRows()
        {
            var grid = new List<List<object?>>
            {
                new List<object?> { null, null, null },
                new List<object?> { "Mark", "", "Mark" },
                new List<object?> { "A", 1.0, "B" },
                new List<object?> { "C", 2.5, null },
            };

            var t = TableAttachment.FromGrid("sheet.xlsx", grid);

            Assert.Equal(new[] { "Mark", "Column 2", "Mark (2)" }, t.Columns);
            Assert.Equal(2, t.Rows.Count);
            Assert.Equal(1L, t.Rows[0][1]);                      // whole doubles become integers
            Assert.Equal(2.5, t.Rows[1][1]);
        }

        [Fact]
        public void CapsRowsAndColumns_AndSaysSo()
        {
            var header = string.Join(",", Enumerable.Range(1, TableAttachment.MaxColumns + 5).Select(i => "c" + i));
            var rows = Enumerable.Range(1, TableAttachment.MaxRows + 10).Select(i => i + ",x");
            var t = TableAttachment.ParseCsv("big.csv", header + "\n" + string.Join("\n", rows));

            Assert.Equal(TableAttachment.MaxColumns, t.Columns.Count);
            Assert.Equal(TableAttachment.MaxRows, t.Rows.Count);
            Assert.Equal(TableAttachment.MaxRows + 10, t.TotalRows);
            Assert.True(t.Truncated);
        }

        [Fact]
        public void ResultTableToCsv_QuotesWhatNeedsIt()
        {
            var table = new ResultTable
            {
                Title = "34 ceilings",
                Columns = new List<string> { "id", "type", "note" },
                Rows = new List<List<string>>
                {
                    new List<string> { "312045", "CL-02A 600x600 Grid", "a, b" },
                    new List<string> { "312048", "CL-20 \"Plasterboard\"", "" },
                },
            };

            var csv = TableAttachment.ToCsv(table);

            Assert.Equal("id,type,note\r\n312045,CL-02A 600x600 Grid,\"a, b\"\r\n312048,\"CL-20 \"\"Plasterboard\"\"\",\r\n", csv);
        }

        [Theory]
        [InlineData("C:\\a\\doors.csv", true)]
        [InlineData("C:\\a\\doors.XLSX", true)]
        [InlineData("C:\\a\\notes.txt", false)]
        [InlineData("C:\\a\\plan.dwg", false)]
        public void IsTable(string path, bool expected) => Assert.Equal(expected, TableAttachment.IsTable(path));
    }
}
