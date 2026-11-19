#nullable enable
// r13 (0.0.147), issue 7 follow-up: find_elements rows of a door/window find grouped by type carry their type's Type Mark,
// Function and Fire Rating beside the instance mark. Optional cells only: absent when the type has no value, and a cell the
// row already holds (the queried Fire Rating) is never overwritten. The Revit read is in FindExecutor (read only).
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FindTypeCells0147Tests
    {
        private static FindPlan Plan(params string[] keys) =>
            new FindPlan { GroupKeys = keys.Select(k => new GroupBySpec { Kind = k }).ToList() };

        [Fact]
        public void OnlyADoorOrWindowFind_GroupedByType_GetsTheTypeCells()
        {
            Assert.True(TypeCells.Applies(Plan("type"), doorOrWindow: true));
            Assert.True(TypeCells.Applies(Plan("level", "type"), doorOrWindow: true));
            Assert.False(TypeCells.Applies(Plan("type"), doorOrWindow: false));     // walls by type: unchanged
            Assert.False(TypeCells.Applies(Plan("level"), doorOrWindow: true));
            Assert.False(TypeCells.Applies(Plan(), doorOrWindow: true));            // no group_by: unchanged
            Assert.Equal(new[] { "Type Mark", "Function", "Fire Rating" }, TypeCells.Names);
        }

        [Fact]
        public void Merge_AddsNonEmptyValues_AndKeepsTheRowsOwn()
        {
            var fields = new Dictionary<string, string?> { ["fire rating"] = "1 HOUR" };
            TypeCells.Merge(fields, new (string, string?)[] { ("Type Mark", "FD2"), ("Function", ""), ("Fire Rating", "2 HOUR") });
            Assert.Equal("FD2", fields["Type Mark"]);
            Assert.False(fields.ContainsKey("Function"));                           // the type has none: no key
            Assert.Equal("1 HOUR", fields["fire rating"]);                          // never overwritten
            Assert.False(fields.ContainsKey("Fire Rating"));
        }

        [Fact]
        public void TheRowsJson_KeepsMark_AndAddsTheTypeCellsAsOptionalKeys()
        {
            var row = new FoundRow { Ref = new BinaVibe.Copilot.Contracts.ElementRef(968434, null), Category = "Doors", Type = "(PTa1) 1000 x 2400 @FD2", Mark = "FD2" };
            TypeCells.Merge(row.Fields, new (string, string?)[] { ("Type Mark", "PTa1"), ("Function", "Interior"), ("Fire Rating", "1 HOUR") });
            var plan = Plan("type");
            plan.Queries.Add(new FindQuery { Index = 0, CategoryText = "Doors" });
            var outcome = new FindOutcome { Rows = new List<FoundRow> { row } };
            outcome.Queries.Add(new QueryOutcome { Count = 1, Filter = "Doors", Subject = "Doors" });
            var env = FindResultBuilder.Build(plan, outcome);
            var json = JsonSerializer.Serialize(env.Data);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement.GetProperty("rows")[0];
            Assert.Equal("FD2", r.GetProperty("mark").GetString());
            Assert.Equal("PTa1", r.GetProperty("Type Mark").GetString());
            Assert.Equal("Interior", r.GetProperty("Function").GetString());
            Assert.Equal("1 HOUR", r.GetProperty("Fire Rating").GetString());
        }
    }
}
