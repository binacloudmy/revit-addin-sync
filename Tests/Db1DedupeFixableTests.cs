using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-516: after AI-506b the real export still had 61 Db.1 spec rows next to 237 "Missing Type Parameter" and 72
    /// "Missing Instance Parameter" rows for the same parameters. Root cause: the backend gives most of those rows a
    /// set_parameter fix (it can infer the value), and a fixable row is never dropped (AI-479), so AI-506b left them.
    /// Now the fixable row stays (it holds the write Fix all makes) and the spec row gives the parameter up.
    /// The rows come from the real export (22/23 Nov round, LOD 300, trimmed: Db.1, Missing, Optional rows).
    /// The export has no element ids, categories or fix columns: the tests give the rows the shapes the live scan has.
    /// </summary>
    public class Db1DedupeFixableTests
    {
        private sealed class Csv { public string Id, Title, Required, Actual; }

        private static List<Csv> Load()
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "ai516_issues_export_board_status.csv"), Encoding.UTF8).TrimStart('﻿');
            var list = new List<Csv>();
            foreach (var c in Parse(text).Skip(1))
                if (c.Count >= 7) list.Add(new Csv { Id = c[0], Title = c[1], Required = c[5], Actual = c[6] });
            return list;
        }

        private static IEnumerable<List<string>> Parse(string text)
        {
            var row = new List<string>(); var cell = new StringBuilder(); var q = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (q) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(c); continue; }
                if (c == '"') q = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); yield return row; row = new List<string>(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
        }

        // The parameters bina-ai's _infer_param_value can give a value for (jkr_deterministic.py): their "Missing" rows carry a set_parameter fix.
        private static readonly HashSet<string> Inferable = new HashSet<string>(new[]
        {
            "Bidang_Kejuruteraan_jkr_stt", "Kod_Jenis_jkr_stt", "Sistem_jkr_stt", "Sub_Sistem_jkr_stt", "Kod_Komponen_jkr_stt",
            "Kod_DAK_Komponen_jkr_stt", "LOD_jkr_sit", "Nama_Komponen_Utama_jkr_stt", "Kaedah_Pemasangan_jkr_sit", "Bahan_jkr_stt",
        }.Select(SpecDedupe.ParamKey));

        private static List<SpecDedupe.Row> SpecRows()
        {
            var rows = new List<SpecDedupe.Row>(); var n = 0;
            foreach (var c in Load().Where(x => x.Id.StartsWith("SPEC-Db.1", StringComparison.Ordinal)))
            {
                n++;
                rows.Add(new SpecDedupe.Row
                {
                    Id = c.Id, CheckId = "Db.1", Source = SpecDedupe.FromModel, Bucket = ScanBoard.Open, Category = "Doors", TypeName = "T" + n,
                    Params = SpecDedupe.ParamsOfActual(c.Actual), Tag = c.Actual,
                    ElementIds = new long[] { 100000L + n * 10, 100000L + n * 10 + 1, 100000L + n * 10 + 2 },
                });
            }
            return rows;
        }

        /// <summary>The backend's rows. Each real "Missing ..." row is put on the representative instance of a Db.1 type that lists its parameter
        /// (round-robin); a parameter no Db.1 row lists gets an element of its own type. Fixable = the backend can infer the value.</summary>
        private static List<SpecDedupe.Row> LegacyRows(List<SpecDedupe.Row> spec, bool fixableByInference = true)
        {
            var rows = new List<SpecDedupe.Row>(); var next = new Dictionary<string, int>(); var n = 0;
            foreach (var c in Load().Where(x => x.Title.StartsWith("Missing ", StringComparison.Ordinal) || x.Title.StartsWith("Optional ", StringComparison.Ordinal)))
            {
                n++;
                var key = SpecDedupe.ParamKey(SpecDedupe.ParamOfRule(c.Title, c.Required));
                var owners = spec.Where(s => s.Params.Select(SpecDedupe.ParamKey).Contains(key)).ToList();
                var optional = c.Title.StartsWith("Optional ", StringComparison.Ordinal);
                var at = next.TryGetValue(key, out var k) ? k : 0; next[key] = at + 1;
                var owner = owners.Count > 0 ? owners[at % owners.Count] : null;
                var fix = fixableByInference && !optional && Inferable.Contains(key);
                var kind = SpecDedupe.ParamKindOfRule(c.Title);
                var id = owner != null ? owner.ElementIds.First() : 800000L + n;
                rows.Add(new SpecDedupe.Row
                {
                    Id = c.Id, CheckId = optional ? "" : "Db.1", Source = SpecDedupe.FromBackend, Bucket = ScanBoard.Open, Category = "Doors",
                    TypeName = owner?.TypeName ?? "Other", ElementId = id, Param = SpecDedupe.ParamOfRule(c.Title, c.Required),
                    ParamRule = SpecDedupe.RuleNamesParameter(c.Title), ParamKind = kind,
                    Fixable = fix, FixKey = fix ? "set_parameter|" + id + "|" + key : null,
                    // a type-parameter row stands for every instance of its type: give it the type's three ids
                    ElementIds = owner != null && kind == "type" ? owner.ElementIds : new[] { id },
                });
            }
            return rows;
        }

        private static List<SpecDedupe.Row> Merge(List<SpecDedupe.Row> legacy, List<SpecDedupe.Row> spec) =>
            ScanBoard.Merge(legacy, new SpecDedupe.Row[0], spec);

        // ── the real export ──

        [Fact]
        public void The_fixture_is_the_real_export_61_spec_rows_237_type_and_72_instance_rows()
        {
            var all = Load();
            Assert.Equal(61, all.Count(c => c.Id.StartsWith("SPEC-Db.1", StringComparison.Ordinal)));
            Assert.Equal(237, all.Count(c => c.Title.StartsWith("Missing Type Parameter", StringComparison.Ordinal)));
            Assert.Equal(72, all.Count(c => c.Title.StartsWith("Missing Instance Parameter", StringComparison.Ordinal)));
        }

        [Fact]
        public void Root_cause_numbers_most_legacy_rows_name_a_db1_parameter_and_are_fixable()
        {
            var spec = SpecRows(); var legacy = LegacyRows(spec).Where(r => r.CheckId == "Db.1").ToList();
            var listed = new HashSet<string>(spec.SelectMany(s => s.Params).Select(SpecDedupe.ParamKey));
            var covered = legacy.Where(r => listed.Contains(SpecDedupe.ParamKey(r.Param))).ToList();
            Assert.Equal(309, legacy.Count);
            Assert.Equal(293, covered.Count);                       // 293 of 309 restate a parameter a Db.1 row lists
            Assert.Equal(288, covered.Count(r => r.Fixable));       // 288 of them can carry a fix, so AI-506b kept every one
            Assert.Equal(5, covered.Count(r => !r.Fixable));        // only 5 were droppable by AI-506b
        }

        [Fact]
        public void No_type_parameter_is_reported_twice_across_db1_and_legacy_rows()
        {
            var spec = SpecRows(); var legacy = LegacyRows(spec);
            var kept = Merge(legacy, spec);
            var shownSpec = kept.Where(r => r.Source == SpecDedupe.FromModel).ToList();
            var twice = new List<string>();
            foreach (var l in kept.Where(r => r.Source == SpecDedupe.FromBackend && r.ParamRule))
            {
                var key = SpecDedupe.ParamKey(l.Param);
                foreach (var s in shownSpec.Where(s => s.ElementIds.Contains(l.ElementId)))
                    if (SpecDedupe.ParamsOfActual(SpecDedupe.ActualWithout((string)s.Tag, s.HeldParams)).Contains(key))
                        twice.Add(l.ParamKind + ":" + key);
            }
            // Type parameters (237 rows) and optional rows: never twice. What is left is INSTANCE-parameter rows only: the add-in sends ONE
            // representative element per (category, type, level), so such a row judged one instance while the spec row judged every instance
            // of the type. Hiding the spec row would hide the instances nobody else reports. See the PR (known gap).
            Assert.DoesNotContain(twice, t => !t.StartsWith("instance:", StringComparison.Ordinal));
            Assert.True(shownSpec.Count < spec.Count);                 // spec rows fully reported by fixable type rows are gone
            Assert.True(kept.Count < legacy.Count + spec.Count);
        }

        [Fact]
        public void Fix_all_count_is_unchanged_by_the_dedupe_and_the_board_counts_reconcile()
        {
            var spec = SpecRows(); var legacy = LegacyRows(spec);
            var before = ScanBoard.Count(legacy.Concat(spec));
            var after = ScanBoard.Count(Merge(legacy, spec));
            Assert.True(before.Fixable > 0);
            Assert.Equal(before.Fixable, after.Fixable);                           // every fixable row is kept: Fix all makes the same changes
            Assert.Equal(after.Open, Merge(legacy, spec).Count);
            Assert.Equal(after.Total, after.Open + after.Manual + after.Ignored + after.Resolved);
            Assert.True(after.Open < before.Open);
            Assert.Equal(0, after.Extra);                                           // no row fell outside the %
        }

        [Fact]
        public void A_legacy_row_for_a_parameter_no_db1_row_lists_is_a_distinct_problem_and_stays()
        {
            var spec = SpecRows(); var legacy = LegacyRows(spec);
            var listed = new HashSet<string>(spec.SelectMany(s => s.Params).Select(SpecDedupe.ParamKey));
            var distinct = legacy.Where(r => !listed.Contains(SpecDedupe.ParamKey(r.Param))).Select(r => r.Id).ToList();
            Assert.NotEmpty(distinct);
            var kept = Merge(legacy, spec).Select(r => r.Id).ToHashSet();
            Assert.All(distinct, id => Assert.Contains(id, kept));
        }

        [Fact]
        public void Optional_rows_for_a_parameter_a_db1_row_lists_show_once_the_others_stay()
        {
            var spec = SpecRows(); var legacy = LegacyRows(spec);
            var optional = legacy.Where(r => r.CheckId == "").ToList();
            var listed = new HashSet<string>(spec.SelectMany(s => s.Params).Select(SpecDedupe.ParamKey));
            var kept = Merge(legacy, spec).Where(r => r.Source == SpecDedupe.FromBackend && r.CheckId == "").ToList();
            Assert.DoesNotContain(kept, r => listed.Contains(SpecDedupe.ParamKey(r.Param)));      // Coping / Kumai: 48 rows go
            Assert.Equal(optional.Count(r => !listed.Contains(SpecDedupe.ParamKey(r.Param))), kept.Count);
            Assert.True(kept.Count > 0 && kept.Count < optional.Count);
        }

        // ── one parameter flagged by both sources ──

        private static SpecDedupe.Row Spec(string actual, string cat = "Doors", string type = "T1", params long[] ids) => new SpecDedupe.Row
        {
            Id = "SPEC-Db.1-x", CheckId = "Db.1", Source = SpecDedupe.FromModel, Bucket = ScanBoard.Open, Category = cat, TypeName = type, Tag = actual,
            Params = SpecDedupe.ParamsOfActual(actual), ElementIds = ids.Length > 0 ? ids : new long[] { 11, 12, 13 },
        };

        private static SpecDedupe.Row Legacy(string id, string rule, long element = 11, bool fixable = true, string cat = "Doors", string type = "T1") => new SpecDedupe.Row
        {
            Id = id, CheckId = "Db.1", Source = SpecDedupe.FromBackend, Bucket = ScanBoard.Open, Category = cat, TypeName = type, ElementId = element,
            Param = SpecDedupe.ParamOfRule(rule, ""), ParamRule = true, ParamKind = SpecDedupe.ParamKindOfRule(rule),
            Fixable = fixable, FixKey = fixable ? "set_parameter|" + element + "|" + rule : null,
        };

        [Fact]
        public void A_type_parameter_flagged_by_both_leaves_one_row_with_the_spec_id_and_the_fix()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Sistem_jkr_stt") }, new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt") });
            var one = Assert.Single(kept);
            Assert.Equal("L1", one.Id);
            Assert.Equal("Db.1", one.CheckId);           // the spec id: it counts in the board's Db.1 and not as "extra"
            Assert.True(one.Fixable);                    // and it keeps the set_parameter fix Fix all writes
            Assert.NotNull(one.FixKey);
        }

        [Fact]
        public void A_non_fixable_legacy_row_is_dropped_and_the_spec_row_wins()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Sistem_jkr_stt", fixable: false) }, new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt") });
            Assert.Equal("SPEC-Db.1-x", Assert.Single(kept).Id);
        }

        [Fact]
        public void A_spec_row_with_other_parameters_stays_without_the_one_a_fixable_row_reports()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Sistem_jkr_stt") },
                             new List<SpecDedupe.Row> { Spec("empty: Sistem_jkr_stt, Bahan_jkr_stt; missing: LOD_jkr_sit") });
            Assert.Equal(2, kept.Count);
            var s = kept.Single(r => r.Source == SpecDedupe.FromModel);
            Assert.Equal("empty: Bahan_jkr_stt; missing: LOD_jkr_sit", SpecDedupe.ActualWithout((string)s.Tag, s.HeldParams));
        }

        [Fact]
        public void A_different_parameter_is_not_merged()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Bahan_jkr_stt") }, new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt") });
            Assert.Equal(2, kept.Count);
            Assert.Empty(kept.Single(r => r.Source == SpecDedupe.FromModel).HeldParams);
        }

        [Fact]
        public void A_different_category_is_not_merged()
        {
            // not in the id list, other category
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Sistem_jkr_stt", element: 900, cat: "Windows") }, new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt") });
            Assert.Equal(2, kept.Count);
            Assert.Empty(kept.Single(r => r.Source == SpecDedupe.FromModel).HeldParams);
        }

        [Fact]
        public void An_instance_row_on_one_of_three_instances_does_not_hide_the_spec_row()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Instance parameter: LOD_jkr_sit", element: 11) }, new List<SpecDedupe.Row> { Spec("missing: LOD_jkr_sit") });
            Assert.Equal(2, kept.Count);   // the other two instances are only on the spec row
        }

        [Fact]
        public void Instance_rows_that_together_cover_every_instance_hold_the_spec_row()
        {
            var rows = new[] { 11L, 12L, 13L }.Select(e => Legacy("L" + e, "Missing Instance parameter: LOD_jkr_sit", element: e)).ToList();
            var kept = Merge(rows, new List<SpecDedupe.Row> { Spec("missing: LOD_jkr_sit") });
            Assert.Equal(3, kept.Count);
            Assert.All(kept, r => Assert.Equal(SpecDedupe.FromBackend, r.Source));
        }

        [Fact]
        public void A_known_instance_the_spec_row_lists_no_such_parameter_for_stays_beside_it()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Bahan_jkr_stt", element: 11) },
                             new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt", ids: new long[] { 11 }) });
            Assert.Equal(2, kept.Count);
        }

        [Fact]
        public void A_type_id_row_of_another_type_is_not_merged()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Sistem_jkr_stt", element: 900, type: "T2") }, new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt") });
            Assert.Equal(2, kept.Count);
        }

        [Fact]
        public void A_type_id_row_of_the_same_type_is_held_by_category_and_type_name()
        {
            var kept = Merge(new List<SpecDedupe.Row> { Legacy("L1", "Missing Type parameter: Sistem_jkr_stt", element: 900) }, new List<SpecDedupe.Row> { Spec("missing: Sistem_jkr_stt") });
            Assert.Equal("L1", Assert.Single(kept).Id);
        }

        [Fact]
        public void The_result_does_not_depend_on_row_order()
        {
            var spec = SpecRows(); var legacy = LegacyRows(spec);
            var a = Merge(legacy, spec).Select(r => r.Id).OrderBy(x => x).ToList();
            var legacy2 = LegacyRows(SpecRows()); legacy2.Reverse(); var spec2 = SpecRows(); spec2.Reverse();
            var b = Merge(legacy2, spec2).Select(r => r.Id).OrderBy(x => x).ToList();
            Assert.Equal(a, b);
        }

        // ── helpers ──

        [Theory]
        [InlineData("Missing Type parameter: X", "type")]
        [InlineData("Missing Instance parameter: X", "instance")]
        [InlineData("Empty Type parameter: X", "type")]
        [InlineData("Optional Instance parameter missing: X", "instance")]
        [InlineData("Naming Convention", "")]
        public void The_parameter_kind_comes_from_the_rule_text(string rule, string kind) => Assert.Equal(kind, SpecDedupe.ParamKindOfRule(rule));

        [Fact]
        public void ActualWithout_drops_named_parameters_and_empty_parts()
        {
            Assert.Equal("missing: B", SpecDedupe.ActualWithout("empty: A; missing: B", new[] { "A" }));
            Assert.Equal("empty: A b", SpecDedupe.ActualWithout("empty: A b, C; missing: D", new[] { "c", "d" }));
            Assert.Equal("", SpecDedupe.ActualWithout("missing: A", new[] { "a" }));
            Assert.Equal("missing: A", SpecDedupe.ActualWithout("missing: A", new string[0]));
            Assert.Equal("(none)", SpecDedupe.ActualWithout("(none)", new[] { "a" }));
        }
    }
}
