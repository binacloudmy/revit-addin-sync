// read_schedule / write_schedule decision logic. The Revit half (Schedules.cs)
// needs a live Document and is not linked into this project, which is why
// ScheduleLogic.cs was split out in the first place.
//
// What is pinned here is the part that can be wrong in an expensive way:
//   - a malformed update must be REPORTED, never silently dropped (a swallowed
//     update reads to the drafter as a write that landed)
//   - a field the schedule does not have must be rejected before it reaches
//     some same-named parameter elsewhere in the model
//   - a grouped / non-itemized schedule must NOT claim one row = one element,
//     or "place one device per row" places the wrong count

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ScheduleToolsTests
    {
        private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

        // ── ParseUpdates ─────────────────────────────────────────────────

        [Fact]
        public void ParseUpdates_reads_id_field_and_value()
        {
            var (ok, bad) = ScheduleLogic.ParseUpdates(Json(
                @"{""updates"":[{""element_id"":384512,""field"":""Comments"",""value"":""GPO-01""}]}"));

            Assert.Empty(bad);
            var u = Assert.Single(ok);
            Assert.Equal(384512, u.ElementId);
            Assert.Equal("Comments", u.Field);
            Assert.Equal("GPO-01", u.Value);
        }

        [Fact]
        public void ParseUpdates_accepts_a_string_element_id()
        {
            // Models emit ids as strings often enough that rejecting them would
            // cost a retry for nothing.
            var (ok, bad) = ScheduleLogic.ParseUpdates(Json(
                @"{""updates"":[{""element_id"":""384512"",""field"":""Mark"",""value"":1}]}"));

            Assert.Empty(bad);
            Assert.Equal(384512, Assert.Single(ok).ElementId);
        }

        [Fact]
        public void ParseUpdates_accepts_param_as_an_alias_for_field()
        {
            var (ok, _) = ScheduleLogic.ParseUpdates(Json(
                @"{""updates"":[{""element_id"":1,""param"":""Mark"",""value"":""A""}]}"));
            Assert.Equal("Mark", Assert.Single(ok).Field);
        }

        [Fact]
        public void ParseUpdates_reports_malformed_entries_instead_of_dropping_them()
        {
            var (ok, bad) = ScheduleLogic.ParseUpdates(Json(
                @"{""updates"":[
                    {""element_id"":1,""field"":""Comments"",""value"":""keep""},
                    {""field"":""Comments"",""value"":""no id""},
                    {""element_id"":2,""value"":""no field""},
                    ""not an object""
                ]}"));

            Assert.Single(ok);
            Assert.Equal(3, bad.Count);
            // The index is what lets the agent say WHICH update it got wrong.
            Assert.Equal(new object?[] { 1, 2, 3 }, bad.Select(b => b["index"]).ToArray());
        }

        [Fact]
        public void ParseUpdates_on_a_missing_updates_array_yields_nothing()
        {
            var (ok, bad) = ScheduleLogic.ParseUpdates(Json(@"{""name"":""Door Schedule""}"));
            Assert.Empty(ok);
            Assert.Empty(bad);
        }

        [Fact]
        public void ParseUpdates_keeps_a_null_value()
        {
            // Clearing a cell is a legitimate edit; null must survive parsing
            // rather than being read as "no value given".
            var (ok, bad) = ScheduleLogic.ParseUpdates(Json(
                @"{""updates"":[{""element_id"":1,""field"":""Comments"",""value"":null}]}"));
            Assert.Empty(bad);
            Assert.Null(Assert.Single(ok).Value);
        }

        // ── ValidateFields ───────────────────────────────────────────────

        private static List<ScheduleUpdate> Ups(params string[] fields) =>
            fields.Select((f, i) => new ScheduleUpdate { ElementId = i + 1, Field = f }).ToList();

        [Fact]
        public void ValidateFields_rejects_a_column_the_schedule_does_not_have()
        {
            var (ok, rejected) = ScheduleLogic.ValidateFields(
                Ups("Comments", "Fire Rating"), new[] { "Mark", "Comments" });

            Assert.Equal("Comments", Assert.Single(ok).Field);
            Assert.Equal("Fire Rating", Assert.Single(rejected)["field"]);
        }

        [Fact]
        public void ValidateFields_matches_field_names_case_insensitively()
        {
            var (ok, rejected) = ScheduleLogic.ValidateFields(
                Ups("comments"), new[] { "Comments" });
            Assert.Single(ok);
            Assert.Empty(rejected);
        }

        [Fact]
        public void ValidateFields_without_a_schedule_validates_nothing()
        {
            // name is optional on write_schedule; with no schedule named there
            // is no field list to check against, and the addin must not invent
            // one.
            var (ok, rejected) = ScheduleLogic.ValidateFields(Ups("Anything"), null);
            Assert.Single(ok);
            Assert.Empty(rejected);
        }

        // ── RowWindow ────────────────────────────────────────────────────

        [Fact]
        public void RowWindow_skips_the_header_row()
        {
            // Body row 0 is the header; data is rows 1..n.
            var (start, count, total, truncated) = ScheduleLogic.RowWindow(bodyRowCount: 11, maxRows: 200);
            Assert.Equal(1, start);
            Assert.Equal(10, count);
            Assert.Equal(10, total);
            Assert.False(truncated);
        }

        [Fact]
        public void RowWindow_caps_and_flags_truncation()
        {
            var (_, count, total, truncated) = ScheduleLogic.RowWindow(bodyRowCount: 4001, maxRows: 200);
            Assert.Equal(200, count);
            Assert.Equal(4000, total);   // total stays honest about what was cut
            Assert.True(truncated);
        }

        [Fact]
        public void RowWindow_treats_non_positive_max_as_uncapped()
        {
            // The Excel export path wants every row.
            var (_, count, total, truncated) = ScheduleLogic.RowWindow(bodyRowCount: 4001, maxRows: 0);
            Assert.Equal(4000, count);
            Assert.Equal(4000, total);
            Assert.False(truncated);
        }

        [Fact]
        public void RowWindow_survives_an_empty_schedule()
        {
            var (_, count, total, truncated) = ScheduleLogic.RowWindow(bodyRowCount: 0, maxRows: 200);
            Assert.Equal(0, count);
            Assert.Equal(0, total);
            Assert.False(truncated);
        }

        // ── RowMapping ───────────────────────────────────────────────────

        [Fact]
        public void RowMapping_is_one_to_one_only_when_everything_lines_up()
        {
            var (verdict, note) = ScheduleLogic.RowMapping(
                isItemized: true, hasGroupHeadersOrFooters: false, showsGrandTotal: false,
                dataRowCount: 12, elementCount: 12);

            Assert.Equal(ScheduleLogic.MappingOneToOne, verdict);
            Assert.Null(note);
        }

        [Fact]
        public void RowMapping_flags_a_non_itemized_schedule()
        {
            // Itemize-off collapses every instance of a type into one row —
            // "one device per row" would place a fraction of the real count.
            var (verdict, note) = ScheduleLogic.RowMapping(
                isItemized: false, hasGroupHeadersOrFooters: false, showsGrandTotal: false,
                dataRowCount: 3, elementCount: 47);

            Assert.Equal(ScheduleLogic.MappingAmbiguous, verdict);
            Assert.Contains("not itemized", note);
            Assert.Contains("elements[]", note);
        }

        [Fact]
        public void RowMapping_flags_group_headers_and_grand_totals()
        {
            var (verdict, note) = ScheduleLogic.RowMapping(
                isItemized: true, hasGroupHeadersOrFooters: true, showsGrandTotal: true,
                dataRowCount: 15, elementCount: 12);

            Assert.Equal(ScheduleLogic.MappingAmbiguous, verdict);
            Assert.Contains("group headers", note);
            Assert.Contains("grand total", note);
        }

        [Fact]
        public void RowMapping_flags_a_count_mismatch_with_no_other_explanation()
        {
            // Nothing about the definition explains it, but the numbers still
            // disagree — say so rather than assert a mapping that is not there.
            var (verdict, note) = ScheduleLogic.RowMapping(
                isItemized: true, hasGroupHeadersOrFooters: false, showsGrandTotal: false,
                dataRowCount: 12, elementCount: 9);

            Assert.Equal(ScheduleLogic.MappingAmbiguous, verdict);
            Assert.Contains("12 data rows vs 9 scheduled elements", note);
        }
    }
}
