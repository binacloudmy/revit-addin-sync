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
    /// AI-506b: a Db.1 spec row and the backend's "Missing Type Parameter: X" rows say the same thing and must show once.
    /// The rows come from the Revit Tester's real export (LOD 300, 22 Nov round b, trimmed): 61 Db.1 rows next to
    /// legacy rows for the same parameters. The export has no element ids, so the tests give the rows ids of the
    /// two kinds the live scan can have: instance ids on the spec rows, a TYPE id (never in a spec row's list) on the legacy rows.
    /// </summary>
    public class ScanBoardDb1RealRowsTests
    {
        private static string DataFile() => Path.Combine(AppContext.BaseDirectory, "data", "ai506b_issues_export_lod300.csv");

        private sealed class Csv { public string Id, Title, Category, Priority, Status, Required, Actual; }

        private static List<Csv> Load()
        {
            var list = new List<Csv>();
            var text = File.ReadAllText(DataFile(), Encoding.UTF8).TrimStart('﻿');
            foreach (var cells in Parse(text).Skip(1))
                if (cells.Count >= 7)
                    list.Add(new Csv { Id = cells[0], Title = cells[1], Category = cells[2], Priority = cells[3], Status = cells[4], Required = cells[5], Actual = cells[6] });
            return list;
        }

        // Minimal RFC 4180 reader: quoted cells, doubled quotes, commas inside quotes.
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

        private static bool IsSpec(Csv c) => c.Id.StartsWith("SPEC-Db.1", StringComparison.Ordinal);
        private static bool IsLegacyParam(Csv c) => c.Title.StartsWith("Missing ", StringComparison.Ordinal);

        private static List<SpecDedupe.Row> SpecRows(string category = "Doors", bool capped = false)
        {
            var rows = new List<SpecDedupe.Row>(); var n = 0;
            foreach (var c in Load().Where(IsSpec))
            {
                n++;
                rows.Add(new SpecDedupe.Row
                {
                    Id = c.Id, CheckId = "Db.1", Source = SpecDedupe.FromModel, Tier = 1, Bucket = ScanBoard.Open, Category = category, TypeName = "T" + n, Family = "F" + n,
                    Params = SpecDedupe.ParamsOfActual(c.Actual),
                    ElementIds = Enumerable.Range(0, capped ? SpecDedupe.InstanceIdCap : 3).Select(k => 100000L + n * 10000 + k).ToArray(),   // instance ids
                });
            }
            return rows;
        }

        // The legacy row as the mapper builds it: rule "Missing Type Parameter: X" (the export's prettified title), Required = raw name.
        private static List<SpecDedupe.Row> LegacyRows(string checkId = "Db.1", string category = "Doors", bool withElement = true, bool typeName = false)
        {
            var rows = new List<SpecDedupe.Row>(); var n = 0;
            foreach (var c in Load().Where(IsLegacyParam))
            {
                n++;
                rows.Add(new SpecDedupe.Row
                {
                    Id = c.Id, CheckId = checkId, Source = SpecDedupe.FromBackend, Tier = 1, Bucket = ScanBoard.Open, Category = category,
                    Param = SpecDedupe.ParamOfRule(c.Title, c.Required), ParamRule = SpecDedupe.RuleNamesParameter(c.Title),
                    ElementId = withElement ? 900000L + n : 0,   // a type id: in no spec row's instance list
                    // (b) the type name of a type a Db.1 row lists this parameter for (the export has no type names: assigned here)
                    TypeName = typeName ? TypeOfFirstSpecRowListing(SpecDedupe.ParamKey(SpecDedupe.ParamOfRule(c.Title, c.Required))) : "",
                    Family = typeName ? TypeOfFirstSpecRowListing(SpecDedupe.ParamKey(SpecDedupe.ParamOfRule(c.Title, c.Required))).Replace("T", "F") : "",
                    Extra = checkId.Length == 0,
                });
            }
            return rows;
        }

        private static string TypeOfFirstSpecRowListing(string key) =>
            SpecRows().FirstOrDefault(r => r.Params.Select(SpecDedupe.ParamKey).Contains(key))?.TypeName ?? "";

        private static HashSet<string> SpecParams() => new HashSet<string>(SpecRows().SelectMany(r => r.Params).Select(SpecDedupe.ParamKey));

        [Fact]
        public void The_fixture_is_the_real_export_with_61_db1_rows_and_legacy_rows_for_the_same_parameters()
        {
            Assert.Equal(61, Load().Count(IsSpec));
            Assert.Equal(51, Load().Count(c => c.Title == "Missing Type Parameter: Bidang Kejuruteraan Jkr Stt"));
            Assert.Equal(26, Load().Count(c => c.Title == "Missing Type Parameter: Sistem Jkr Stt"));
        }

        // The three cases the fallback may hide a row in: no element (a), a type id whose type name a Db.1 row lists (b), id lists cut at the cap (c).
        [Theory]
        [InlineData("a", "Db.1")]
        [InlineData("a", "")]       // no spec id on the row (the rule text still names the parameter)
        [InlineData("b", "Db.1")]
        [InlineData("b", "")]
        [InlineData("c", "Db.1")]
        public void Each_parameter_shows_once_a_legacy_row_for_a_parameter_a_db1_row_lists_is_dropped(string kind, string checkId)
        {
            var spec = SpecParams();
            var specRows = SpecRows(capped: kind == "c");
            var legacy = LegacyRows(checkId, withElement: kind != "a", typeName: kind == "b");
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], specRows);
            var keptLegacy = kept.Where(r => r.Source == SpecDedupe.FromBackend).ToList();
            Assert.DoesNotContain(keptLegacy, r => spec.Contains(SpecDedupe.ParamKey(r.Param)));
            Assert.Equal(61, kept.Count(r => r.Source == SpecDedupe.FromModel));   // every Db.1 spec row stays
            // What stays is the legacy rows whose parameter no Db.1 row lists (Komponen IBS / Bomba, Kemasan ...).
            Assert.Equal(legacy.Count(r => !spec.Contains(SpecDedupe.ParamKey(r.Param))), keptLegacy.Count);
            Assert.True(keptLegacy.Count > 0 && keptLegacy.Count < legacy.Count);
        }

        [Fact]
        public void A_row_without_a_category_is_kept_even_when_a_spec_row_lists_the_parameter()
        {
            var legacy = LegacyRows(category: "", withElement: false);   // even with no element at all
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], SpecRows("Doors", capped: true));
            Assert.Equal(legacy.Count, kept.Count(r => r.Source == SpecDedupe.FromBackend));
        }

        [Fact]
        public void A_type_id_row_on_another_type_of_the_same_category_stays()
        {
            // Two types of Doors; the spec row flags type A for the parameter; the legacy row is on type B (its id is listed nowhere).
            var spec = new SpecDedupe.Row { Id = "s", CheckId = "Db.1", Source = SpecDedupe.FromModel, Category = "Doors", TypeName = "A", Family = "FA",
                Params = SpecDedupe.ParamsOfActual("tiada: Sistem_jkr_stt"), ElementIds = new long[] { 1, 2, 3 } };
            SpecDedupe.Row Legacy(string id, string type) => new SpecDedupe.Row { Id = id, CheckId = "Db.1", Source = SpecDedupe.FromBackend, Bucket = ScanBoard.Open,
                Category = "Doors", TypeName = type, Family = "FA", ElementId = 777, ParamRule = true, Param = "Sistem Jkr Stt" };
            var kept = ScanBoard.Merge(new[] { Legacy("onB", "B"), Legacy("onA", "A") }, new SpecDedupe.Row[0], new[] { spec }).Select(r => r.Id).ToList();
            Assert.Contains("onB", kept);
            Assert.DoesNotContain("onA", kept);   // the same type: restated
        }

        [Fact]
        public void The_named_parameters_of_the_report_are_gone_from_the_legacy_rows()
        {
            var kept = ScanBoard.Merge(LegacyRows(withElement: false), new SpecDedupe.Row[0], SpecRows());
            var left = kept.Where(r => r.Source == SpecDedupe.FromBackend).Select(r => SpecDedupe.ParamKey(r.Param)).ToList();
            foreach (var p in new[] { "Bidang Kejuruteraan Jkr Stt", "Nama Komponen Utama Jkr Stt", "Kod DAK Komponen Jkr Stt", "Sistem Jkr Stt", "Sub Sistem Jkr Stt", "LOD Jkr Sit" })
                Assert.DoesNotContain(SpecDedupe.ParamKey(p), left);
        }

        [Fact]
        public void The_pretty_title_and_the_raw_name_are_the_same_parameter()
        {
            Assert.Equal(SpecDedupe.ParamKey("Bidang_Kejuruteraan_jkr_stt"),
                SpecDedupe.ParamKey(SpecDedupe.ParamOfRule("Missing Type Parameter: Bidang Kejuruteraan Jkr Stt", "")));
            Assert.Equal("Bidang_Kejuruteraan_jkr_stt", SpecDedupe.ParamOfRule("Something else", "Bidang_Kejuruteraan_jkr_stt"));
            Assert.True(SpecDedupe.RuleNamesParameter("Missing Instance Parameter: LOD Jkr Sit"));
            Assert.True(SpecDedupe.RuleNamesParameter("Optional Type Parameter Missing: Kod Ruang Berkaitan Jkr Stt"));   // AI-516: it names a parameter too
            Assert.False(SpecDedupe.RuleNamesParameter("Naming Convention"));
        }

        [Fact]
        public void Board_counts_reconcile_after_the_merge()
        {
            var legacy = LegacyRows(withElement: false); var spec = SpecRows();
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], spec);
            var t = ScanBoard.Count(kept);
            Assert.Equal(kept.Count, t.Total);
            Assert.Equal(t.Open, kept.Count);
            Assert.True(kept.Count < legacy.Count + spec.Count);
        }

        [Fact]
        public void A_legacy_row_in_another_category_is_not_merged()
        {
            var legacy = LegacyRows("Db.1", "Windows", withElement: false);   // the spec rows are Doors
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], SpecRows("Doors"));
            Assert.Equal(legacy.Count, kept.Count(r => r.Source == SpecDedupe.FromBackend));
        }

        [Fact]
        public void A_parameter_no_db1_row_lists_is_not_merged()
        {
            var legacy = LegacyRows(withElement: false);
            var spec = SpecRows();
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], spec.Select(r => { r.Params = r.Params.Where(p => !p.StartsWith("sistem_")).ToArray(); return r; }).ToList());
            Assert.Equal(26, kept.Count(r => r.Source == SpecDedupe.FromBackend && SpecDedupe.ParamKey(r.Param) == "sistem_jkr_stt"));
        }

        [Fact]
        public void A_fixable_legacy_row_is_never_dropped()
        {
            var legacy = LegacyRows(); foreach (var r in legacy) r.Fixable = true;
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], SpecRows());
            Assert.Equal(legacy.Count, kept.Count(r => r.Source == SpecDedupe.FromBackend));
        }

        [Fact]
        public void A_known_instance_the_db1_row_does_not_list_the_parameter_for_stays()
        {
            var spec = SpecRows();
            var known = spec[0].ElementIds.First();
            var row = new SpecDedupe.Row { Id = "x", CheckId = "Db.1", Source = SpecDedupe.FromBackend, Bucket = ScanBoard.Open, ElementId = known, ParamRule = true, Param = "Panjang_jkr_stl", Category = "Doors" };
            // spec[0] lists no Panjang parameter, but another spec row does: the id says this instance is judged and fine.
            Assert.DoesNotContain("panjang_jkr_stl", spec[0].Params.Select(SpecDedupe.ParamKey));
            Assert.Contains("panjang_jkr_stl", spec.SelectMany(r => r.Params).Select(SpecDedupe.ParamKey));
            Assert.Contains(ScanBoard.Merge(new[] { row }, new SpecDedupe.Row[0], spec), r => r.Id == "x");
        }
    }
}
