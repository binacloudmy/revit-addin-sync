#nullable enable
// W3 live-test round (ruling R86; LIVE-C-triage L15, L16, L17, L22). What a
// code preview tells the confirm card:
//   - L15: real field/before/after rows for the existing elements it edits
//     (from values read in the preview and again after its rollback), and
//     data.created = [{category, name}] for named elements it adds;
//   - L16: an addition the document's own collector cannot see is something
//     Revit created along (internal pieces of a viewport, sketch planes…): a
//     side effect, not touched, so the card counts what the code made;
//   - L17: a type-parameter preview names how many instances it affects;
//   - L22: deleting a view that is open closes its tab even in a preview: the
//     preview says so.
//
// Revit-free: compiles in Tests.csproj and runs in the scratch harness.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class PreviewDetailTests
    {
        private static ValueCell C(string name, string storage, string display, bool readOnly = false) =>
            new ValueCell(name + "#1", name, storage, display, readOnly);

        [Fact]
        public void EditedValuesBecomeFieldRowsWithDisplayValues()
        {
            var before = new Dictionary<long, IReadOnlyList<ValueCell>>
            {
                [101] = new[] { C("Mark", "s:101", "101"), C("Comments", "s:", ""), C("Area", "d:1", "1 m²", readOnly: true) },
                [102] = new[] { C("Mark", "s:102", "102") },
            };
            var after = new Dictionary<long, IReadOnlyList<ValueCell>>
            {
                [101] = new[] { C("Mark", "s:D-01-01", "D-01-01"), C("Comments", "s:", ""), C("Area", "d:2", "2 m²", readOnly: true) },
                [102] = new[] { C("Mark", "s:102", "102") },   // unchanged: no row
            };

            var rows = ValueRows.Diff(before, after);

            var row = Assert.Single(rows);   // read-only recompute and unchanged values give no row
            Assert.Equal(101, row.Id);
            Assert.Equal("Mark", row.Field);
            Assert.Equal("101", row.Before);
            Assert.Equal("D-01-01", row.After);
        }

        [Fact]
        public void RowsAreCappedAtFiftyInIdOrder()
        {
            var before = new Dictionary<long, IReadOnlyList<ValueCell>>();
            var after = new Dictionary<long, IReadOnlyList<ValueCell>>();
            for (var i = 80; i > 0; i--)
            {
                before[i] = new[] { C("Mark", "s:" + i, i.ToString()) };
                after[i] = new[] { C("Mark", "s:X" + i, "X" + i) };
            }
            var rows = ValueRows.Diff(before, after);
            Assert.Equal(EnvelopeLimits.MaxRows, rows.Count);
            Assert.Equal(1, rows[0].Id);
            Assert.Null(ValueRows.Diff(before, new Dictionary<long, IReadOnlyList<ValueCell>>()).FirstOrDefault());
        }

        [Fact]
        public void CreatedNamedElementsAreAddedToTheEnvelopeData()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Summary = "Preview.",
                Data = JsonDocument.Parse("{\"mode\":\"write\",\"output\":\"\"}").RootElement.Clone(),
            };
            var created = Enumerable.Range(1, 60).Select(i => new CreatedItem(1000 + i, i <= 3 ? "Sheets" : "Views", "Elevation " + i)).ToList();

            EnvelopeChanges.SetCreated(env, created);

            var data = env.Data!.Value;
            Assert.Equal("write", data.GetProperty("mode").GetString());   // the tool's data is kept
            var list = data.GetProperty("created");
            Assert.Equal(EnvelopeLimits.MaxRows, list.GetArrayLength());
            Assert.Equal("Sheets", list[0].GetProperty("category").GetString());
            Assert.Equal("Elevation 1", list[0].GetProperty("name").GetString());
            Assert.Equal(1001, list[0].GetProperty("id").GetInt64());
            Assert.Equal(60, data.GetProperty("created_count").GetInt32());
        }

        [Fact]
        public void AdditionsTheCollectorCannotSeeAreCreatedAlongNotTouched()
        {
            // D-01: the code placed 3 views on sheets; the recorder also saw 6 internal Viewports-category
            // pieces that no document collector returns. They are side effects, so the card counts 3.
            var added = new long[] { 1, 2, 3, 11, 12, 13, 14, 15, 16 };
            var visible = new HashSet<long> { 1, 2, 3 };
            var touched = ChangeClassifier.CodeTouched(added, modified: new long[] { 40 }, deleted: new long[] { 50 }, visibleAdded: visible);

            Assert.Equal(new long[] { 1, 2, 3, 40, 50 }, touched.OrderBy(i => i));
        }

        [Fact]
        public void ATypeParameterPreviewNamesTheInstancesItAffects()
        {
            Assert.Equal(" Type parameter: changes every instance of that type (8 Doors in the model).",
                SetParametersText.TypeNote(types: 1, instances: 8, category: "Doors"));
            Assert.Equal(" Type parameter: changes every instance of those types (12 in the model).",
                SetParametersText.TypeNote(types: 2, instances: 12, category: null));
            Assert.Equal("", SetParametersText.TypeNote(types: 0, instances: 0, category: null));
        }

        [Fact]
        public void DeletingOpenViewsSaysTheirTabsClosed()
        {
            var warning = EnvelopeChanges.ClosedTabsWarning(new[] { "3D Perspective", "Level 2 - Working" });
            Assert.Contains("2 open view", warning);
            Assert.Contains("3D Perspective", warning);
            Assert.Contains("closes", warning);
            Assert.Null(EnvelopeChanges.ClosedTabsWarning(new string[0]));
        }
    }
}
