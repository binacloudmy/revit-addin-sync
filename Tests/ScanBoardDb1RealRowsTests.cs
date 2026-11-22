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

        private static List<SpecDedupe.Row> SpecRows(string category = "Doors")
        {
            var rows = new List<SpecDedupe.Row>(); var n = 0;
            foreach (var c in Load().Where(IsSpec))
            {
                n++;
                rows.Add(new SpecDedupe.Row
                {
                    Id = c.Id, CheckId = "Db.1", Source = SpecDedupe.FromModel, Tier = 1, Bucket = ScanBoard.Open, Category = category,
                    Params = SpecDedupe.ParamsOfActual(c.Actual),
                    ElementIds = Enumerable.Range(0, 3).Select(k => 100000L + n * 10 + k).ToArray(),   // instance ids
                });
            }
            return rows;
        }

        // The legacy row as the mapper builds it: rule "Missing Type Parameter: X" (the export's prettified title), Required = raw name.
        private static List<SpecDedupe.Row> LegacyRows(string checkId = "Db.1", string category = "", bool withElement = true)
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
                    Extra = checkId.Length == 0,
                });
            }
            return rows;
        }

        private static HashSet<string> SpecParams() => new HashSet<string>(SpecRows().SelectMany(r => r.Params).Select(SpecDedupe.ParamKey));

        [Fact]
        public void The_fixture_is_the_real_export_with_61_db1_rows_and_legacy_rows_for_the_same_parameters()
        {
            Assert.Equal(61, Load().Count(IsSpec));
            Assert.Equal(51, Load().Count(c => c.Title == "Missing Type Parameter: Bidang Kejuruteraan Jkr Stt"));
            Assert.Equal(26, Load().Count(c => c.Title == "Missing Type Parameter: Sistem Jkr Stt"));
        }

        [Theory]
        [InlineData("Db.1", "", true)]       // legacy row with a type id, no category
        [InlineData("Db.1", "Doors", true)]  // the same with the category
        [InlineData("", "", true)]           // no spec id on the row (the rule text still names the parameter)
        [InlineData("", "Doors", false)]     // no spec id and no element id at all
        [InlineData("Db.1", "", false)]
        public void Each_parameter_shows_once_a_legacy_row_for_a_parameter_a_db1_row_lists_is_dropped(string checkId, string category, bool withElement)
        {
            var spec = SpecParams();
            var kept = ScanBoard.Merge(LegacyRows(checkId, category, withElement), new SpecDedupe.Row[0], SpecRows());
            var keptLegacy = kept.Where(r => r.Source == SpecDedupe.FromBackend).ToList();
            Assert.DoesNotContain(keptLegacy, r => spec.Contains(SpecDedupe.ParamKey(r.Param)));
            Assert.Equal(61, kept.Count(r => r.Source == SpecDedupe.FromModel));   // every Db.1 spec row stays
            // What stays is the legacy rows whose parameter no Db.1 row lists (Komponen IBS / Bomba, Kemasan ...).
            var all = LegacyRows(checkId, category, withElement);
            Assert.Equal(all.Count(r => !spec.Contains(SpecDedupe.ParamKey(r.Param))), keptLegacy.Count);
            Assert.True(keptLegacy.Count > 0 && keptLegacy.Count < all.Count);
        }

        [Fact]
        public void The_named_parameters_of_the_report_are_gone_from_the_legacy_rows()
        {
            var kept = ScanBoard.Merge(LegacyRows(), new SpecDedupe.Row[0], SpecRows());
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
            Assert.False(SpecDedupe.RuleNamesParameter("Optional Type Parameter Missing: Kod Ruang Berkaitan Jkr Stt"));
        }

        [Fact]
        public void Board_counts_reconcile_after_the_merge()
        {
            var legacy = LegacyRows(); var spec = SpecRows();
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], spec);
            var t = ScanBoard.Count(kept);
            Assert.Equal(kept.Count, t.Total);
            Assert.Equal(t.Open, kept.Count);
            Assert.True(kept.Count < legacy.Count + spec.Count);
        }

        [Fact]
        public void A_legacy_row_in_another_category_is_not_merged()
        {
            var legacy = LegacyRows("Db.1", "Windows");   // the spec rows are Doors
            var kept = ScanBoard.Merge(legacy, new SpecDedupe.Row[0], SpecRows("Doors"));
            Assert.Equal(legacy.Count, kept.Count(r => r.Source == SpecDedupe.FromBackend));
        }

        [Fact]
        public void A_parameter_no_db1_row_lists_is_not_merged()
        {
            var legacy = LegacyRows();
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
